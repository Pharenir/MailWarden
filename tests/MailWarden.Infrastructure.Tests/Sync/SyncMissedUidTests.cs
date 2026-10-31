using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;

using MailWarden.Infrastructure.Sync;
namespace MailWarden.Infrastructure.Tests.Sync;

/// <summary>AP-03a fix.3 (G1): UIDs the server did not deliver are retried; initial-window floor. Cases 35-38 and 40.</summary>
[Trait("Category", "feature:sync")]
public sealed class SyncMissedUidTests
{
    private const string Inbox = "INBOX";

    private static byte[] Mail(string subject) => SyncTestEml.Plain(subject, "body");

    private static FakeMailFolder AddSevenMessages(SyncHarness harness)
    {
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        for (int index = 1; index <= 7; index++)
        {
            inbox.AddMessage(Mail($"m{index}"));
        }

        return inbox;
    }

    // Case 35: SEARCH delivered only UIDs 5-7 on the first run
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Incomplete_search_result_is_completed_by_the_next_run()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = AddSevenMessages(harness);
        foreach (uint hidden in new uint[] { 1, 2, 3, 4 })
        {
            inbox.SearchHiddenUids.Add(hidden);
        }

        SyncReport first = await harness.SyncAsync();

        Assert.Null(Assert.Single(first.Folders).Error);
        Assert.Equal(new uint[] { 5, 6, 7 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(0u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);

        inbox.SearchHiddenUids.Clear();
        SyncReport second = await harness.SyncWithFreshLogAsync();

        FolderSyncResult result = Assert.Single(second.Folders);
        Assert.Null(result.Error);
        Assert.Equal(4, result.Added);
        Assert.Equal(
            new[] { (Inbox, 1u), (Inbox, 2u), (Inbox, 3u), (Inbox, 4u) },
            harness.Server.Fetches.OrderBy(fetch => fetch.Uid).ToArray());
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 7 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(7, harness.EmlFileCount());
    }

    // Case 36: FETCH returned no summary (null) for UIDs 1-4 on the first run
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Null_fetch_results_are_completed_by_the_next_run()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = AddSevenMessages(harness);
        foreach (uint nullFetch in new uint[] { 1, 2, 3, 4 })
        {
            inbox.NullFetchUids.Add(nullFetch);
        }

        SyncReport first = await harness.SyncAsync();

        Assert.Null(Assert.Single(first.Folders).Error);
        Assert.Equal(new uint[] { 5, 6, 7 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(0u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);

        inbox.NullFetchUids.Clear();
        SyncReport second = await harness.SyncWithFreshLogAsync();

        Assert.Null(Assert.Single(second.Folders).Error);
        Assert.Equal(
            new[] { (Inbox, 1u), (Inbox, 2u), (Inbox, 3u), (Inbox, 4u) },
            harness.Server.Fetches.OrderBy(fetch => fetch.Uid).ToArray());
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 7 }, await harness.StoredUidsAsync(Inbox));
    }

    // Case 37: floor = min(window UIDs) - 1, a failed window UID is retried, older UIDs are never fetched
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Window_floor_is_set_below_the_first_window_uid_even_when_that_uid_fails()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(initialSyncDays: 30);
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        for (int index = 1; index <= 3; index++)
        {
            inbox.AddMessage(Mail($"old_{index}"), internalDate: SyncHarness.FixedNow.AddDays(-60));
        }

        for (int index = 4; index <= 6; index++)
        {
            inbox.AddMessage(Mail($"recent_{index}"), internalDate: SyncHarness.FixedNow.AddDays(-10));
        }

        harness.Server.FetchFailures[(Inbox, 4u)] =
            new MailTransportException(MailTransportFailure.Protocol, "Fetch rejected.");

        SyncReport first = await harness.SyncAsync();

        Assert.Equal("1 message(s) failed (first: MailTransportException)", Assert.Single(first.Folders).Error);
        Assert.Equal(new uint[] { 5, 6 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(3u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);
        Assert.DoesNotContain(harness.Server.Fetches, fetch => fetch.Uid <= 3);

        harness.Server.FetchFailures.Clear();
        SyncReport second = await harness.SyncWithFreshLogAsync();

        Assert.Null(Assert.Single(second.Folders).Error);
        Assert.Equal(new[] { (Inbox, 4u) }, harness.Server.Fetches.ToArray());
        Assert.Equal(new uint[] { 4, 5, 6 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(3u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);
    }

    // Case 38: nothing inside the window -> floor is the highest server UID
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Window_without_any_message_sets_the_floor_to_the_highest_server_uid()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(initialSyncDays: 30);
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        for (int index = 1; index <= 3; index++)
        {
            inbox.AddMessage(Mail($"old_{index}"), internalDate: SyncHarness.FixedNow.AddDays(-60));
        }

        SyncReport first = await harness.SyncAsync();

        Assert.Null(Assert.Single(first.Folders).Error);
        Assert.Empty(await harness.StoredUidsAsync(Inbox));
        Assert.Empty(harness.Server.Fetches);
        Assert.Equal(3u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);

        uint newUid = inbox.AddMessage(Mail("arrived_later"), internalDate: SyncHarness.FixedNow.AddDays(-1));
        SyncReport second = await harness.SyncWithFreshLogAsync();

        Assert.Null(Assert.Single(second.Folders).Error);
        Assert.Equal(4u, newUid);
        Assert.Equal(new[] { (Inbox, 4u) }, harness.Server.Fetches.ToArray());
        Assert.Equal(new uint[] { 4 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(3u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);
    }

    // Case 38 (supporting): empty folder with a window -> floor 0
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_folder_with_a_window_has_floor_zero()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(initialSyncDays: 30);
        harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);

        await harness.SyncAsync();

        Assert.Equal(0u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);
    }

    // Case 40: positive control - a second run without changes fetches nothing
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_run_without_changes_makes_no_fetch_calls()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        AddSevenMessages(harness);
        await harness.SyncAsync();
        Assert.Equal(7, harness.Server.Fetches.Count);

        SyncReport second = await harness.SyncWithFreshLogAsync();

        Assert.Empty(harness.Server.Fetches);
        Assert.DoesNotContain(harness.Server.Operations, operation => operation.StartsWith("Fetch:", StringComparison.Ordinal));
        Assert.Equal(0, Assert.Single(second.Folders).Added);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 7 }, await harness.StoredUidsAsync(Inbox));
    }
}
