using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Composition;

/// <summary>AP-07d D1 / cases 1-6: <see cref="ClassifyingSyncCycle"/> with fake functions, no server.</summary>
[Trait("Category", "feature:classifier")]
[Trait("Category", "feature:sync")]
public sealed class ClassifyingSyncCycleTests
{
    private const string MarkerText = "marker_cycle_failure_text";

    private static readonly Guid AccountId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>The cycle with scripted functions; every call is recorded in <see cref="Order"/>.</summary>
    private sealed class Rig
    {
        public Rig()
        {
            SyncResult = new SyncReport(AccountId, 0, 0, []);
            SyncBehavior = (_, _, _) => Task.FromResult(SyncResult);
            ClassifyBehavior = (_, _) => Task.FromResult(Report());
            Cycle = new ClassifyingSyncCycle(
                (accountId, progress, ct) =>
                {
                    Order.Add("sync");
                    SyncArguments.Add((accountId, progress, ct));
                    return SyncBehavior(accountId, progress, ct);
                },
                (_, _) => Task.FromResult(new ContactHarvestReport(0, 0, 0)),
                (accountId, ct) =>
                {
                    Order.Add("classify");
                    ClassifyArguments.Add(accountId);
                    return ClassifyBehavior(accountId, ct);
                },
                accountId =>
                {
                    Order.Add("trigger");
                    Triggers.Add(accountId);
                },
                summary =>
                {
                    Order.Add("summary");
                    Summaries.Add(summary);
                    SummaryBehavior(summary);
                },
                Log);
        }

        public ClassifyingSyncCycle Cycle { get; }

        public SyncReport SyncResult { get; }

        public RecordingCriticalLog Log { get; } = new();

        public List<string> Order { get; } = [];

        public List<(Guid AccountId, IProgress<SyncProgress>? Progress, CancellationToken Ct)> SyncArguments { get; } = [];

        public List<Guid> ClassifyArguments { get; } = [];

        public List<Guid> Triggers { get; } = [];

        public List<ClassificationPassSummary> Summaries { get; } = [];

        public Func<Guid, IProgress<SyncProgress>?, CancellationToken, Task<SyncReport>> SyncBehavior { get; set; }

        public Func<Guid, CancellationToken, Task<ClassificationPassReport>> ClassifyBehavior { get; set; }

        public Action<ClassificationPassSummary> SummaryBehavior { get; set; } = _ => { };

        public Task<SyncReport> RunAsync(CancellationToken ct = default) => Cycle.RunAsync(AccountId, null, ct);
    }

    private static ClassificationPassReport Report(
        int moves = 0,
        int retentionDeletes = 0,
        int retentionRejected = 0,
        int brakeHeld = 0,
        int evaluationFailed = 0,
        int readFailed = 0,
        bool actSuspended = false) =>
        new(
            Classified: 0,
            SkippedKnown: 0,
            ReadFailed: readFailed,
            MovesEnqueued: moves,
            RetentionDeletesEnqueued: retentionDeletes,
            ActSuspendedNoContacts: actSuspended,
            TierCounts: new Dictionary<SpamTier, int>(),
            RetentionDeletesRejected: retentionRejected,
            EvaluationFailed: evaluationFailed,
            BrakeHeld: brakeHeld);

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Normal_cycle_runs_sync_then_classify_then_summary_and_returns_the_sync_report()
    {
        var rig = new Rig();

        SyncReport result = await rig.RunAsync();

        Assert.Same(rig.SyncResult, result);
        Assert.Equal(new[] { "sync", "classify", "summary" }, rig.Order);
        Assert.Equal(AccountId, Assert.Single(rig.ClassifyArguments));
        Assert.Empty(rig.Log.Entries);
    }

    // Case 1: the arguments of RunAsync reach the sync function unchanged.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Progress_and_cancellation_token_are_handed_to_sync()
    {
        var rig = new Rig();
        var progress = new Progress<SyncProgress>();
        using var source = new CancellationTokenSource();

        await rig.Cycle.RunAsync(AccountId, progress, source.Token);

        (Guid accountId, IProgress<SyncProgress>? receivedProgress, CancellationToken token) = Assert.Single(rig.SyncArguments);
        Assert.Equal(AccountId, accountId);
        Assert.Same(progress, receivedProgress);
        Assert.Equal(source.Token, token);
    }

    // Case 1: the summary carries the counts of the report for the account.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Summary_carries_the_counts_of_the_pass_report()
    {
        var rig = new Rig();
        rig.ClassifyBehavior = (_, _) => Task.FromResult(Report(brakeHeld: 5, evaluationFailed: 2, readFailed: 4, actSuspended: true));

        await rig.RunAsync();

        Assert.Equal(
            new ClassificationPassSummary(AccountId, EvaluationFailed: 2, ReadFailed: 4, BrakeHeld: 5, ActSuspendedNoContacts: true),
            Assert.Single(rig.Summaries));
    }

    // Case 2
    [Theory]
    [InlineData(2, 0, 0, 0, 1)]
    [InlineData(0, 1, 0, 0, 1)]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(0, 0, 3, 0, 0)]
    [InlineData(0, 0, 0, 5, 0)]
    [InlineData(2, 1, 0, 0, 1)]
    public async Task Trigger_runs_once_only_when_moves_or_retention_deletes_were_enqueued(
        int moves, int retentionDeletes, int retentionRejected, int brakeHeld, int expectedTriggers)
    {
        var rig = new Rig();
        rig.ClassifyBehavior = (_, _) => Task.FromResult(Report(moves, retentionDeletes, retentionRejected, brakeHeld));

        await rig.RunAsync();

        Assert.Equal(expectedTriggers, rig.Triggers.Count);
        if (expectedTriggers == 1)
        {
            Assert.Equal(AccountId, rig.Triggers[0]);
        }
    }

    // Case 3
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Sync_failure_skips_classify_and_summary_propagates_and_writes_no_log_line()
    {
        var rig = new Rig();
        var failure = new InvalidOperationException(MarkerText);
        rig.SyncBehavior = (_, _, _) => throw failure;

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.RunAsync());

        Assert.Same(failure, thrown);
        Assert.Equal(new[] { "sync" }, rig.Order);
        Assert.Empty(rig.Log.Entries);
    }

    // Case 3: a sync task that fails asynchronously behaves the same.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Asynchronously_failing_sync_skips_classify_and_summary()
    {
        var rig = new Rig();
        rig.SyncBehavior = async (_, _, _) =>
        {
            await Task.Yield();
            throw new IOException(MarkerText);
        };

        await Assert.ThrowsAsync<IOException>(() => rig.RunAsync());

        Assert.Equal(new[] { "sync" }, rig.Order);
        Assert.Empty(rig.Log.Entries);
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Classify_failure_writes_one_line_with_the_type_name_and_never_the_message_and_propagates()
    {
        var rig = new Rig();
        var failure = new InvalidOperationException(MarkerText);
        rig.ClassifyBehavior = (_, _) => throw failure;

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.RunAsync());

        Assert.Same(failure, thrown);
        (string level, string source, string message) = Assert.Single(rig.Log.Entries);
        Assert.Equal(CriticalLogLevel.Critical, level);
        Assert.Contains("InvalidOperationException", message, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerText, rig.Log.AllText, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(source));
        Assert.Equal(new[] { "sync", "classify" }, rig.Order);
    }

    // Case 4: a classify task that fails asynchronously is treated alike.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Asynchronously_failing_classify_is_logged_by_type_name_and_propagates()
    {
        var rig = new Rig();
        rig.ClassifyBehavior = async (_, _) =>
        {
            await Task.Yield();
            throw new FormatException(MarkerText);
        };

        await Assert.ThrowsAsync<FormatException>(() => rig.RunAsync());

        Assert.Contains("FormatException", Assert.Single(rig.Log.Entries).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerText, rig.Log.AllText, StringComparison.Ordinal);
    }

    // Case 4: cancellation propagates without a log line (and without summary or trigger).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cancellation_during_classify_propagates_without_a_log_line()
    {
        var rig = new Rig();
        using var source = new CancellationTokenSource();
        rig.ClassifyBehavior = (_, ct) =>
        {
            source.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Report(moves: 2));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.RunAsync(source.Token));

        Assert.Empty(rig.Log.Entries);
        Assert.DoesNotContain("summary", rig.Order);
        Assert.DoesNotContain("trigger", rig.Order);
    }

    // Case 19 (fix.2): a cancellation exception without a cancelled token (e.g. a timeout) is a failure: logged, propagates.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task Cancellation_exception_with_an_uncancelled_token_is_logged_and_propagates(Type exceptionType)
    {
        var rig = new Rig();
        var failure = (OperationCanceledException)Activator.CreateInstance(exceptionType, MarkerText)!;
        rig.ClassifyBehavior = (_, _) => throw failure;
        using var source = new CancellationTokenSource();

        OperationCanceledException thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.RunAsync(source.Token));

        Assert.Same(failure, thrown);
        Assert.Contains(exceptionType.Name, Assert.Single(rig.Log.Entries).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerText, rig.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("summary", rig.Order);
    }

    // Case 20 (fix.2): the same exception with a cancelled token is a cancellation: no log line, propagates.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Task_canceled_exception_with_a_cancelled_token_propagates_without_a_log_line()
    {
        var rig = new Rig();
        using var source = new CancellationTokenSource();
        rig.ClassifyBehavior = (_, _) =>
        {
            source.Cancel();
            throw new TaskCanceledException(MarkerText);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.RunAsync(source.Token));

        Assert.Empty(rig.Log.Entries);
        Assert.DoesNotContain("summary", rig.Order);
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Summary_handler_failure_is_logged_once_by_type_name_and_does_not_fail_the_cycle()
    {
        var rig = new Rig();
        rig.SummaryBehavior = _ => throw new InvalidOperationException(MarkerText);

        SyncReport result = await rig.RunAsync();

        Assert.Same(rig.SyncResult, result);
        Assert.Contains("InvalidOperationException", Assert.Single(rig.Log.Entries).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerText, rig.Log.AllText, StringComparison.Ordinal);
    }

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failed_evaluations_and_held_moves_write_one_line_with_the_counts_only()
    {
        var rig = new Rig();
        rig.ClassifyBehavior = (_, _) => Task.FromResult(Report(evaluationFailed: 7, brakeHeld: 13));

        await rig.RunAsync();

        (string level, _, string message) = Assert.Single(rig.Log.Entries);
        Assert.Equal(CriticalLogLevel.Critical, level);
        Assert.Contains("7", message, StringComparison.Ordinal);
        Assert.Contains("13", message, StringComparison.Ordinal);
        Assert.DoesNotContain("@", rig.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountId.ToString(), rig.Log.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "sync", "classify", "summary" }, rig.Order);
    }

    // Case 6: either count alone is enough.
    [Theory]
    [InlineData(3, 0)]
    [InlineData(0, 3)]
    public async Task Either_count_alone_writes_a_line(int evaluationFailed, int brakeHeld)
    {
        var rig = new Rig();
        rig.ClassifyBehavior = (_, _) => Task.FromResult(Report(evaluationFailed: evaluationFailed, brakeHeld: brakeHeld));

        await rig.RunAsync();

        Assert.Single(rig.Log.Entries);
    }

    // Case 6, control: a clean pass writes nothing.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Clean_pass_writes_no_line()
    {
        var rig = new Rig();
        rig.ClassifyBehavior = (_, _) => Task.FromResult(Report(moves: 4, retentionDeletes: 1, actSuspended: true));

        await rig.RunAsync();

        Assert.Empty(rig.Log.Entries);
    }

    // Case 6 / D4: ReadFailed shows up as a count in the line of the pass (when a line is written).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Read_failed_count_is_part_of_the_pass_line()
    {
        var rig = new Rig();
        rig.ClassifyBehavior = (_, _) => Task.FromResult(Report(evaluationFailed: 1, readFailed: 9));

        await rig.RunAsync();

        Assert.Contains("9", Assert.Single(rig.Log.Entries).Message, StringComparison.Ordinal);
    }

    // Case 2 + 5 + 6 combined: held moves never trigger; a failing summary adds its own line.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Held_moves_and_failing_summary_write_two_lines_and_never_trigger()
    {
        var rig = new Rig();
        rig.ClassifyBehavior = (_, _) => Task.FromResult(Report(brakeHeld: 3));
        rig.SummaryBehavior = _ => throw new InvalidOperationException(MarkerText);

        await rig.RunAsync();

        Assert.Equal(2, rig.Log.Entries.Count);
        Assert.Empty(rig.Triggers);
    }
}
