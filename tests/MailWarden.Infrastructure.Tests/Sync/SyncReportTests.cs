using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Sync;

/// <summary>AP-03a case 27 (positive control: report equals store contents) and the end-to-end smoke test.</summary>
[Trait("Category", "feature:sync")]
public sealed class SyncReportTests
{
    private static byte[] Mail(string subject) => SyncTestEml.Plain(subject, "report body");

    // Case 27
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Full_pass_over_two_folders_with_mixed_changes_reports_exactly_what_changed()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox, condStore: true);
        FakeMailFolder junk = harness.Server.AddFolder("Junk", ServerFolderFlags.Junk, condStore: true);
        FakeMailFolder old = harness.Server.AddFolder("Old_Projects");
        for (int index = 1; index <= 3; index++)
        {
            inbox.AddMessage(Mail($"inbox_{index}"));
        }

        junk.AddMessage(Mail("junk_1"));
        junk.AddMessage(Mail("junk_2"));
        old.AddMessage(Mail("old_1"));
        SyncReport first = await harness.SyncAsync();
        Assert.Equal(3, first.FoldersAdded);
        Assert.Equal(new[] { 3, 2, 1 }, first.Folders.Select(folder => folder.Added).ToArray());
        Assert.Equal(6, harness.EmlFileCount());

        inbox.RemoveMessage(1);
        inbox.SetFlags(2, MessageFlags.Seen);
        inbox.AddMessage(Mail("inbox_4"));
        inbox.AddMessage(Mail("inbox_5"));
        junk.RemoveMessage(2);
        junk.SetFlags(1, MessageFlags.Flagged);
        junk.AddMessage(Mail("junk_3"));
        harness.Server.RemoveFolder("Old_Projects");

        SyncReport second = await harness.SyncAsync();

        Assert.Equal(harness.Account.Id, second.AccountId);
        Assert.Equal(0, second.FoldersAdded);
        Assert.Equal(1, second.FoldersRemoved);
        Assert.Equal(2, second.Folders.Count);
        FolderSyncResult inboxResult = second.Folders[0];
        FolderSyncResult junkResult = second.Folders[1];
        Assert.Equal(("INBOX", FolderRole.Inbox, 2, 1, 1, false, (string?)null), Flatten(inboxResult));
        Assert.Equal(("Junk", FolderRole.Junk, 1, 1, 1, false, (string?)null), Flatten(junkResult));

        Assert.Equal(new uint[] { 2, 3, 4, 5 }, await harness.StoredUidsAsync("INBOX"));
        Assert.Equal(new uint[] { 1, 3 }, await harness.StoredUidsAsync("Junk"));
        Assert.Equal(MessageFlags.Seen, (await harness.MessagesOfAsync("INBOX"))[0].Flags);
        Assert.Equal(MessageFlags.Flagged, (await harness.MessagesOfAsync("Junk"))[0].Flags);
        Assert.Null(await harness.FindFolderAsync("Old_Projects"));
        Assert.Equal(6, await harness.Storage.CountRowsAsync("messages"));
        Assert.Equal(6, harness.EmlFileCount());
        Assert.Equal(0u, (await harness.StateOfAsync("INBOX"))!.SkippedUpToUid);
        Assert.Equal(0u, (await harness.StateOfAsync("Junk"))!.SkippedUpToUid);
        Assert.Equal(inbox.HighestModSeq, (await harness.StateOfAsync("INBOX"))!.HighestModSeq);
    }

    // Case 27: the control must be able to fail - a third run without changes reports zeros everywhere
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Run_without_changes_reports_only_zeros()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox, condStore: true);
        FakeMailFolder junk = harness.Server.AddFolder("Junk", ServerFolderFlags.Junk, condStore: true);
        inbox.AddMessage(Mail("inbox_1"));
        junk.AddMessage(Mail("junk_1"));
        await harness.SyncAsync();

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(0, report.FoldersAdded);
        Assert.Equal(0, report.FoldersRemoved);
        Assert.All(report.Folders, folder => Assert.Equal((0, 0, 0, false, (string?)null), (folder.Added, folder.FlagsUpdated, folder.Deleted, folder.UidValidityReset, folder.Error)));
    }

    // Smoke: first sync of an INBOX with two messages end to end (fake transport, real stores)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Smoke_first_sync_stores_rows_files_and_state_for_an_inbox()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(initialSyncDays: 90);
        FakeMailFolder inbox = harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox);
        inbox.AddMessage(Mail("smoke_one"), internalDate: SyncHarness.FixedNow.AddDays(-1));
        inbox.AddMessage(Mail("smoke_two"), internalDate: SyncHarness.FixedNow.AddDays(-2));

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(1, report.FoldersAdded);
        Assert.Equal(2, Assert.Single(report.Folders).Added);
        Assert.Equal(2, (await harness.MessagesOfAsync("INBOX")).Count);
        Assert.Equal(2, harness.EmlFileCount());
        Assert.NotNull(await harness.StateOfAsync("INBOX"));
        Assert.Single(await harness.Storage.Messages.SearchAsync("smoke_one"));
    }

    private static (string Name, FolderRole Role, int Added, int FlagsUpdated, int Deleted, bool Reset, string? Error) Flatten(
        FolderSyncResult result) =>
        (result.FolderFullName, result.Role, result.Added, result.FlagsUpdated, result.Deleted, result.UidValidityReset, result.Error);
}
