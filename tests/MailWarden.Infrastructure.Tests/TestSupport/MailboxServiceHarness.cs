using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Commands;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>One recorded call of the executor or sync delegate.</summary>
internal sealed record ServiceCall(string Kind, Guid AccountId, int Number);

/// <summary>Arguments handed to a scripted sync behaviour. <see cref="Number"/> is 1-based per account.</summary>
internal sealed record SyncInvocation(int Number, IProgress<SyncProgress>? Progress, CancellationToken Ct);

/// <summary>One requested wait of the service; the test completes it explicitly.</summary>
internal sealed class DelayRequest(TimeSpan duration, int sequence)
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TimeSpan Duration { get; } = duration;

    public int Sequence { get; } = sequence;

    public Task Task => _completion.Task;

    public bool IsPending => !_completion.Task.IsCompleted;

    public void Complete() => _completion.TrySetResult();

    public void Cancel(CancellationToken ct) => _completion.TrySetCanceled(ct);
}

/// <summary>Step of a scripted idle watcher.</summary>
internal sealed record IdleStep(bool? Result, Exception? Failure)
{
    public static IdleStep Change { get; } = new(true, null);

    public static IdleStep Timeout { get; } = new(false, null);

    public static IdleStep Throw(Exception failure) => new(null, failure);
}

/// <summary>
/// Everything a <see cref="MailboxService"/> test needs: fake accounts, executor, sync, idle watchers, delay and
/// fixed time, a temporary data root below the test bin folder, recorded calls and statuses, and an async wait
/// helper. All state is guarded by <c>_gate</c>; waiters are re-evaluated after each change.
/// </summary>
internal sealed class MailboxServiceHarness : IAsyncDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NegativeCheckWindow = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private readonly List<(Func<bool> Condition, TaskCompletionSource Signal)> _waiters = [];
    private readonly List<ServiceCall> _calls = [];
    private readonly List<AccountSyncStatus> _statuses = [];
    private readonly List<DelayRequest> _delays = [];
    private readonly Dictionary<Guid, Func<SyncInvocation, Task>> _syncBehaviors = [];
    private readonly Dictionary<Guid, Func<int, CancellationToken, Task>> _executeBehaviors = [];
    private readonly Dictionary<Guid, int> _syncCounts = [];
    private readonly Dictionary<Guid, int> _executeCounts = [];
    private readonly Dictionary<Guid, Queue<IdleStep>> _idleScripts = [];
    private readonly Dictionary<Guid, List<FakeIdleWatcher>> _watchers = [];
    private readonly List<Account> _accountList;
    private int _accountListCalls;
    private int _delaySequence;

    public MailboxServiceHarness(IEnumerable<Account> accounts, MailboxServiceOptions? options = null)
    {
        _accountList = [.. accounts];
        Options = options ?? MailboxServiceOptions.Default;
        RootPath = Path.Combine(AppContext.BaseDirectory, "ap03d-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        Paths = new DataPaths(RootPath);
        Time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero));
        Service = new MailboxService(
            Paths,
            new FakeAccountStore(this),
            ExecuteAsync,
            SyncAsync,
            new FakeIdleWatcherFactory(this),
            Options,
            new FakeServiceDelay(this),
            Time);
        Service.StatusChanged += (_, status) =>
        {
            lock (_gate)
            {
                _statuses.Add(status);
            }

            Changed();
        };
    }

    public string RootPath { get; }

    public DataPaths Paths { get; }

    public FixedTimeProvider Time { get; }

    public MailboxServiceOptions Options { get; }

    public MailboxService Service { get; }

    /// <summary>When true, delays equal to <c>TriggerDebounce</c> complete immediately.</summary>
    public bool AutoCompleteDebounce { get; set; }

    public static Account NewAccount(string tag, MailProvider provider = MailProvider.GenericImap) =>
        new(Guid.NewGuid(), $"Account_{tag}", $"sender_{tag.ToLowerInvariant()}@example.test", provider);

    // ---- configuration -------------------------------------------------------------------------------------

    public void SetSyncBehavior(Guid accountId, Func<SyncInvocation, Task> behavior)
    {
        lock (_gate)
        {
            _syncBehaviors[accountId] = behavior;
        }
    }

    public void SetExecuteBehavior(Guid accountId, Func<int, CancellationToken, Task> behavior)
    {
        lock (_gate)
        {
            _executeBehaviors[accountId] = behavior;
        }
    }

    /// <summary>Scripts the idle watchers of an account; the script is shared across reconnects.</summary>
    public void SetIdleScript(Guid accountId, params IdleStep[] steps)
    {
        lock (_gate)
        {
            _idleScripts[accountId] = new Queue<IdleStep>(steps);
        }
    }

    public void ReplaceAccounts(params Account[] accounts)
    {
        lock (_gate)
        {
            _accountList.Clear();
            _accountList.AddRange(accounts);
        }
    }

    // ---- observation ---------------------------------------------------------------------------------------

    public IReadOnlyList<ServiceCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<AccountSyncStatus> Statuses
    {
        get
        {
            lock (_gate)
            {
                return [.. _statuses];
            }
        }
    }

    public int AccountListCalls
    {
        get
        {
            lock (_gate)
            {
                return _accountListCalls;
            }
        }
    }

    public int TotalCalls
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    public int SyncCount(Guid accountId)
    {
        lock (_gate)
        {
            return _syncCounts.GetValueOrDefault(accountId);
        }
    }

    public int ExecuteCount(Guid accountId)
    {
        lock (_gate)
        {
            return _executeCounts.GetValueOrDefault(accountId);
        }
    }

    public int ConnectCount(Guid accountId)
    {
        lock (_gate)
        {
            return ConnectCountLocked(accountId);
        }
    }

    public int WaitCallCount(Guid accountId)
    {
        lock (_gate)
        {
            return WaitCallCountLocked(accountId);
        }
    }

    public IReadOnlyList<TimeSpan> WaitMaxWaits(Guid accountId)
    {
        lock (_gate)
        {
            return _watchers.TryGetValue(accountId, out List<FakeIdleWatcher>? list)
                ? [.. list.SelectMany(watcher => watcher.WaitCalls)]
                : [];
        }
    }

    public int DisposedWatcherCount(Guid accountId)
    {
        lock (_gate)
        {
            return _watchers.TryGetValue(accountId, out List<FakeIdleWatcher>? list) ? list.Count(watcher => watcher.Disposed) : 0;
        }
    }

    public bool AllWatchersDisposed()
    {
        lock (_gate)
        {
            return _watchers.Values.SelectMany(list => list).All(watcher => watcher.Disposed);
        }
    }

    public IReadOnlyList<DelayRequest> DelayRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _delays];
            }
        }
    }

    public int StatusMark()
    {
        lock (_gate)
        {
            return _statuses.Count;
        }
    }

    public AccountSyncStatus? LatestStatus(Guid accountId)
    {
        lock (_gate)
        {
            return _statuses.LastOrDefault(status => status.AccountId == accountId);
        }
    }

    public bool HasPendingDelay(TimeSpan duration)
    {
        lock (_gate)
        {
            return _delays.Any(request => request.IsPending && request.Duration == duration);
        }
    }

    // ---- waiting -------------------------------------------------------------------------------------------

    /// <summary>Waits until <paramref name="condition"/> holds; fails after 20 s with the description.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (condition())
            {
                return;
            }

            _waiters.Add((condition, signal));
        }

        try
        {
            await signal.Task.WaitAsync(WaitLimit);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Timed out waiting for: {description}");
        }
    }

    /// <summary>Waits for a status of the account (from index <paramref name="fromIndex"/> on) matching the predicate.</summary>
    public async Task<AccountSyncStatus> WaitForStatusAsync(
        Guid accountId, Func<AccountSyncStatus, bool> predicate, string description, int fromIndex = 0)
    {
        AccountSyncStatus? found = null;
        await WaitUntilAsync(
            () =>
            {
                found = _statuses.Skip(fromIndex).FirstOrDefault(status => status.AccountId == accountId && predicate(status));
                return found is not null;
            },
            $"status: {description}");
        return found!;
    }

    /// <summary>Waits for an Idle status that carries a success time (i.e. a finished cycle, not the initial status).</summary>
    public Task<AccountSyncStatus> WaitForSuccessAsync(Guid accountId, int fromIndex = 0) =>
        WaitForStatusAsync(
            accountId,
            status => status.State == AccountSyncState.Idle && status.LastSuccessAt is not null,
            "Idle with LastSuccessAt",
            fromIndex);

    public Task<AccountSyncStatus> WaitForStateAsync(Guid accountId, AccountSyncState state, int fromIndex = 0) =>
        WaitForStatusAsync(accountId, status => status.State == state, state.ToString(), fromIndex);

    public Task WaitForSyncCountAsync(Guid accountId, int count) =>
        WaitUntilAsync(() => _syncCounts.GetValueOrDefault(accountId) >= count, $"sync call #{count}");

    public Task WaitForExecuteCountAsync(Guid accountId, int count) =>
        WaitUntilAsync(() => _executeCounts.GetValueOrDefault(accountId) >= count, $"execute call #{count}");

    public Task WaitForConnectCountAsync(Guid accountId, int count) =>
        WaitUntilAsync(() => ConnectCountLocked(accountId) >= count, $"idle connect #{count}");

    public Task WaitForIdleWaitCountAsync(Guid accountId, int count) =>
        WaitUntilAsync(() => WaitCallCountLocked(accountId) >= count, $"WaitForChangeAsync call #{count}");

    public async Task<DelayRequest> WaitForPendingDelayAsync(TimeSpan duration)
    {
        DelayRequest? found = null;
        await WaitUntilAsync(
            () =>
            {
                found = _delays.FirstOrDefault(request => request.IsPending && request.Duration == duration);
                return found is not null;
            },
            $"pending delay of {duration}");
        return found!;
    }

    /// <summary>Waits for a pending delay of the given length and completes it.</summary>
    public async Task CompleteDelayAsync(TimeSpan duration)
    {
        DelayRequest request = await WaitForPendingDelayAsync(duration);
        request.Complete();
        Changed();
    }

    /// <summary>Completes every delay that is pending right now.</summary>
    public void CompleteAllPendingDelays()
    {
        List<DelayRequest> pending;
        lock (_gate)
        {
            pending = [.. _delays.Where(request => request.IsPending)];
        }

        foreach (DelayRequest request in pending)
        {
            request.Complete();
        }

        Changed();
    }

    /// <summary>Gives the service a short real-time window in which something unwanted could still happen.</summary>
    public static Task NegativeCheckWindowAsync() => Task.Delay(NegativeCheckWindow);

    // ---- fakes ---------------------------------------------------------------------------------------------

    private async Task<CommandRunReport> ExecuteAsync(Guid accountId, CancellationToken ct)
    {
        int number;
        Func<int, CancellationToken, Task>? behavior;
        lock (_gate)
        {
            number = _executeCounts.GetValueOrDefault(accountId) + 1;
            _executeCounts[accountId] = number;
            _calls.Add(new ServiceCall("exec", accountId, number));
            _executeBehaviors.TryGetValue(accountId, out behavior);
        }

        Changed();
        if (behavior is not null)
        {
            await behavior(number, ct);
        }

        return new CommandRunReport(0, 0, 0);
    }

    private async Task<SyncReport> SyncAsync(Guid accountId, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        int number;
        Func<SyncInvocation, Task>? behavior;
        lock (_gate)
        {
            number = _syncCounts.GetValueOrDefault(accountId) + 1;
            _syncCounts[accountId] = number;
            _calls.Add(new ServiceCall("sync", accountId, number));
            _syncBehaviors.TryGetValue(accountId, out behavior);
        }

        Changed();
        if (behavior is not null)
        {
            await behavior(new SyncInvocation(number, progress, ct));
        }

        return new SyncReport(accountId, 0, 0, []);
    }

    // Helpers below expect _gate to be held (or to be called from a waiter condition, which runs under it).
    private int ConnectCountLocked(Guid accountId) =>
        _watchers.TryGetValue(accountId, out List<FakeIdleWatcher>? list) ? list.Count : 0;

    private int WaitCallCountLocked(Guid accountId) =>
        _watchers.TryGetValue(accountId, out List<FakeIdleWatcher>? list) ? list.Sum(watcher => watcher.WaitCalls.Count) : 0;

    /// <summary>Re-evaluates all waiters; call after every state change, never while holding the gate.</summary>
    private void Changed()
    {
        List<TaskCompletionSource> ready = [];
        lock (_gate)
        {
            for (int index = _waiters.Count - 1; index >= 0; index--)
            {
                if (_waiters[index].Condition())
                {
                    ready.Add(_waiters[index].Signal);
                    _waiters.RemoveAt(index);
                }
            }
        }

        foreach (TaskCompletionSource signal in ready)
        {
            signal.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
            // A failing test may leave the lock file open; cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private sealed class FakeAccountStore(MailboxServiceHarness owner) : IAccountStore
    {
        public Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default)
        {
            lock (owner._gate)
            {
                owner._accountListCalls++;
                return Task.FromResult<IReadOnlyList<Account>>([.. owner._accountList]);
            }
        }

        public Task<Account?> GetAsync(Guid id, CancellationToken ct = default)
        {
            lock (owner._gate)
            {
                return Task.FromResult(owner._accountList.FirstOrDefault(account => account.Id == id));
            }
        }

        public Task UpsertAsync(Account account, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeServiceDelay(MailboxServiceHarness owner) : IServiceDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            DelayRequest request;
            lock (owner._gate)
            {
                request = new DelayRequest(delay, ++owner._delaySequence);
                owner._delays.Add(request);
            }

            if (owner.AutoCompleteDebounce && delay == owner.Options.TriggerDebounce)
            {
                request.Complete();
            }
            else if (ct.CanBeCanceled)
            {
                ct.Register(() =>
                {
                    request.Cancel(ct);
                    owner.Changed();
                });
            }

            owner.Changed();
            return request.Task;
        }
    }

    private sealed class FakeIdleWatcherFactory(MailboxServiceHarness owner) : IMailIdleWatcherFactory
    {
        public Task<IMailIdleWatcher> ConnectAsync(Account account, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            FakeIdleWatcher watcher = new(owner, account.Id);
            lock (owner._gate)
            {
                if (!owner._watchers.TryGetValue(account.Id, out List<FakeIdleWatcher>? list))
                {
                    list = [];
                    owner._watchers[account.Id] = list;
                }

                list.Add(watcher);
            }

            owner.Changed();
            return Task.FromResult<IMailIdleWatcher>(watcher);
        }
    }

    /// <summary>Watcher that follows the account's shared script and blocks (until cancelled/disposed) afterwards.</summary>
    private sealed class FakeIdleWatcher(MailboxServiceHarness owner, Guid accountId) : IMailIdleWatcher
    {
        private readonly CancellationTokenSource _disposed = new();

        public List<TimeSpan> WaitCalls { get; } = [];

        public bool Disposed { get; private set; }

        public async Task<bool> WaitForChangeAsync(TimeSpan maxWait, CancellationToken ct = default)
        {
            IdleStep? step = null;
            lock (owner._gate)
            {
                WaitCalls.Add(maxWait);
                if (owner._idleScripts.TryGetValue(accountId, out Queue<IdleStep>? script) && script.Count > 0)
                {
                    step = script.Dequeue();
                }
            }

            owner.Changed();
            if (step is null)
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
                await Task.Delay(Timeout.Infinite, linked.Token);
                return false;
            }

            if (step.Failure is not null)
            {
                throw step.Failure;
            }

            return step.Result!.Value;
        }

        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                Disposed = true;
            }

            _disposed.Cancel();
            owner.Changed();
            return ValueTask.CompletedTask;
        }
    }
}
