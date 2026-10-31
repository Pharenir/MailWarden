using MailWarden.Core.Accounts;
using MailWarden.Core.Model;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Sync;

/// <summary>AP-03b B1/B2/B7: batched message fetch, fallback to single fetches, options and UID 0 (cases 1-6, 16).</summary>
[Trait("Category", "feature:sync")]
public sealed class SyncBatchTests
{
    private const string Inbox = "INBOX";

    private static byte[] Mail(string subject) => SyncTestEml.Plain(subject, "body");

    private static uint[] Range(int count) => Enumerable.Range(1, count).Select(uid => (uint)uid).ToArray();

    private static FakeMailFolder AddInbox(SyncHarness harness, int messageCount)
    {
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        for (int index = 1; index <= messageCount; index++)
        {
            inbox.AddMessage(Mail($"message_{index}"));
        }

        return inbox;
    }

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Missing_uids_are_fetched_in_ascending_batches_of_fifty_without_single_fetches()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        AddInbox(harness, 120);
        harness.UseOptions(new SyncOptions { FetchBatchSize = 50 });

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Null(result.Error);
        Assert.Equal(120, result.Added);
        Assert.Equal(new[] { 50, 50, 20 }, harness.Server.BatchCalls.Select(call => call.Uids.Length).ToArray());
        Assert.All(harness.Server.BatchCalls, call => Assert.Equal(Inbox, call.Folder));
        Assert.Equal(Range(120), harness.Server.BatchCalls.SelectMany(call => call.Uids).ToArray());
        Assert.Empty(harness.Server.SingleFetches);
        Assert.Equal(Range(120), await harness.StoredUidsAsync(Inbox));
        Assert.Equal(120, harness.EmlFileCount());
    }

    // Case 1 (default): the default batch size is 50 without configuring anything
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Default_options_use_batches_of_fifty()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        AddInbox(harness, 51);

        await harness.SyncAsync();

        Assert.Equal(new[] { 50, 1 }, harness.Server.BatchCalls.Select(call => call.Uids.Length).ToArray());
        Assert.Empty(harness.Server.SingleFetches);
    }

    // Case 1 (supporting): a small batch size is honoured
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Configured_batch_size_is_honoured()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        AddInbox(harness, 7);
        harness.UseOptions(new SyncOptions { FetchBatchSize = 3 });

        await harness.SyncAsync();

        Assert.Equal(new[] { 3, 3, 1 }, harness.Server.BatchCalls.Select(call => call.Uids.Length).ToArray());
        Assert.Equal(Range(7), await harness.StoredUidsAsync(Inbox));
    }

    // Assumption (B2): with nothing missing no batch call is made
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task No_batch_call_is_made_when_nothing_is_missing()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        AddInbox(harness, 3);
        await harness.SyncAsync();

        await harness.SyncWithFreshLogAsync();

        Assert.Empty(harness.Server.BatchCalls);
        Assert.Empty(harness.Server.Fetches);
    }

    // Case 2
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public void Fetch_batch_size_outside_1_to_500_throws(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SyncOptions { FetchBatchSize = value });
    }

    // Case 2
    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Max_reconnects_outside_0_to_10_throws(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SyncOptions { MaxReconnectsPerPass = value });
    }

    // Case 2
    [Fact]
    public void Defaults_are_fifty_and_three_and_boundaries_are_valid()
    {
        var defaults = new SyncOptions();

        Assert.Equal(50, defaults.FetchBatchSize);
        Assert.Equal(3, defaults.MaxReconnectsPerPass);
        Assert.Equal(1, new SyncOptions { FetchBatchSize = 1 }.FetchBatchSize);
        Assert.Equal(500, new SyncOptions { FetchBatchSize = 500 }.FetchBatchSize);
        Assert.Equal(0, new SyncOptions { MaxReconnectsPerPass = 0 }.MaxReconnectsPerPass);
        Assert.Equal(10, new SyncOptions { MaxReconnectsPerPass = 10 }.MaxReconnectsPerPass);
    }

    // Case 2 (source compatibility): the positional constructor still works together with the new init properties
    [Fact]
    public void Positional_constructor_stays_source_compatible()
    {
        var options = new SyncOptions(30) { FetchBatchSize = 10 };

        Assert.Equal(30, options.InitialSyncDays);
        Assert.Equal(10, options.FetchBatchSize);
    }

    // Case 3
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Uids_the_server_returns_nothing_for_are_skipped_without_error_and_retried_next_pass()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = AddInbox(harness, 10);
        inbox.NullFetchUids.Add(4);
        inbox.NullFetchUids.Add(7);

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Null(result.Error);
        Assert.Equal(8, result.Added);
        Assert.Equal(new uint[] { 1, 2, 3, 5, 6, 8, 9, 10 }, await harness.StoredUidsAsync(Inbox));

        inbox.NullFetchUids.Clear();
        SyncReport retry = await harness.SyncWithFreshLogAsync();

        Assert.Null(Assert.Single(retry.Folders).Error);
        Assert.Equal(new uint[] { 4, 7 }, harness.Server.BatchCalls.SelectMany(call => call.Uids).ToArray());
        Assert.Equal(Range(10), await harness.StoredUidsAsync(Inbox));
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Eml_write_failure_for_one_message_of_a_batch_does_not_abort_the_batch()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        AddInbox(harness, 10);
        harness.FaultyEml.FailWriteWhen = path => path.EndsWith("/5.eml", StringComparison.Ordinal);

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Equal("1 message(s) failed (first: StorageException)", result.Error);
        Assert.Equal(9, result.Added);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 6, 7, 8, 9, 10 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(9, harness.EmlFileCount());
        Assert.Empty(harness.Server.SingleFetches);

        harness.FaultyEml.FailWriteWhen = null;
        SyncReport retry = await harness.SyncWithFreshLogAsync();

        Assert.Null(Assert.Single(retry.Folders).Error);
        Assert.Equal(new uint[] { 5 }, harness.Server.BatchCalls.SelectMany(call => call.Uids).ToArray());
        Assert.Equal(Range(10), await harness.StoredUidsAsync(Inbox));
        Assert.Equal(10, harness.EmlFileCount());
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Batch_protocol_error_falls_back_to_single_fetches_for_every_uid_of_the_batch()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        AddInbox(harness, 10);
        harness.UseOptions(new SyncOptions { FetchBatchSize = 5 });
        harness.Server.BatchFailures[Inbox] = new MailTransportException(MailTransportFailure.Protocol, "Batch rejected.");

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Null(result.Error);
        Assert.Equal(10, result.Added);
        Assert.Equal(
            Range(10).Select(uid => (Inbox, uid)).ToArray(),
            harness.Server.SingleFetches.ToArray());
        Assert.Equal(Range(10), await harness.StoredUidsAsync(Inbox));
    }

    // Case 5 (supporting): only the UIDs not yet delivered by the failing batch are fetched singly
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Fallback_fetches_only_the_uids_the_failed_batch_did_not_deliver()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        AddInbox(harness, 5);
        harness.Server.FetchFailures[(Inbox, 3u)] = new MailTransportException(MailTransportFailure.Protocol, "Rejected.");

        // The failure is transient: the single fetch of UID 3 (second attempt) succeeds.
        int attempts = 0;
        harness.Server.OnFetch = (_, uid) =>
        {
            if (uid == 3 && ++attempts == 2)
            {
                harness.Server.FetchFailures.Clear();
            }
        };

        SyncReport report = await harness.SyncAsync();

        Assert.Null(Assert.Single(report.Folders).Error);
        Assert.Equal(new[] { (Inbox, 3u), (Inbox, 4u), (Inbox, 5u) }, harness.Server.SingleFetches.ToArray());
        Assert.Equal(Range(5), await harness.StoredUidsAsync(Inbox));
    }

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Batch_protocol_error_with_one_failing_single_stores_the_others_and_reports_one_failure()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        AddInbox(harness, 10);
        harness.Server.BatchFailures[Inbox] = new MailTransportException(MailTransportFailure.Protocol, "Batch rejected.");
        harness.Server.FetchFailures[(Inbox, 6u)] = new MailTransportException(MailTransportFailure.Protocol, "Message rejected.");

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Equal("1 message(s) failed (first: MailTransportException)", result.Error);
        Assert.Equal(9, result.Added);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 7, 8, 9, 10 }, await harness.StoredUidsAsync(Inbox));

        harness.Server.BatchFailures.Clear();
        harness.Server.FetchFailures.Clear();
        SyncReport retry = await harness.SyncWithFreshLogAsync();

        Assert.Null(Assert.Single(retry.Folders).Error);
        Assert.Equal(new uint[] { 6 }, harness.Server.BatchCalls.SelectMany(call => call.Uids).ToArray());
        Assert.Equal(Range(10), await harness.StoredUidsAsync(Inbox));
    }

    // Case 16 (B7)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Uid_zero_in_the_window_search_fails_that_folder_and_stores_nothing()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(initialSyncDays: 30);
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        FakeMailFolder junk = harness.Server.AddFolder("Junk", ServerFolderFlags.Junk);
        inbox.AddMessage(Mail("inbox_one"), internalDate: SyncHarness.FixedNow.AddDays(-1));
        inbox.WindowSearchExtraUids.Add(0);
        junk.AddMessage(Mail("junk_one"), internalDate: SyncHarness.FixedNow.AddDays(-1));

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult inboxResult = report.Folders.Single(folder => folder.FolderFullName == Inbox);
        FolderSyncResult junkResult = report.Folders.Single(folder => folder.FolderFullName == "Junk");
        Assert.NotNull(inboxResult.Error);
        Assert.StartsWith("MailTransportException", inboxResult.Error, StringComparison.Ordinal);
        Assert.Null(junkResult.Error);
        Assert.DoesNotContain(harness.Server.Fetches, fetch => fetch.Folder == Inbox);
        MailFolder? storedInbox = await harness.FindFolderAsync(Inbox);
        if (storedInbox is not null)
        {
            Assert.Empty(await harness.Storage.Messages.GetUidFlagsAsync(storedInbox.Id));
            Assert.Null(await harness.SyncStates.GetAsync(storedInbox.Id));
        }

        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync("Junk"));
    }
}
