using MailWarden.Core.Accounts;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Commands;
using MailWarden.Infrastructure.Storage;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Background;

/// <summary>
/// Long-running service that keeps all accounts of one data folder current: one loop per account runs cycles (queued
/// commands, then a sync pass) on a schedule, after triggers and after IDLE changes of the inbox; one IDLE watcher per
/// account reports inbox changes. Holds the <see cref="DataFolderLock"/> while running, unless the composition already
/// holds it and passes it in.
/// </summary>
/// <remarks>
/// <para>
/// Per-account loop: a cycle at start, then wait for the first of: the scheduled time (<see cref="AccountSyncStatus.NextRunAt"/>),
/// a trigger (<see cref="TriggerAccount"/>, an IDLE change or <see cref="RetryAccount"/>) followed by
/// <see cref="MailboxServiceOptions.TriggerDebounce"/>, or stop. Triggers set a flag that the next cycle clears when it
/// starts, so any number of triggers during a cycle or a debounce causes exactly one more cycle. In
/// <see cref="AccountSyncState.WaitingAfterError"/> and <see cref="AccountSyncState.NeedsSignIn"/> only
/// <see cref="RetryAccount"/> ends the wait early; a trigger is kept and served by the next cycle.
/// </para>
/// <para>
/// Concurrency: <see cref="StartAsync"/>, <see cref="StopAsync"/> and <see cref="AccountsChangedAsync"/> are
/// serialised. Status changes are stored under a lock and <see cref="StatusChanged"/> is raised after the lock is
/// released, always from the account's loop (a thread-pool thread), so the events of one account arrive in order.
/// An exception of a subscriber is ignored (see <see cref="StatusChanged"/>). Subscribers must not block on
/// <see cref="StopAsync"/> inside the handler (the stop waits for the loop that raises the event).
/// </para>
/// </remarks>
public sealed class MailboxService : IAsyncDisposable
{
    private const int MaxBackoffMinutes = 30;
    private const int BackoffSteps = 6;

    private readonly DataPaths paths;
    private readonly IAccountStore accounts;
    private readonly Func<Guid, CancellationToken, Task<CommandRunReport>> executeCommands;
    private readonly Func<Guid, IProgress<SyncProgress>?, CancellationToken, Task<SyncReport>> syncAccount;
    private readonly IMailIdleWatcherFactory idleWatchers;
    private readonly MailboxServiceOptions options;
    private readonly IServiceDelay delay;
    private readonly TimeProvider timeProvider;

    // Lock taken by the composition before opening the database; the service then neither acquires nor releases one.
    private readonly DataFolderLock? heldLock;

    // Serialises start, stop and account changes. Never disposed: it holds no wait handle (AvailableWaitHandle unused).
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    // Guards workers and statuses; never held while raising StatusChanged or awaiting.
    private readonly Lock stateGate = new();
    private readonly Dictionary<Guid, AccountWorker> workers = [];
    private readonly Dictionary<Guid, AccountSyncStatus> statuses = [];
    private readonly List<Guid> statusOrder = [];

    // Lifecycle state; written only under lifecycleGate.
    private ServiceState state = ServiceState.NotStarted;
    private CancellationTokenSource? serviceStop;
    private DataFolderLock? folderLock;

    /// <summary>Creates the service; nothing runs before <see cref="StartAsync"/>.</summary>
    /// <param name="paths">Data folder whose lock the service holds while running.</param>
    /// <param name="accounts">Account store; the accounts to keep current are read from here.</param>
    /// <param name="executeCommands">Runs the queued commands of an account (<c>CommandExecutor.ExecutePendingAsync</c>).</param>
    /// <param name="syncAccount">Runs one sync pass of an account (<c>SyncEngine.SyncAccountAsync</c>).</param>
    /// <param name="idleWatchers">Opens the inbox watcher of an account.</param>
    /// <param name="options">Poll interval, IDLE renewal and trigger debounce.</param>
    /// <param name="delay">Every wait of the service goes through it.</param>
    /// <param name="timeProvider">Clock for <see cref="AccountSyncStatus.NextRunAt"/> and <see cref="AccountSyncStatus.LastSuccessAt"/>.</param>
    /// <param name="heldLock">
    /// Lock the caller already holds and keeps owning. When given, the service neither acquires nor releases the lock;
    /// when null, the service acquires it in <see cref="StartAsync"/> and releases it in <see cref="StopAsync"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">A parameter other than <paramref name="heldLock"/> is null.</exception>
    public MailboxService(
        DataPaths paths,
        IAccountStore accounts,
        Func<Guid, CancellationToken, Task<CommandRunReport>> executeCommands,
        Func<Guid, IProgress<SyncProgress>?, CancellationToken, Task<SyncReport>> syncAccount,
        IMailIdleWatcherFactory idleWatchers,
        MailboxServiceOptions options,
        IServiceDelay delay,
        TimeProvider timeProvider,
        DataFolderLock? heldLock = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.executeCommands = executeCommands ?? throw new ArgumentNullException(nameof(executeCommands));
        this.syncAccount = syncAccount ?? throw new ArgumentNullException(nameof(syncAccount));
        this.idleWatchers = idleWatchers ?? throw new ArgumentNullException(nameof(idleWatchers));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.heldLock = heldLock;
    }

    /// <summary>
    /// Raised on a thread-pool thread whenever the status of an account changes (also for every progress report of a
    /// running cycle). Never raised while the service holds a lock.
    /// </summary>
    /// <remarks>
    /// An exception thrown by a subscriber is caught and ignored, per subscriber: the UI must not be able to stop an
    /// account loop or keep other subscribers from being called. Subscribers therefore handle and report their own
    /// errors (AP-04 logs them as critical). Do not block on <see cref="StopAsync"/> inside a handler.
    /// </remarks>
    public event EventHandler<AccountSyncStatus>? StatusChanged;

    private enum ServiceState
    {
        NotStarted,
        Running,
        Stopped,
    }

    private enum WatcherEnd
    {
        NotSupported,
        SignInRequired,
        ConnectionFailed,
    }

    /// <summary>
    /// Snapshot of the status of every account the service has run, in start order. Removed accounts keep their last
    /// (<see cref="AccountSyncState.Stopped"/>) status.
    /// </summary>
    public IReadOnlyList<AccountSyncStatus> CurrentStatus
    {
        get
        {
            lock (stateGate)
            {
                return statusOrder.Select(accountId => statuses[accountId]).ToList();
            }
        }
    }

    /// <summary>
    /// Acquires the data-folder lock (D1), unless the constructor received a held lock, reads the accounts and starts one
    /// loop + one IDLE watcher per account.
    /// </summary>
    /// <param name="ct">Cancels acquiring and reading the accounts; does not stop the started loops.</param>
    /// <returns>A task that completes when all loops are started (not when their first cycle ends).</returns>
    /// <exception cref="InvalidOperationException">
    /// The data folder is in use by another process or instance ("data folder is in use"), or the service was started
    /// before. Nothing is started then.
    /// </exception>
    /// <exception cref="OperationCanceledException">The start was cancelled; the lock is released again.</exception>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (state != ServiceState.NotStarted)
            {
                throw new InvalidOperationException("The mailbox service was already started; create a new instance.");
            }

            // Lock first: a second process must fail before any account is read or any loop runs. A lock held by the
            // composition stays with its owner.
            DataFolderLock? acquiredLock = heldLock is not null
                ? null
                : DataFolderLock.TryAcquire(paths)
                    ?? throw new InvalidOperationException("The data folder is in use by another MailWarden instance.");
            try
            {
                IReadOnlyList<Account> accountList = await accounts.ListAsync(ct).ConfigureAwait(false);
                var stopSource = new CancellationTokenSource();
                serviceStop = stopSource;
                folderLock = acquiredLock;
                state = ServiceState.Running;
                foreach (Account account in accountList)
                {
                    StartWorker(account, stopSource.Token);
                }
            }
            catch
            {
                acquiredLock?.Dispose();
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Cancels all loops and watchers, waits for them (running cycles see cancellation), releases the lock. Idempotent;
    /// does nothing when the service was never started.
    /// </summary>
    /// <returns>A task that completes when every loop has ended and the lock is released.</returns>
    /// <exception cref="AggregateException">
    /// A loop ended with an unexpected failure (programming error) or a cancellation callback threw; the service is
    /// stopped anyway and the lock is released after every loop ended.
    /// </exception>
    public async Task StopAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (state != ServiceState.Running)
            {
                return;
            }

            state = ServiceState.Stopped;
            AccountWorker[] runningWorkers;
            lock (stateGate)
            {
                runningWorkers = [.. workers.Values];
                workers.Clear();
            }

            // A throwing cancellation callback must not skip the wait: the lock is released only after all loops ended.
            List<Exception> failures = [];
            try
            {
                await CancelCollectingAsync(serviceStop!.CancelAsync, failures).ConfigureAwait(false);
                foreach (AccountWorker worker in runningWorkers)
                {
                    await WaitForWorkerAsync(worker, failures).ConfigureAwait(false);
                }
            }
            finally
            {
                serviceStop!.Dispose();
                serviceStop = null;
                folderLock?.Dispose();
                folderLock = null;
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("Stopping the account loops failed (cancellation or an unexpected loop failure).", failures);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Triggers a cycle for the account soon (after <see cref="MailboxServiceOptions.TriggerDebounce"/>); used after
    /// enqueueing commands and by "refresh". Triggers coalesce. While the account waits after an error or needs a
    /// sign-in, the trigger is kept for the next cycle but does not start one (see <see cref="RetryAccount"/>). Unknown
    /// accounts and a service that is not running are ignored (an account may have just been removed).
    /// </summary>
    /// <param name="accountId">Account to refresh.</param>
    public void TriggerAccount(Guid accountId)
    {
        FindWorker(accountId)?.CycleTrigger.Set();
    }

    /// <summary>
    /// Leaves <see cref="AccountSyncState.NeedsSignIn"/> / <see cref="AccountSyncState.WaitingAfterError"/> and runs a
    /// cycle soon (after <see cref="MailboxServiceOptions.TriggerDebounce"/>); also restarts an IDLE watcher stopped
    /// because a sign-in is needed. Unknown accounts and a service that is not running are ignored.
    /// </summary>
    /// <param name="accountId">Account to retry.</param>
    public void RetryAccount(Guid accountId)
    {
        AccountWorker? worker = FindWorker(accountId);
        worker?.Retry.Set();
        worker?.WatcherRetry.Set();
    }

    /// <summary>
    /// Re-reads the account list: starts loops for new accounts, stops loops of removed ones (their last status is
    /// <see cref="AccountSyncState.Stopped"/>) and waits until they ended. Accounts that stay keep running untouched.
    /// </summary>
    /// <param name="ct">Cancels reading the account list.</param>
    /// <returns>A task that completes when new loops are started and removed loops have ended.</returns>
    /// <exception cref="InvalidOperationException">The service is not running.</exception>
    /// <exception cref="AggregateException">
    /// A removed loop ended with an unexpected failure (programming error) or cancelling it threw; the method still waits
    /// for every removed loop before it throws.
    /// </exception>
    public async Task AccountsChangedAsync(CancellationToken ct = default)
    {
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (state != ServiceState.Running)
            {
                throw new InvalidOperationException("The mailbox service is not running.");
            }

            IReadOnlyList<Account> accountList = await accounts.ListAsync(ct).ConfigureAwait(false);
            var currentIds = accountList.Select(account => account.Id).ToHashSet();
            List<AccountWorker> removedWorkers = [];
            List<Account> addedAccounts;
            lock (stateGate)
            {
                foreach (AccountWorker worker in workers.Values.Where(worker => !currentIds.Contains(worker.Account.Id)))
                {
                    removedWorkers.Add(worker);
                }

                foreach (AccountWorker worker in removedWorkers)
                {
                    workers.Remove(worker.Account.Id);
                }

                addedAccounts = [.. accountList.Where(account => !workers.ContainsKey(account.Id))];
            }

            foreach (Account account in addedAccounts)
            {
                StartWorker(account, serviceStop!.Token);
            }

            List<Exception> failures = [];
            foreach (AccountWorker worker in removedWorkers)
            {
                await CancelCollectingAsync(worker.CancelAsync, failures).ConfigureAwait(false);
                await WaitForWorkerAsync(worker, failures).ConfigureAwait(false);
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("Stopping the removed account loops failed (cancellation or an unexpected loop failure).", failures);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Stops the service (<see cref="StopAsync"/>).</summary>
    /// <returns>A task that completes when the service is stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    // Backoff after the n-th consecutive failure: 1, 2, 4, 8, 16, 30, 30 … minutes.
    private static TimeSpan Backoff(int consecutiveFailures)
    {
        int minutes = consecutiveFailures >= BackoffSteps ? MaxBackoffMinutes : 1 << (consecutiveFailures - 1);
        return TimeSpan.FromMinutes(minutes);
    }

    // Type name only (plus the failure category of transport errors); a message may contain server or account details.
    private static string DescribeError(Exception exception)
    {
        return exception is MailTransportException transportFailure
            ? $"{nameof(MailTransportException)}({transportFailure.Failure})"
            : exception.GetType().Name;
    }

    // A wait task that is abandoned (cancelled but maybe not yet finished) must not end as an unobserved exception.
    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static abandonedTask => _ = abandonedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // Cancels and records a throwing cancellation callback instead of propagating it, so the caller still waits.
    private static async Task CancelCollectingAsync(Func<Task> cancel, List<Exception> failures)
    {
        try
        {
            await cancel().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failures.Add(exception);
        }
    }

    // Collects unexpected failures of a worker's loops; cancellation is the normal end. Disposes the worker afterwards.
    private static async Task WaitForWorkerAsync(AccountWorker worker, List<Exception> failures)
    {
        foreach (Task loopTask in new[] { worker.LoopTask, worker.WatcherTask })
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(exception);
            }
        }

        worker.Dispose();
    }

    // Registers the worker with an initial status and starts its two loops on the thread pool. Caller holds lifecycleGate.
    private void StartWorker(Account account, CancellationToken serviceToken)
    {
        var worker = new AccountWorker(account, serviceToken);
        lock (stateGate)
        {
            workers[account.Id] = worker;
            if (!statuses.ContainsKey(account.Id))
            {
                statusOrder.Add(account.Id);
            }

            statuses[account.Id] = new AccountSyncStatus(account.Id, AccountSyncState.Idle, null, null, null, null);
        }

        worker.LoopTask = Task.Run(() => RunAccountLoopAsync(worker), CancellationToken.None);
        worker.WatcherTask = Task.Run(() => RunWatcherLoopAsync(worker), CancellationToken.None);
    }

    private AccountWorker? FindWorker(Guid accountId)
    {
        lock (stateGate)
        {
            return workers.GetValueOrDefault(accountId);
        }
    }

    // Account loop: cycle, wait, cycle … until stop. Ends with status Stopped on every path.
    private async Task RunAccountLoopAsync(AccountWorker worker)
    {
        CancellationToken ct = worker.Token;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Triggers up to here are served by this cycle; later ones cause exactly one more cycle.
                worker.CycleTrigger.Reset();
                worker.Retry.Reset();
                CycleOutcome outcome = await RunCycleAsync(worker, ct).ConfigureAwait(false);
                await WaitForNextCycleAsync(worker, outcome, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop requested (service stop or account removed): the loop ends normally.
        }
        finally
        {
            UpdateStatus(
                worker.Account.Id,
                status => status with { State = AccountSyncState.Stopped, NextRunAt = null, Progress = null });
        }
    }

    // One cycle: queued commands, then the sync pass. Failures become a status; only a stop propagates.
    private async Task<CycleOutcome> RunCycleAsync(AccountWorker worker, CancellationToken ct)
    {
        Guid accountId = worker.Account.Id;
        UpdateStatus(accountId, status => status with { State = AccountSyncState.Running, NextRunAt = null, Progress = null });

        var progress = new CycleProgress(this, accountId);
        Exception? failure = null;
        try
        {
            await executeCommands(accountId, ct).ConfigureAwait(false);
            await syncAccount(accountId, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Every other failure (also unexpected ones) is kept to this account; the service never crashes.
            failure = exception;
        }
        finally
        {
            progress.Close();
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (failure is null)
        {
            worker.ConsecutiveFailures = 0;
            UpdateStatus(
                accountId,
                status => status with
                {
                    State = AccountSyncState.Idle,
                    LastSuccessAt = now,
                    LastErrorType = null,
                    NextRunAt = now + options.PollInterval,
                    Progress = null,
                });
            return new CycleOutcome(AccountSyncState.Idle, options.PollInterval);
        }

        string errorType = DescribeError(failure);
        if (failure is AuthenticationFailedException { Reason: AuthFailureReason.InteractionRequired })
        {
            UpdateStatus(
                accountId,
                status => status with
                {
                    State = AccountSyncState.NeedsSignIn,
                    LastErrorType = errorType,
                    NextRunAt = null,
                    Progress = null,
                });
            return new CycleOutcome(AccountSyncState.NeedsSignIn, WaitDuration: null);
        }

        worker.ConsecutiveFailures++;
        TimeSpan backoff = Backoff(worker.ConsecutiveFailures);
        UpdateStatus(
            accountId,
            status => status with
            {
                State = AccountSyncState.WaitingAfterError,
                LastErrorType = errorType,
                NextRunAt = now + backoff,
                Progress = null,
            });
        return new CycleOutcome(AccountSyncState.WaitingAfterError, backoff);
    }

    // Waits for the scheduled time, a trigger/retry (then the debounce) or stop (OperationCanceledException).
    private async Task WaitForNextCycleAsync(AccountWorker worker, CycleOutcome outcome, CancellationToken ct)
    {
        using var timerCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? timerTask = outcome.WaitDuration is TimeSpan waitDuration
            ? DelayAsync(waitDuration, timerCancel.Token)
            : null;
        bool triggersAllowed = outcome.State == AccountSyncState.Idle;
        bool triggered;
        try
        {
            triggered = await WaitForTimerOrSignalAsync(worker, timerTask, triggersAllowed, ct).ConfigureAwait(false);
        }
        finally
        {
            if (timerTask is not null)
            {
                if (!timerTask.IsCompleted)
                {
                    await timerCancel.CancelAsync().ConfigureAwait(false);
                }

                // Also a timer that faulted while a trigger won (awaiting it again is harmless).
                ObserveFault(timerTask);
            }
        }

        if (triggered && options.TriggerDebounce > TimeSpan.Zero)
        {
            await DelayUnlessStoppedAsync(worker, options.TriggerDebounce, ct).ConfigureAwait(false);
        }
    }

    // True when a trigger/retry ended the wait, false when the timer elapsed. Flags are checked before every wait, so a
    // trigger between check and wait is never lost (WhenSet completes at once for a set flag).
    private static async Task<bool> WaitForTimerOrSignalAsync(
        AccountWorker worker,
        Task? timerTask,
        bool triggersAllowed,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (worker.Retry.IsSet || (triggersAllowed && worker.CycleTrigger.IsSet))
            {
                return true;
            }

            List<Task> waits = [worker.Retry.WhenSet(), worker.Stopped];
            if (triggersAllowed)
            {
                waits.Add(worker.CycleTrigger.WhenSet());
            }

            if (timerTask is not null)
            {
                waits.Add(timerTask);
            }

            Task first = await Task.WhenAny(waits).ConfigureAwait(false);
            if (first == timerTask)
            {
                ct.ThrowIfCancellationRequested();
                await timerTask.ConfigureAwait(false);
                return false;
            }
        }
    }

    // Watcher loop: connect, IDLE until failure; reconnect after its own backoff, stop on NotSupported, wait for a
    // retry when a sign-in is needed (InteractionRequired). A change triggers a cycle like TriggerAccount.
    private async Task RunWatcherLoopAsync(AccountWorker worker)
    {
        CancellationToken ct = worker.Token;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Retries before this attempt are served by it.
                worker.WatcherRetry.Reset();
                WatcherEnd end = await WatchInboxAsync(worker, ct).ConfigureAwait(false);
                switch (end)
                {
                    case WatcherEnd.NotSupported:
                        return;
                    case WatcherEnd.SignInRequired:
                        await Task.WhenAny(worker.WatcherRetry.WhenSet(), worker.Stopped).ConfigureAwait(false);
                        worker.WatcherFailures = 0;
                        break;
                    default:
                        worker.WatcherFailures++;
                        await DelayUnlessStoppedAsync(worker, Backoff(worker.WatcherFailures), ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop requested: the watcher ends normally.
        }
    }

    // One watcher session; the watcher is disposed on every path before the caller waits for a reconnect.
    private async Task<WatcherEnd> WatchInboxAsync(AccountWorker worker, CancellationToken ct)
    {
        IMailIdleWatcher? watcher = null;
        try
        {
            watcher = await idleWatchers.ConnectAsync(worker.Account, ct).ConfigureAwait(false);
            while (true)
            {
                bool changed = await watcher.WaitForChangeAsync(options.IdleRenewInterval, ct).ConfigureAwait(false);
                worker.WatcherFailures = 0;
                if (changed)
                {
                    worker.CycleTrigger.Set();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            return WatcherEnd.NotSupported;
        }
        catch (AuthenticationFailedException exception) when (exception.Reason == AuthFailureReason.InteractionRequired)
        {
            return WatcherEnd.SignInRequired;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // MailTransportException, other token failures (e.g. no network after resume) and anything unexpected:
            // reconnect after the watcher backoff.
            return WatcherEnd.ConnectionFailed;
        }
        finally
        {
            if (watcher is not null)
            {
                await DisposeWatcherAsync(watcher).ConfigureAwait(false);
            }
        }
    }

    private static async Task DisposeWatcherAsync(IMailIdleWatcher watcher)
    {
        try
        {
            await watcher.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ignored on purpose: the session is abandoned either way; a failing logout must not end the watcher loop.
        }
    }

    // Every wait goes through IServiceDelay; the async wrapper turns a synchronous throw into a faulted task.
    private async Task DelayAsync(TimeSpan duration, CancellationToken ct)
    {
        await delay.DelayAsync(duration, ct).ConfigureAwait(false);
    }

    // A wait that also ends on stop, even when a delay implementation ignores the token.
    private async Task DelayUnlessStoppedAsync(AccountWorker worker, TimeSpan duration, CancellationToken ct)
    {
        Task delayTask = DelayAsync(duration, ct);
        Task first = await Task.WhenAny(delayTask, worker.Stopped).ConfigureAwait(false);
        if (first != delayTask)
        {
            ObserveFault(delayTask);
            ct.ThrowIfCancellationRequested();
        }

        await delayTask.ConfigureAwait(false);
    }

    // Stores the changed status under the lock and raises the event after releasing it.
    private void UpdateStatus(Guid accountId, Func<AccountSyncStatus, AccountSyncStatus> change)
    {
        AccountSyncStatus updated;
        lock (stateGate)
        {
            if (!statuses.TryGetValue(accountId, out AccountSyncStatus? current))
            {
                return;
            }

            updated = change(current);
            statuses[accountId] = updated;
        }

        RaiseStatusChanged(updated);
    }

    private void RaiseStatusChanged(AccountSyncStatus status)
    {
        EventHandler<AccountSyncStatus>? handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<AccountSyncStatus> handler in handlers.GetInvocationList().Cast<EventHandler<AccountSyncStatus>>())
        {
            try
            {
                handler(this, status);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Ignored on purpose: a faulty subscriber must not stop an account loop or other subscribers.
            }
        }
    }

    /// <summary>Result of a cycle: the state it left and how long to wait for the next scheduled one (null = none).</summary>
    private sealed record CycleOutcome(AccountSyncState State, TimeSpan? WaitDuration);

    /// <summary>Forwards sync progress as Running status while the cycle runs; reports after the cycle are dropped.</summary>
    private sealed class CycleProgress(MailboxService service, Guid accountId) : IProgress<SyncProgress>
    {
        private volatile bool isClosed;

        public void Report(SyncProgress value)
        {
            if (isClosed)
            {
                return;
            }

            service.UpdateStatus(accountId, status => status with { State = AccountSyncState.Running, Progress = value });
        }

        public void Close()
        {
            isClosed = true;
        }
    }

    /// <summary>
    /// A level-triggered wake-up flag: <see cref="Set"/> keeps the flag until <see cref="Reset"/>; <see cref="WhenSet"/>
    /// completes at once for a set flag, otherwise at the next <see cref="Set"/>. Continuations run asynchronously, so
    /// nothing runs inside the lock.
    /// </summary>
    private sealed class WakeSignal
    {
        private readonly Lock gate = new();
        private TaskCompletionSource waiter = NewWaiter();
        private bool isSet;

        public bool IsSet
        {
            get
            {
                lock (gate)
                {
                    return isSet;
                }
            }
        }

        public void Set()
        {
            lock (gate)
            {
                isSet = true;
                waiter.TrySetResult();
            }
        }

        public void Reset()
        {
            lock (gate)
            {
                isSet = false;
                if (waiter.Task.IsCompleted)
                {
                    waiter = NewWaiter();
                }
            }
        }

        public Task WhenSet()
        {
            lock (gate)
            {
                if (isSet)
                {
                    return Task.CompletedTask;
                }

                if (waiter.Task.IsCompleted)
                {
                    waiter = NewWaiter();
                }

                return waiter.Task;
            }
        }

        private static TaskCompletionSource NewWaiter() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Loops, signals and stop token of one account.</summary>
    private sealed class AccountWorker : IDisposable
    {
        private readonly CancellationTokenSource stopSource;
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration stopRegistration;

        public AccountWorker(Account account, CancellationToken serviceToken)
        {
            Account = account;
            stopSource = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
            Token = stopSource.Token;
            stopRegistration = Token.Register(() => stopped.TrySetResult());
        }

        public Account Account { get; }

        /// <summary>Cancelled when the service stops or the account is removed.</summary>
        public CancellationToken Token { get; }

        /// <summary>Completes when <see cref="Token"/> is cancelled; ends waits that a delay fake might not end.</summary>
        public Task Stopped => stopped.Task;

        /// <summary>TriggerAccount and IDLE changes.</summary>
        public WakeSignal CycleTrigger { get; } = new();

        /// <summary>RetryAccount for the account loop.</summary>
        public WakeSignal Retry { get; } = new();

        /// <summary>RetryAccount for the watcher loop.</summary>
        public WakeSignal WatcherRetry { get; } = new();

        /// <summary>Consecutive failed cycles; touched by the account loop only.</summary>
        public int ConsecutiveFailures { get; set; }

        /// <summary>Consecutive failed watcher sessions; touched by the watcher loop only.</summary>
        public int WatcherFailures { get; set; }

        public Task LoopTask { get; set; } = Task.CompletedTask;

        public Task WatcherTask { get; set; } = Task.CompletedTask;

        public Task CancelAsync() => stopSource.CancelAsync();

        public void Dispose()
        {
            stopRegistration.Dispose();
            stopSource.Dispose();
        }
    }
}
