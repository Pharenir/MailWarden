using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Composition;

/// <summary>AP-06 D5 / cases 14, 15: order sync, harvest, classify in <see cref="ClassifyingSyncCycle"/>.</summary>
[Trait("Category", "feature:contacts")]
[Trait("Category", "feature:classifier")]
[Trait("Category", "feature:sync")]
public sealed class ClassifyingSyncCycleHarvestTests
{
    private const string MarkerText = "marker_harvest_failure_text";

    private const string PassingAuthentication =
        "mx.google.com; dkim=pass header.i=@paypal.test header.s=s1; spf=pass smtp.mailfrom=bounce@paypal.test; " +
        "dmarc=pass header.from=paypal.test";

    private static readonly Guid AccountId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private sealed class Rig
    {
        public Rig()
        {
            var syncReport = new SyncReport(AccountId, 0, 0, []);
            SyncResult = syncReport;
            Cycle = new ClassifyingSyncCycle(
                (_, _, _) =>
                {
                    Order.Add("sync");
                    return Task.FromResult(syncReport);
                },
                (accountId, ct) =>
                {
                    Order.Add("harvest");
                    HarvestArguments.Add((accountId, ct));
                    return HarvestBehavior(accountId, ct);
                },
                (accountId, ct) =>
                {
                    Order.Add("classify");
                    return Task.FromResult(Report());
                },
                _ => Order.Add("trigger"),
                _ => Order.Add("summary"),
                Log);
        }

        public ClassifyingSyncCycle Cycle { get; }

        public SyncReport SyncResult { get; }

        public RecordingCriticalLog Log { get; } = new();

        public List<string> Order { get; } = [];

        public List<(Guid AccountId, CancellationToken Ct)> HarvestArguments { get; } = [];

        public Func<Guid, CancellationToken, Task<ContactHarvestReport>> HarvestBehavior { get; set; } =
            (_, _) => Task.FromResult(new ContactHarvestReport(0, 0, 0));

        public Task<SyncReport> RunAsync(CancellationToken ct = default) => Cycle.RunAsync(AccountId, null, ct);
    }

    private static ClassificationPassReport Report() =>
        new(
            Classified: 0,
            SkippedKnown: 0,
            ReadFailed: 0,
            MovesEnqueued: 0,
            RetentionDeletesEnqueued: 0,
            ActSuspendedNoContacts: false,
            TierCounts: new Dictionary<SpamTier, int>(),
            RetentionDeletesRejected: 0,
            EvaluationFailed: 0,
            BrakeHeld: 0);

    // Case 14
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Cycle_runs_sync_then_harvest_then_classify_then_summary()
    {
        var rig = new Rig();

        SyncReport result = await rig.RunAsync();

        Assert.Same(rig.SyncResult, result);
        Assert.Equal(new[] { "sync", "harvest", "classify", "summary" }, rig.Order);
        Assert.Empty(rig.Log.Entries);
    }

    // Case 14: the account and the token reach the harvest function
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Account_and_cancellation_token_are_handed_to_harvest()
    {
        var rig = new Rig();
        using var source = new CancellationTokenSource();

        await rig.RunAsync(source.Token);

        (Guid accountId, CancellationToken token) = Assert.Single(rig.HarvestArguments);
        Assert.Equal(AccountId, accountId);
        Assert.Equal(source.Token, token);
    }

    // Case 14: harvest throws -> classify not called, one log line with the type, no message text, propagates
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Harvest_failure_skips_classify_logs_the_type_name_only_and_propagates()
    {
        var rig = new Rig();
        var failure = new InvalidOperationException(MarkerText);
        rig.HarvestBehavior = (_, _) => throw failure;

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.RunAsync());

        Assert.Same(failure, thrown);
        Assert.Equal(new[] { "sync", "harvest" }, rig.Order);
        (string level, string source, string message) = Assert.Single(rig.Log.Entries);
        Assert.Equal(CriticalLogLevel.Critical, level);
        Assert.False(string.IsNullOrWhiteSpace(source));
        Assert.Contains("InvalidOperationException", message, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerText, rig.Log.AllText, StringComparison.Ordinal);
    }

    // Case 14: a failure that surfaces asynchronously is handled the same way
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Asynchronously_failing_harvest_is_logged_by_type_name_and_propagates()
    {
        var rig = new Rig();
        rig.HarvestBehavior = async (_, _) =>
        {
            await Task.Yield();
            throw new FormatException(MarkerText);
        };

        await Assert.ThrowsAsync<FormatException>(() => rig.RunAsync());

        Assert.DoesNotContain("classify", rig.Order);
        Assert.Contains("FormatException", Assert.Single(rig.Log.Entries).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerText, rig.Log.AllText, StringComparison.Ordinal);
    }

    // Case 14: cancellation is a stop, not a failure (like classify)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cancellation_during_harvest_propagates_without_a_log_line_and_skips_classify()
    {
        var rig = new Rig();
        using var source = new CancellationTokenSource();
        rig.HarvestBehavior = (_, ct) =>
        {
            source.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ContactHarvestReport(0, 0, 0));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.RunAsync(source.Token));

        Assert.Empty(rig.Log.Entries);
        Assert.Equal(new[] { "sync", "harvest" }, rig.Order);
    }

    // Case 14: a cancellation exception with an uncancelled token (e.g. timeout) is a failure
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Timeout_like_cancellation_exception_in_harvest_is_logged_and_propagates()
    {
        var rig = new Rig();
        rig.HarvestBehavior = (_, _) => throw new TaskCanceledException(MarkerText);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.RunAsync());

        Assert.Contains("TaskCanceledException", Assert.Single(rig.Log.Entries).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerText, rig.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("classify", rig.Order);
    }

    // Case 14: a failing sync does not reach harvest
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Sync_failure_skips_harvest_and_classify()
    {
        var harvestCalled = false;
        var log = new RecordingCriticalLog();
        var cycle = new ClassifyingSyncCycle(
            (_, _, _) => throw new IOException(MarkerText),
            (_, _) =>
            {
                harvestCalled = true;
                return Task.FromResult(new ContactHarvestReport(0, 0, 0));
            },
            (_, _) => Task.FromResult(Report()),
            _ => { },
            _ => { },
            log);

        await Assert.ThrowsAsync<IOException>(() => cycle.RunAsync(AccountId, null, CancellationToken.None));

        Assert.False(harvestCalled);
        Assert.Empty(log.Entries);
    }

    /// <summary>
    /// Real harvester and real pass on the harness, the "sync" function stores one sent mail and one incoming mail
    /// from the sent mail's recipient. With <paramref name="withHarvest"/> false the harvest function does nothing.
    /// </summary>
    private static async Task<ClassificationLogEntry> RunCycleWithSentAndIncomingAsync(bool withHarvest)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.SeedMarkerAsync(harness.Inbox);
        ContactHarvester harvester = harness.CreateHarvester();
        ClassificationPass pass = harness.CreatePass(ClassificationHarness.RecordOnly);
        MailMessage? incoming = null;
        var cycle = new ClassifyingSyncCycle(
            async (_, _, _) =>
            {
                await harness.AddSentAsync(harness.Sent!, 1, to: "Recipient A <recipient_a@paypal.test>");
                incoming = await harness.AddRawAsync(
                    harness.Inbox,
                    11,
                    ClassificationTestEml.Build("Recipient A <recipient_a@paypal.test>", [PassingAuthentication], "Your paypal statement is ready."),
                    "<incoming_a@example.test>");
                return new SyncReport(harness.Account.Id, 0, 0, []);
            },
            withHarvest ? harvester.RunAsync : (_, _) => Task.FromResult(new ContactHarvestReport(0, 0, 0)),
            pass.RunAsync,
            _ => { },
            _ => { },
            new RecordingCriticalLog());

        await cycle.RunAsync(harness.Account.Id, null, CancellationToken.None);

        return await harness.RequireEntryAsync(incoming!);
    }

    // Case 15
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Contact_collected_in_this_cycle_counts_for_this_cycles_classification()
    {
        ClassificationLogEntry entry = await RunCycleWithSentAndIncomingAsync(withHarvest: true);

        Assert.Equal(SpamTier.TrustedContact, entry.Tier);
        Assert.Equal(AuthStatus.Authenticated, entry.AuthStatus);
    }

    // Case 15: positive control, without the harvest the same incoming mail is not trusted
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Without_the_harvest_the_same_incoming_mail_is_not_trusted()
    {
        ClassificationLogEntry entry = await RunCycleWithSentAndIncomingAsync(withHarvest: false);

        Assert.NotEqual(SpamTier.TrustedContact, entry.Tier);
    }
}
