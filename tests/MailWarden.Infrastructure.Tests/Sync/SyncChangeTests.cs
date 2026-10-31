using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Sync;

/// <summary>AP-03a rules R3 (UIDVALIDITY), R4 (deletions), R7 (flags) and cases 7, 8, 9, 10, 11.</summary>
[Trait("Category", "feature:sync")]
public sealed class SyncChangeTests
{
    private const string Inbox = "INBOX";

    private static async Task<(SyncHarness Harness, FakeMailFolder Folder)> StartAsync(bool condStore)
    {
        SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder folder = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox, condStore: condStore);
        return (harness, folder);
    }

    private static byte[] Mail(string subject, string body) => SyncTestEml.Plain(subject, body);

    // Case 7
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changed_server_flags_update_the_rows_and_leave_the_search_index_alone(bool condStore)
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(condStore);
        await using SyncHarness lifetime = harness;
        server.AddMessage(Mail("one_subject", "quokkaword"));
        server.AddMessage(Mail("two_subject", "wombatword"), MessageFlags.Answered | MessageFlags.Flagged);
        server.AddMessage(Mail("three_subject", "zebraword"), MessageFlags.Seen);
        await harness.SyncAsync();
        server.SetFlags(1, MessageFlags.Seen);
        server.SetFlags(2, MessageFlags.Flagged);

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Equal(2, result.FlagsUpdated);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Deleted);
        IReadOnlyList<MailMessage> rows = await harness.MessagesOfAsync(Inbox);
        Assert.Equal(MessageFlags.Seen, rows[0].Flags);
        Assert.Equal(MessageFlags.Flagged, rows[1].Flags);
        Assert.Equal(MessageFlags.Seen, rows[2].Flags);
        Assert.Equal("one_subject", rows[0].Subject);
        MailMessage found = Assert.Single(await harness.Storage.Messages.SearchAsync("quokkaword"));
        Assert.Equal(rows[0].Id, found.Id);
        Assert.Single(await harness.Storage.Messages.SearchAsync("wombatword"));
        Assert.Single(await harness.Storage.Messages.SearchAsync("zebraword"));
    }

    // Case 7 (R7): only the flag change is reported when exactly one message changed
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task One_seen_flag_added_reports_exactly_one_update()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(condStore: true);
        await using SyncHarness lifetime = harness;
        server.AddMessage(Mail("one_subject", "x"));
        server.AddMessage(Mail("two_subject", "x"));
        await harness.SyncAsync();
        server.SetFlags(2, MessageFlags.Seen);

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(1, Assert.Single(report.Folders).FlagsUpdated);
        Assert.Equal(MessageFlags.Seen, (await harness.MessagesOfAsync(Inbox))[1].Flags);
        Assert.Equal(MessageFlags.None, (await harness.MessagesOfAsync(Inbox))[0].Flags);
    }

    // Case 8
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task With_condstore_the_stored_mod_seq_is_passed_to_the_flag_fetch()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(condStore: true);
        await using SyncHarness lifetime = harness;
        server.AddMessage(Mail("one_subject", "x"));
        Assert.Equal(10UL, server.HighestModSeq);
        await harness.SyncAsync();
        Assert.Equal(10UL, (await harness.StateOfAsync(Inbox))!.HighestModSeq);
        server.SetFlags(1, MessageFlags.Seen);
        server.SetFlags(1, MessageFlags.Seen | MessageFlags.Flagged);
        Assert.Equal(12UL, server.HighestModSeq);

        SyncReport report = await harness.SyncWithFreshLogAsync();

        Assert.Equal(new ulong[] { 10 }, harness.Server.FlagFetchArguments.ToArray());
        Assert.Equal(1, Assert.Single(report.Folders).FlagsUpdated);
        Assert.Equal(12UL, (await harness.StateOfAsync(Inbox))!.HighestModSeq);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, (await harness.MessagesOfAsync(Inbox))[0].Flags);
    }

    // Case 9
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Without_condstore_all_flags_are_fetched()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(condStore: false);
        await using SyncHarness lifetime = harness;
        server.AddMessage(Mail("one_subject", "x"));
        server.AddMessage(Mail("two_subject", "x"));
        await harness.SyncAsync();
        Assert.Equal(0UL, (await harness.StateOfAsync(Inbox))!.HighestModSeq);
        server.SetFlags(1, MessageFlags.Seen);

        SyncReport report = await harness.SyncWithFreshLogAsync();

        Assert.Equal(new ulong[] { 0 }, harness.Server.FlagFetchArguments.ToArray());
        Assert.Equal(1, Assert.Single(report.Folders).FlagsUpdated);
        Assert.Equal(MessageFlags.Seen, (await harness.MessagesOfAsync(Inbox))[0].Flags);
    }

    // Case 9 (R7): stored mod-seq 0 but server now announces one -> full fetch
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Stored_mod_seq_zero_forces_a_full_flag_fetch_even_if_the_server_has_one_now()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(condStore: false);
        await using SyncHarness lifetime = harness;
        server.AddMessage(Mail("one_subject", "x"));
        await harness.SyncAsync();
        server.HighestModSeq = 5;

        await harness.SyncWithFreshLogAsync();

        Assert.Equal(new ulong[] { 0 }, harness.Server.FlagFetchArguments.ToArray());
    }

    // Case 9 (R7): stored mod-seq > 0 but server reports none now -> full fetch
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Server_mod_seq_zero_forces_a_full_flag_fetch_even_if_one_is_stored()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(condStore: true);
        await using SyncHarness lifetime = harness;
        server.AddMessage(Mail("one_subject", "x"));
        await harness.SyncAsync();
        Assert.True((await harness.StateOfAsync(Inbox))!.HighestModSeq > 0);
        server.HighestModSeq = 0;

        await harness.SyncWithFreshLogAsync();

        Assert.Equal(new ulong[] { 0 }, harness.Server.FlagFetchArguments.ToArray());
    }

    // Case 10
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Message_deleted_on_the_server_is_removed_with_its_file_and_search_entry()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(condStore: true);
        await using SyncHarness lifetime = harness;
        server.AddMessage(Mail("one_subject", "keepbodyword"));
        server.AddMessage(Mail("two_subject", "gonebodyword"));
        server.AddMessage(Mail("three_subject", "otherbodyword"));
        await harness.SyncAsync();
        MailFolder folder = await harness.GetFolderAsync(Inbox);
        string goneEmlPath = (await harness.Storage.Messages.GetUidEmlPathsAsync(folder.Id))[2]!;
        Assert.True(harness.Eml.Exists(goneEmlPath));
        server.RemoveMessage(2);

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Added);
        Assert.Equal(new uint[] { 1, 3 }, await harness.StoredUidsAsync(Inbox));
        Assert.False(harness.Eml.Exists(goneEmlPath));
        Assert.Equal(2, harness.EmlFileCount());
        Assert.Empty(await harness.Storage.Messages.SearchAsync("gonebodyword"));
        Assert.Single(await harness.Storage.Messages.SearchAsync("keepbodyword"));
    }

    // Case 10 (R4): a pending flag change and a deletion in the same run are both handled
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Deletion_and_flag_change_in_one_run_are_counted_separately()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(condStore: true);
        await using SyncHarness lifetime = harness;
        server.AddMessage(Mail("one_subject", "x"));
        server.AddMessage(Mail("two_subject", "x"));
        server.AddMessage(Mail("three_subject", "x"));
        await harness.SyncAsync();
        server.RemoveMessage(1);
        server.SetFlags(3, MessageFlags.Seen);

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.FlagsUpdated);
        Assert.Equal(new uint[] { 2, 3 }, await harness.StoredUidsAsync(Inbox));
    }

    // Case 10 (R4/R5): a message outside the initial window that is expunged later was never stored -> not a deletion
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Expunged_message_that_was_never_stored_is_not_counted_as_deleted()
    {
        SyncHarness harness = await SyncHarness.CreateAsync(initialSyncDays: 30);
        await using SyncHarness lifetime = harness;
        FakeMailFolder server = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        uint oldUid = server.AddMessage(Mail("old_subject", "x"), internalDate: SyncHarness.FixedNow.AddDays(-90));
        server.AddMessage(Mail("recent_subject", "x"), internalDate: SyncHarness.FixedNow.AddDays(-1));
        await harness.SyncAsync();
        server.RemoveMessage(oldUid);

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(0, Assert.Single(report.Folders).Deleted);
        Assert.Equal(new uint[] { 2 }, await harness.StoredUidsAsync(Inbox));
    }

    // Case 11
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Changed_uid_validity_wipes_the_folder_and_fetches_everything_again()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(condStore: true);
        await using SyncHarness lifetime = harness;
        server.AddMessage(Mail("old_one", "oldwordone"));
        server.AddMessage(Mail("old_two", "oldwordtwo"));
        server.AddMessage(Mail("old_three", "oldwordthree"));
        await harness.SyncAsync();
        Assert.Equal(3, harness.EmlFileCount());
        server.ResetUidValidity(200);
        server.AddMessage(Mail("new_one", "newwordone"));
        server.AddMessage(Mail("new_two", "newwordtwo"));

        SyncReport report = await harness.SyncWithFreshLogAsync();

        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.True(result.UidValidityReset);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Added);
        Assert.Equal(new uint[] { 1, 2 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(new[] { "new_one", "new_two" }, (await harness.MessagesOfAsync(Inbox)).Select(m => m.Subject).ToArray());
        Assert.Equal(new[] { (Inbox, 1u), (Inbox, 2u) }, harness.Server.Fetches.ToArray());
        Assert.Equal(2, harness.EmlFileCount());
        Assert.Empty(await harness.Storage.Messages.SearchAsync("oldwordthree"));
        Assert.Empty(await harness.Storage.Messages.SearchAsync("oldwordone"));
        Assert.Single(await harness.Storage.Messages.SearchAsync("newwordone"));
        Assert.Equal(200u, (await harness.GetFolderAsync(Inbox)).UidValidity);
        FolderSyncState? state = await harness.StateOfAsync(Inbox);
        Assert.Equal(0u, state!.SkippedUpToUid);
    }

    // Case 11 (R3): the sync after the reset sees a stable validity again
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Sync_after_the_reset_does_not_reset_again()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(condStore: true);
        await using SyncHarness lifetime = harness;
        server.AddMessage(Mail("old_one", "x"));
        await harness.SyncAsync();
        server.ResetUidValidity(200);
        server.AddMessage(Mail("new_one", "x"));
        await harness.SyncAsync();

        SyncReport report = await harness.SyncWithFreshLogAsync();

        Assert.False(Assert.Single(report.Folders).UidValidityReset);
        Assert.Empty(harness.Server.Fetches);
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Inbox));
    }

    // Case 11 (R3): a folder stored with UIDVALIDITY 0 is not a reset
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folder_stored_before_any_sync_with_validity_zero_is_not_reset()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(condStore: false);
        await using SyncHarness lifetime = harness;
        server.AddMessage(Mail("one_subject", "x"));
        await harness.Storage.Folders.UpsertAsync(TestData.NewFolder(harness.Account.Id, Inbox, FolderRole.Inbox, uidValidity: 0));

        SyncReport report = await harness.SyncAsync();

        Assert.False(Assert.Single(report.Folders).UidValidityReset);
        Assert.Equal(100u, (await harness.GetFolderAsync(Inbox)).UidValidity);
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Inbox));
    }

    // Case 11 (R3): other folders are untouched by a reset of one folder
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reset_of_one_folder_keeps_the_other_folder_intact()
    {
        (SyncHarness harness, FakeMailFolder inbox) = await StartAsync(condStore: false);
        await using SyncHarness lifetime = harness;
        FakeMailFolder junk = harness.Server.AddFolder("Junk", ServerFolderFlags.Junk);
        inbox.AddMessage(Mail("inbox_one", "x"));
        junk.AddMessage(Mail("junk_one", "junkkeepword"));
        await harness.SyncAsync();
        inbox.ResetUidValidity(300);
        inbox.AddMessage(Mail("inbox_new", "x"));

        SyncReport report = await harness.SyncWithFreshLogAsync();

        Assert.True(report.Folders.Single(folder => folder.FolderFullName == Inbox).UidValidityReset);
        Assert.False(report.Folders.Single(folder => folder.FolderFullName == "Junk").UidValidityReset);
        Assert.Single(await harness.StoredUidsAsync("Junk"));
        Assert.Single(await harness.Storage.Messages.SearchAsync("junkkeepword"));
        Assert.DoesNotContain(("Junk", 1u), harness.Server.Fetches);
    }
}
