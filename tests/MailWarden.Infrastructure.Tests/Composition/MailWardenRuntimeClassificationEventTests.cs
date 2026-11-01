using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Classification;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Sync;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Tests.Composition;

/// <summary>AP-07d D3 / case 13: the runtime raises <c>ClassificationPassCompleted</c> from the real service loop (fake steps, no server).</summary>
[Trait("Category", "feature:classifier")]
[Trait("Category", "feature:sync")]
[Trait("Category", "feature:ui")]
public sealed class MailWardenRuntimeClassificationEventTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(AppContext.BaseDirectory, "ap07d-runtime-events", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was created.
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private sealed class NoIdleWatchers : IMailIdleWatcherFactory
    {
        public Task<IMailIdleWatcher> ConnectAsync(Account account, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private DataPaths NewPaths()
    {
        string root = Path.Combine(_baseDirectory, "data");
        Directory.CreateDirectory(root);
        return new DataPaths(root);
    }

    private static ClassificationPassReport Report(int moves = 0, int evaluationFailed = 0, int brakeHeld = 0) =>
        new(
            Classified: 0,
            SkippedKnown: 0,
            ReadFailed: 0,
            MovesEnqueued: moves,
            RetentionDeletesEnqueued: 0,
            ActSuspendedNoContacts: false,
            TierCounts: new Dictionary<MailWarden.Core.Classification.SpamTier, int>(),
            RetentionDeletesRejected: 0,
            EvaluationFailed: evaluationFailed,
            BrakeHeld: brakeHeld);

    private static async Task<Account> AddAccountAsync(MailWardenRuntime runtime)
    {
        Account account = TestData.NewAccount("Account_A");
        await new SqliteAccountStore(await runtime.OpenDatabaseAsync()).UpsertAsync(account);
        return account;
    }

    private static AccountCycleSteps Steps(Func<Guid, CancellationToken, Task<ClassificationPassReport>> classify) =>
        new(
            (accountId, _, _) => Task.FromResult(new SyncReport(accountId, 0, 0, [])),
            new NoIdleWatchers(),
            classify);

    private static async Task<T> WithinAsync<T>(Task<T> task)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(task, finished);
        return await task;
    }

    // Case 13
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Service_cycle_raises_the_event_with_the_counts_of_the_pass()
    {
        await using MailWardenRuntime runtime = await MailWardenRuntime.OpenWithCycleStepsAsync(
            NewPaths(), Steps((_, _) => Task.FromResult(Report(evaluationFailed: 3, brakeHeld: 5))));
        Account account = await AddAccountAsync(runtime);
        var raised = new TaskCompletionSource<ClassificationPassSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ClassificationPassCompleted += (_, summary) => raised.TrySetResult(summary);

        await runtime.Service.StartAsync();

        ClassificationPassSummary summary = await WithinAsync(raised.Task);
        Assert.Equal(account.Id, summary.AccountId);
        Assert.Equal(3, summary.EvaluationFailed);
        Assert.Equal(5, summary.BrakeHeld);
    }

    // Case 13: enqueued moves trigger a second cycle soon (after the debounce of 2 s).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Enqueued_moves_trigger_a_second_cycle()
    {
        int passes = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using MailWardenRuntime runtime = await MailWardenRuntime.OpenWithCycleStepsAsync(
            NewPaths(),
            Steps((_, _) =>
            {
                if (Interlocked.Increment(ref passes) >= 2)
                {
                    second.TrySetResult();
                }

                // Only the first pass enqueues, so the follow-up cycle ends the chain.
                return Task.FromResult(Report(moves: passes == 1 ? 2 : 0));
            }));
        await AddAccountAsync(runtime);

        await runtime.Service.StartAsync();

        await WithinAsync(second.Task.ContinueWith(_ => true, TaskScheduler.Default));
        Assert.True(Volatile.Read(ref passes) >= 2);
    }

    // Case 13, control: without enqueued moves no follow-up cycle starts within the debounce window.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Pass_without_moves_does_not_trigger_a_second_cycle()
    {
        int passes = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using MailWardenRuntime runtime = await MailWardenRuntime.OpenWithCycleStepsAsync(
            NewPaths(),
            Steps((_, _) =>
            {
                Interlocked.Increment(ref passes);
                first.TrySetResult();
                return Task.FromResult(Report());
            }));
        await AddAccountAsync(runtime);

        await runtime.Service.StartAsync();
        await WithinAsync(first.Task.ContinueWith(_ => true, TaskScheduler.Default));
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Equal(1, Volatile.Read(ref passes));
    }

    // D3: not raised after dispose. The pass finishes only after DisposeAsync has begun.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Event_is_not_raised_for_a_pass_that_finishes_during_dispose()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MailWardenRuntime runtime = await MailWardenRuntime.OpenWithCycleStepsAsync(
            NewPaths(),
            Steps(async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return Report(evaluationFailed: 1);
            }));
        await AddAccountAsync(runtime);
        int raisedCount = 0;
        runtime.ClassificationPassCompleted += (_, _) => Interlocked.Increment(ref raisedCount);
        await runtime.Service.StartAsync();
        await WithinAsync(entered.Task.ContinueWith(_ => true, TaskScheduler.Default));

        Task dispose = runtime.DisposeAsync().AsTask();
        release.TrySetResult();
        await WithinAsync(dispose.ContinueWith(_ => true, TaskScheduler.Default));

        Assert.Equal(0, Volatile.Read(ref raisedCount));
    }
}
