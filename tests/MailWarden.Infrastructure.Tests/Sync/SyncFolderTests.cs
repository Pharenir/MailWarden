using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Sync;

/// <summary>AP-03a rules R1 (folders), R2 (order), R10 (read-only) and cases 1, 2, 3, 19, 20, 21.</summary>
[Trait("Category", "feature:sync")]
public sealed class SyncFolderTests
{
    private static byte[] SampleMail(string subject = "sample_subject", string body = "samplebodyword") =>
        SyncTestEml.Plain(subject, body);

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task First_sync_stores_visible_folders_with_roles_and_skips_hidden_ones()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox);
        harness.Server.AddFolder("Sent", ServerFolderFlags.Sent);
        harness.Server.AddFolder("[Gmail]/All Mail", ServerFolderFlags.All);
        harness.Server.AddFolder("[Gmail]", ServerFolderFlags.NoSelect);

        SyncReport report = await harness.SyncAsync();

        IReadOnlyList<MailFolder> stored = await harness.Storage.Folders.ListAsync(harness.Account.Id);
        Assert.Equal(new[] { "INBOX", "Sent" }, stored.Select(folder => folder.FullName).ToArray());
        Assert.Equal(FolderRole.Inbox, stored.Single(folder => folder.FullName == "INBOX").Role);
        Assert.Equal(FolderRole.Sent, stored.Single(folder => folder.FullName == "Sent").Role);
        Assert.Equal(2, report.FoldersAdded);
        Assert.Equal(0, report.FoldersRemoved);
        Assert.Equal(harness.Account.Id, report.AccountId);
    }

    // Case 1 / R1: hidden folders are never opened
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Hidden_folders_are_never_opened_on_the_server()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox);
        harness.Server.AddFolder("[Gmail]/All Mail", ServerFolderFlags.All);
        harness.Server.AddFolder("[Gmail]", ServerFolderFlags.NoSelect | ServerFolderFlags.NonExistent);
        harness.Server.AddFolder("[Gmail]/Important", ServerFolderFlags.Important);

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(new[] { "INBOX" }, harness.Server.OpenedFolders.ToArray());
        Assert.Equal(new[] { "INBOX" }, report.Folders.Select(folder => folder.FolderFullName).ToArray());
    }

    // R1: role comes from the resolver (Microsoft: top-level "Archive" without flags)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Role_comes_from_the_folder_role_resolver_of_the_account_provider()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync(provider: MailProvider.Microsoft);
        harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox);
        harness.Server.AddFolder("Archive");
        harness.Server.AddFolder("Notes");
        harness.Server.AddFolder("Projects");

        await harness.SyncAsync();

        Assert.Equal(FolderRole.Archive, (await harness.GetFolderAsync("Archive")).Role);
        Assert.Equal(FolderRole.None, (await harness.GetFolderAsync("Projects")).Role);
        Assert.Null(await harness.FindFolderAsync("Notes"));
    }

    // Case 1 + R3: the folder is stored with the server UIDVALIDITY after the first sync
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task First_sync_stores_the_server_uid_validity()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox, uidValidity: 4711);

        await harness.SyncAsync();

        Assert.Equal(4711u, (await harness.GetFolderAsync("INBOX")).UidValidity);
    }

    // Case 2
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folder_that_vanished_on_the_server_is_removed_with_rows_files_and_state()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox);
        FakeMailFolder custom = harness.Server.AddFolder("Projects");
        custom.AddMessage(SampleMail(body: "projectbodyword"));
        custom.AddMessage(SampleMail());
        await harness.SyncAsync();
        MailFolder storedBefore = await harness.GetFolderAsync("Projects");
        IReadOnlyDictionary<uint, string?> paths = await harness.Storage.Messages.GetUidEmlPathsAsync(storedBefore.Id);
        Assert.Equal(2, paths.Count);
        Assert.All(paths.Values, path => Assert.True(harness.Eml.Exists(path!)));
        Assert.NotNull(await harness.SyncStates.GetAsync(storedBefore.Id));
        harness.Server.RemoveFolder("Projects");

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(1, report.FoldersRemoved);
        Assert.Equal(0, report.FoldersAdded);
        Assert.Null(await harness.FindFolderAsync("Projects"));
        Assert.NotNull(await harness.FindFolderAsync("INBOX"));
        Assert.Empty(await harness.Storage.Messages.GetUidFlagsAsync(storedBefore.Id));
        Assert.Empty(await harness.Storage.Messages.SearchAsync("projectbodyword"));
        Assert.Null(await harness.SyncStates.GetAsync(storedBefore.Id));
        Assert.All(paths.Values, path => Assert.False(harness.Eml.Exists(path!)));
        Assert.Equal(0, harness.EmlFileCount());
    }

    // Case 3
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folder_that_becomes_hidden_is_removed_like_a_vanished_one()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox);
        FakeMailFolder label = harness.Server.AddFolder("Label_X");
        label.AddMessage(SampleMail(body: "labelbodyword"));
        await harness.SyncAsync();
        MailFolder storedBefore = await harness.GetFolderAsync("Label_X");
        string path = (await harness.Storage.Messages.GetUidEmlPathsAsync(storedBefore.Id)).Values.Single()!;
        label.Info = label.Info with { Flags = ServerFolderFlags.NoSelect };

        SyncReport report = await harness.SyncWithFreshLogAsync();

        Assert.Equal(1, report.FoldersRemoved);
        Assert.Null(await harness.FindFolderAsync("Label_X"));
        Assert.Empty(await harness.Storage.Messages.SearchAsync("labelbodyword"));
        Assert.Null(await harness.SyncStates.GetAsync(storedBefore.Id));
        Assert.False(harness.Eml.Exists(path));
        Assert.DoesNotContain("Label_X", harness.Server.OpenedFolders);
    }

    // Case 3 (variant): a folder that turns into a role-All folder (Gmail All Mail) is hidden as well
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folder_that_gets_the_All_flag_is_removed()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder folder = harness.Server.AddFolder("Everything");
        folder.AddMessage(SampleMail());
        await harness.SyncAsync();
        folder.Info = folder.Info with { Flags = ServerFolderFlags.All };

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(1, report.FoldersRemoved);
        Assert.Null(await harness.FindFolderAsync("Everything"));
        Assert.Equal(0, harness.EmlFileCount());
    }

    // R1: unchanged folder list -> nothing added or removed on the second run
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_sync_with_unchanged_folder_list_adds_and_removes_nothing()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox);
        harness.Server.AddFolder("Sent", ServerFolderFlags.Sent);
        await harness.SyncAsync();
        MailFolder inboxBefore = await harness.GetFolderAsync("INBOX");

        SyncReport second = await harness.SyncAsync();

        Assert.Equal(0, second.FoldersAdded);
        Assert.Equal(0, second.FoldersRemoved);
        Assert.Equal(inboxBefore.Id, (await harness.GetFolderAsync("INBOX")).Id);
    }

    // Case 19
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Inbox_is_synced_first_then_junk_then_the_rest_by_name_ignoring_case()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder("Zed");
        harness.Server.AddFolder("beta");
        harness.Server.AddFolder("Sent", ServerFolderFlags.Sent);
        harness.Server.AddFolder("[Gmail]/Spam", ServerFolderFlags.Junk);
        harness.Server.AddFolder("Alpha");
        harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox);

        SyncReport report = await harness.SyncAsync();

        string[] expected = ["INBOX", "[Gmail]/Spam", "Alpha", "beta", "Sent", "Zed"];
        Assert.Equal(expected, harness.Server.OpenedFolders.ToArray());
        Assert.Equal(expected, report.Folders.Select(folder => folder.FolderFullName).ToArray());
    }

    // Case 19 (supporting): the report carries the resolved role per folder
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Report_lists_the_role_of_every_synced_folder()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox);
        harness.Server.AddFolder("[Gmail]/Spam", ServerFolderFlags.Junk);
        harness.Server.AddFolder("Alpha");

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(
            new[] { FolderRole.Inbox, FolderRole.Junk, FolderRole.None },
            report.Folders.Select(folder => folder.Role).ToArray());
    }

    // Case 20 / R10
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Sync_leaves_the_server_untouched_and_does_not_set_seen()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox, condStore: true);
        uint unreadUid = inbox.AddMessage(SampleMail());
        uint answeredUid = inbox.AddMessage(SampleMail(), MessageFlags.Answered);
        ulong modSeqBefore = inbox.HighestModSeq;

        await harness.SyncAsync();
        await harness.SyncAsync();

        Assert.Equal(MessageFlags.None, inbox.Messages[unreadUid].Flags);
        Assert.Equal(MessageFlags.Answered, inbox.Messages[answeredUid].Flags);
        Assert.Equal(2, inbox.Messages.Count);
        Assert.Equal(modSeqBefore, inbox.HighestModSeq);
        Assert.Equal(MessageFlags.None, (await harness.Storage.Messages.GetByUidAsync((await harness.GetFolderAsync("INBOX")).Id, unreadUid))!.Flags);
    }

    // Case 20: the transport contract offers read operations only
    [Fact]
    public void Transport_contract_has_no_write_operation()
    {
        string[] methodNames = typeof(IMailTransport)
            .GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            nameof(IMailTransport.FetchFlagsAsync),
            nameof(IMailTransport.FetchMessageAsync),
            nameof(IMailTransport.ListFoldersAsync),
            nameof(IMailTransport.OpenFolderAsync),
            nameof(IMailTransport.SearchUidsAsync),
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), methodNames);
    }

    // Case 20: only list/open/search/fetch calls are made; folder opens are exactly the synced folders
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Engine_uses_only_read_calls_and_opens_each_folder_once_per_run()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox);
        inbox.AddMessage(SampleMail());
        harness.Server.AddFolder("Sent", ServerFolderFlags.Sent);

        await harness.SyncAsync();

        Assert.All(
            harness.Server.Operations,
            operation => Assert.Matches("^(List|Open:.+|Search|FetchFlags:\\d+|Fetch:.+:\\d+)$", operation));
        Assert.Equal(new[] { "INBOX", "Sent" }, harness.Server.OpenedFolders.ToArray());
    }

    // Case 21
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unknown_account_id_throws_ArgumentException()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => harness.Engine.SyncAccountAsync(Guid.NewGuid()));
    }

    // Case 26
    [Fact]
    public void Negative_initial_sync_days_throw_ArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SyncOptions(-1));
    }

    // Case 26 (supporting): 0 and the default are valid
    [Fact]
    public void Zero_and_default_initial_sync_days_are_valid()
    {
        Assert.Equal(0, new SyncOptions(0).InitialSyncDays);
        Assert.Equal(90, new SyncOptions().InitialSyncDays);
    }
}
