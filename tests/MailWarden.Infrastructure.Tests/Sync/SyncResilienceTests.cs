using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Sync;

/// <summary>AP-03a fix.2 decisions F1-F4 and cases 29-33 (state rule changed by fix.3 G2: SkippedUpToUid stays 0).</summary>
[Trait("Category", "feature:sync")]
public sealed class SyncResilienceTests
{
    private const string Inbox = "INBOX";

    private static byte[] Mail(string subject) => SyncTestEml.Plain(subject, "body");

    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Fetch_failure_for_one_uid_stores_the_others_and_retries_only_that_uid()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        inbox.AddMessage(Mail("one"));
        inbox.AddMessage(Mail("two"));
        inbox.AddMessage(Mail("three"));
        harness.Server.FetchFailures[(Inbox, 2u)] = new MailTransportException(MailTransportFailure.Protocol, "Fetch rejected.");

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Equal("1 message(s) failed (first: MailTransportException)", result.Error);
        Assert.Equal(2, result.Added);
        Assert.Equal(new uint[] { 1, 3 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(0u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);

        harness.Server.FetchFailures.Clear();
        SyncReport retry = await harness.SyncWithFreshLogAsync();

        Assert.Null(Assert.Single(retry.Folders).Error);
        Assert.Equal(new[] { (Inbox, 2u) }, harness.Server.Fetches.ToArray());
        Assert.Equal(new uint[] { 1, 2, 3 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(0u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);
    }

    // Case 29 (fix.3): the first UID fails -> floor stays 0, next run fetches only it
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failure_of_the_first_uid_keeps_the_skipped_floor_at_zero()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        inbox.AddMessage(Mail("one"));
        inbox.AddMessage(Mail("two"));
        harness.Server.FetchFailures[(Inbox, 1u)] = new MailTransportException(MailTransportFailure.Protocol, "Fetch rejected.");

        SyncReport report = await harness.SyncAsync();

        Assert.Equal("1 message(s) failed (first: MailTransportException)", Assert.Single(report.Folders).Error);
        Assert.Equal(new uint[] { 2 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(0u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);

        harness.Server.FetchFailures.Clear();
        await harness.SyncWithFreshLogAsync();

        Assert.Equal(new[] { (Inbox, 1u) }, harness.Server.Fetches.ToArray());
        Assert.Equal(new uint[] { 1, 2 }, await harness.StoredUidsAsync(Inbox));
    }

    // Case 29 (fix.3): two failures are counted; they no longer influence the state (floor 0)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Several_failures_are_counted_and_do_not_change_the_state()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        for (int index = 1; index <= 5; index++)
        {
            inbox.AddMessage(Mail($"m{index}"));
        }

        harness.Server.FetchFailures[(Inbox, 2u)] = new MailTransportException(MailTransportFailure.Protocol, "Rejected.");
        harness.Server.FetchFailures[(Inbox, 4u)] = new MailTransportException(MailTransportFailure.Protocol, "Rejected.");

        SyncReport report = await harness.SyncAsync();

        Assert.Equal("2 message(s) failed (first: MailTransportException)", Assert.Single(report.Folders).Error);
        Assert.Equal(new uint[] { 1, 3, 5 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(0u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);
    }

    // Case 29 / R7: flag sync still runs when a message failed
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Flag_sync_still_runs_when_a_message_failed()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        inbox.AddMessage(Mail("one"));
        await harness.SyncAsync();
        inbox.SetFlags(1, MessageFlags.Seen);
        inbox.AddMessage(Mail("two"));
        harness.Server.FetchFailures[(Inbox, 2u)] = new MailTransportException(MailTransportFailure.Protocol, "Rejected.");

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(1, Assert.Single(report.Folders).FlagsUpdated);
        Assert.Equal(MessageFlags.Seen, (await harness.MessagesOfAsync(Inbox))[0].Flags);
    }

    // Case 29: network errors still abort (R9 unchanged)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Network_error_for_one_uid_still_aborts_the_sync()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        inbox.AddMessage(Mail("one"));
        inbox.AddMessage(Mail("two"));
        harness.Server.FetchFailures[(Inbox, 2u)] = new MailTransportException(MailTransportFailure.Network, "Connection lost.");

        await Assert.ThrowsAsync<MailTransportException>(() => harness.SyncAsync());
    }

    // Case 30
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Eml_write_failure_for_one_uid_is_handled_like_a_fetch_failure()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        inbox.AddMessage(Mail("one"));
        inbox.AddMessage(Mail("two"));
        inbox.AddMessage(Mail("three"));
        harness.FaultyEml.FailWriteWhen = path => path.EndsWith("/2.eml", StringComparison.Ordinal);

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Equal("1 message(s) failed (first: StorageException)", result.Error);
        Assert.Equal(new uint[] { 1, 3 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(0u, (await harness.StateOfAsync(Inbox))!.SkippedUpToUid);
        Assert.Equal(2, harness.EmlFileCount());

        harness.FaultyEml.FailWriteWhen = null;
        SyncReport retry = await harness.SyncWithFreshLogAsync();

        Assert.Null(Assert.Single(retry.Folders).Error);
        Assert.Equal(new[] { (Inbox, 2u) }, harness.Server.Fetches.ToArray());
        Assert.Equal(new uint[] { 1, 2, 3 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(3, harness.EmlFileCount());
    }

    // Case 31
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Removed_folder_with_a_locked_eml_file_is_still_removed_and_the_sync_continues()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        FakeMailFolder doomed = harness.Server.AddFolder("Projects");
        doomed.AddMessage(Mail("locked"));
        doomed.AddMessage(Mail("free"));
        await harness.SyncAsync();
        MailFolder stored = await harness.GetFolderAsync("Projects");
        IReadOnlyDictionary<uint, string?> paths = await harness.Storage.Messages.GetUidEmlPathsAsync(stored.Id);
        string lockedPath = Path.Combine(harness.EmlRoot, paths[1]!.Replace('/', Path.DirectorySeparatorChar));
        harness.Server.RemoveFolder("Projects");

        SyncReport report;
        await using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            report = await harness.SyncAsync();
        }

        Assert.Equal(1, report.FoldersRemoved);
        Assert.Null(await harness.FindFolderAsync("Projects"));
        Assert.NotNull(await harness.FindFolderAsync(Inbox));
        Assert.Contains(Inbox, report.Folders.Select(folder => folder.FolderFullName));
        Assert.False(harness.Eml.Exists(paths[2]!));
    }

    // Case 32
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Uid_validity_zero_is_a_protocol_error_for_that_folder_only()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox, uidValidity: 0);
        FakeMailFolder junk = harness.Server.AddFolder("Junk", ServerFolderFlags.Junk);
        inbox.AddMessage(Mail("inbox_one"));
        junk.AddMessage(Mail("junk_one"));

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult inboxResult = report.Folders.Single(folder => folder.FolderFullName == Inbox);
        FolderSyncResult junkResult = report.Folders.Single(folder => folder.FolderFullName == "Junk");
        Assert.NotNull(inboxResult.Error);
        Assert.StartsWith("MailTransportException", inboxResult.Error, StringComparison.Ordinal);
        Assert.Null(junkResult.Error);
        Assert.Equal(1, junkResult.Added);
        Assert.DoesNotContain(harness.Server.Fetches, fetch => fetch.Folder == Inbox);
        MailFolder? storedInbox = await harness.FindFolderAsync(Inbox);
        if (storedInbox is not null)
        {
            Assert.Empty(await harness.Storage.Messages.GetUidFlagsAsync(storedInbox.Id));
            Assert.Null(await harness.SyncStates.GetAsync(storedInbox.Id));
        }

        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync("Junk"));
    }

    // Case 33
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folder_list_without_inbox_throws_protocol_error_and_leaves_stored_folders_untouched()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        FakeMailFolder sent = harness.Server.AddFolder("Sent", ServerFolderFlags.Sent);
        inbox.AddMessage(Mail("inbox_one"));
        sent.AddMessage(Mail("sent_one"));
        await harness.SyncAsync();
        harness.Server.RemoveFolder(Inbox);

        MailTransportException exception =
            await Assert.ThrowsAsync<MailTransportException>(() => harness.SyncAsync());

        Assert.Equal(MailTransportFailure.Protocol, exception.Failure);
        Assert.Equal(
            new[] { Inbox, "Sent" },
            (await harness.Storage.Folders.ListAsync(harness.Account.Id)).Select(folder => folder.FullName).ToArray());
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync("Sent"));
        Assert.Equal(2, harness.EmlFileCount());
        Assert.Equal(1, harness.Server.Transports.Last().DisposeCalls);
    }

    // Case 33: also on a fresh account
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folder_list_without_inbox_stores_nothing_on_a_fresh_account()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder("Sent", ServerFolderFlags.Sent);

        MailTransportException exception =
            await Assert.ThrowsAsync<MailTransportException>(() => harness.SyncAsync());

        Assert.Equal(MailTransportFailure.Protocol, exception.Failure);
        Assert.Empty(await harness.Storage.Folders.ListAsync(harness.Account.Id));
        Assert.Empty(harness.Server.OpenedFolders);
    }
}
