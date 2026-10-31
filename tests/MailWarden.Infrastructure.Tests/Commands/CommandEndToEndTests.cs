using System.Reflection;
using MailWarden.Core.Accounts;
using MailWarden.Core.Commands;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Commands;

/// <summary>
/// AP-03c case 22 (sync pass stays read-only, write transport is a separate interface) and case 23 (positive
/// control: queue, executor and a real sync pass over a fake server that really moves and expunges).
/// </summary>
[Trait("Category", "feature:sync")]
public sealed class CommandEndToEndTests
{
    private const string TrashName = "[Gmail]/Bin";

    private const string JunkName = "[Gmail]/Spam";

    /// <summary>Server with INBOX (3 messages), Archive, Trash and Junk, synced once into the local store.</summary>
    private static async Task<CommandHarness> CreateSyncedAsync(int junkMessages = 0)
    {
        CommandHarness harness = await CommandHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder("INBOX", ServerFolderFlags.Inbox);
        harness.Server.AddFolder("Archive", ServerFolderFlags.Archive);
        harness.Server.AddFolder(TrashName, ServerFolderFlags.Trash);
        FakeMailFolder junk = harness.Server.AddFolder(JunkName, ServerFolderFlags.Junk);
        for (int index = 1; index <= 3; index++)
        {
            inbox.AddMessage(SyncTestEml.Plain($"inbox_{index}", $"body {index}", messageId: $"<inbox_{index}@example.test>"));
        }

        for (int index = 1; index <= junkMessages; index++)
        {
            junk.AddMessage(SyncTestEml.Plain($"junk_{index}", $"junk body {index}", messageId: $"<junk_{index}@example.test>"));
        }

        SyncReport report = await harness.RunSyncAsync();
        Assert.All(report.Folders, folder => Assert.Null(folder.Error));
        Assert.Equal(FolderRole.Trash, (await harness.Sync.GetFolderAsync(TrashName)).Role);
        Assert.Equal(FolderRole.Junk, (await harness.Sync.GetFolderAsync(JunkName)).Role);
        Assert.Equal(FolderRole.Archive, (await harness.Sync.GetFolderAsync("Archive")).Role);
        return harness;
    }

    private static async Task<string[]> SubjectsAsync(CommandHarness harness, string folder) =>
        (await harness.Sync.MessagesOfAsync(folder)).Select(message => message.Subject).OrderBy(subject => subject, StringComparer.Ordinal).ToArray();

    // Case 22
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_sync_pass_never_opens_a_write_session_and_only_uses_read_operations()
    {
        await using CommandHarness harness = await CreateSyncedAsync(junkMessages: 1);

        await harness.RunSyncAsync();

        Assert.Equal(0, harness.Server.CommandConnectCalls);
        Assert.Empty(harness.Server.CommandCalls);
        Assert.All(
            harness.Server.Operations,
            operation => Assert.Matches("^(List|Open:.+|Search|FetchFlags:\\d+|Fetch:.+|FetchBatch:.+)$", operation));
    }

    [Fact]
    public void The_read_only_transport_still_has_only_read_members()
    {
        string[] methods = typeof(IMailTransport).GetMethods().Where(method => !method.IsSpecialName)
            .Select(method => method.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[] { "FetchFlagsAsync", "FetchMessageAsync", "FetchMessagesAsync", "ListFoldersAsync", "OpenFolderAsync", "SearchUidsAsync" },
            methods);
    }

    [Fact]
    public void The_write_transport_is_a_separate_interface_without_a_plain_expunge_or_close()
    {
        Assert.False(typeof(IMailCommandTransport).IsAssignableFrom(typeof(IMailTransport)));
        Assert.False(typeof(IMailTransport).IsAssignableFrom(typeof(IMailCommandTransport)));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IMailCommandTransport)));
        string[] methods = typeof(IMailCommandTransport).GetMethods().Where(method => !method.IsSpecialName)
            .Select(method => method.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "AddFlagsAsync", "ExpungeAsync", "MoveAsync", "RemoveFlagsAsync", "SelectFolderAsync" },
            methods);
        MethodInfo expunge = typeof(IMailCommandTransport).GetMethod("ExpungeAsync")!;
        Assert.Contains(expunge.GetParameters(), parameter => parameter.ParameterType == typeof(IReadOnlyList<uint>));
    }

    [Fact]
    public void The_sync_engine_does_not_depend_on_the_command_queue()
    {
        ConstructorInfo constructor = Assert.Single(typeof(SyncEngine).GetConstructors());

        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType.Namespace == "MailWarden.Core.Commands");
        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IMailCommandTransport));
    }

    // Case 23
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_moved_message_leaves_the_source_and_appears_in_the_target_after_the_next_sync()
    {
        await using CommandHarness harness = await CreateSyncedAsync();
        MailFolder inbox = await harness.Sync.GetFolderAsync("INBOX");
        MailFolder archive = await harness.Sync.GetFolderAsync("Archive");
        Assert.Equal(new uint[] { 1, 2, 3 }, await harness.LocalUidsAsync(inbox));
        Assert.Empty(await harness.LocalUidsAsync(archive));
        await harness.Queue.EnqueueMoveAsync(inbox.Id, [1, 3], archive.Id);

        CommandRunReport report = await harness.ExecuteAsync();
        SyncReport sync = await harness.RunSyncAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.All(sync.Folders, folder => Assert.Null(folder.Error));
        Assert.Equal(new uint[] { 2 }, harness.Server.ServerUids("INBOX"));
        Assert.Equal(new uint[] { 2 }, await harness.LocalUidsAsync(inbox));
        Assert.Equal(new[] { "inbox_1", "inbox_3" }, await SubjectsAsync(harness, "Archive"));
        Assert.Equal(new[] { "inbox_2" }, await SubjectsAsync(harness, "INBOX"));
        foreach (MailMessage message in await harness.Sync.MessagesOfAsync("Archive"))
        {
            Assert.NotEmpty(await harness.Sync.ReadEmlAsync(message));
        }
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_message_moved_to_trash_appears_in_the_trash_after_the_next_sync()
    {
        await using CommandHarness harness = await CreateSyncedAsync();
        MailFolder inbox = await harness.Sync.GetFolderAsync("INBOX");
        await harness.Queue.EnqueueMoveToTrashAsync(inbox.Id, [2]);

        await harness.ExecuteAsync();
        SyncReport sync = await harness.RunSyncAsync();

        Assert.All(sync.Folders, folder => Assert.Null(folder.Error));
        Assert.Equal(new[] { "inbox_2" }, await SubjectsAsync(harness, TrashName));
        Assert.Equal(new[] { "inbox_1", "inbox_3" }, await SubjectsAsync(harness, "INBOX"));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_permanently_deleted_message_is_gone_everywhere_after_the_next_sync()
    {
        await using CommandHarness harness = await CreateSyncedAsync();
        MailFolder inbox = await harness.Sync.GetFolderAsync("INBOX");
        await harness.Queue.EnqueueDeletePermanentlyAsync(inbox.Id, [1]);

        CommandRunReport report = await harness.ExecuteAsync();
        SyncReport sync = await harness.RunSyncAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.All(sync.Folders, folder => Assert.Null(folder.Error));
        Assert.Equal(new[] { "inbox_2", "inbox_3" }, await SubjectsAsync(harness, "INBOX"));
        Assert.Empty(await SubjectsAsync(harness, TrashName));
        Assert.Empty(harness.Server.ServerUids(TrashName));
        Assert.Equal(new uint[] { 2, 3 }, harness.Server.ServerUids("INBOX"));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Emptying_the_junk_folder_leaves_every_other_folder_unchanged()
    {
        await using CommandHarness harness = await CreateSyncedAsync(junkMessages: 3);
        MailFolder junk = await harness.Sync.GetFolderAsync(JunkName);
        await harness.Queue.EnqueueEmptyFolderAsync(junk.Id);

        CommandRunReport report = await harness.ExecuteAsync();
        SyncReport sync = await harness.RunSyncAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.All(sync.Folders, folder => Assert.Null(folder.Error));
        Assert.Empty(harness.Server.ServerUids(JunkName));
        Assert.Empty(await harness.LocalUidsAsync(junk));
        Assert.Equal(new uint[] { 1, 2, 3 }, harness.Server.ServerUids("INBOX"));
        Assert.Equal(new[] { "inbox_1", "inbox_2", "inbox_3" }, await SubjectsAsync(harness, "INBOX"));
        Assert.Equal(new[] { "Select:" + JunkName, "Expunge:" + JunkName + ":1,2,3" }, harness.Server.CommandCallTexts.ToArray());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_flag_change_is_visible_on_the_server_and_stays_stable_after_the_next_sync()
    {
        await using CommandHarness harness = await CreateSyncedAsync();
        MailFolder inbox = await harness.Sync.GetFolderAsync("INBOX");
        await harness.Queue.EnqueueSetFlagsAsync(inbox.Id, [1, 2], MessageFlags.Seen | MessageFlags.Flagged, MessageFlags.None);

        await harness.ExecuteAsync();
        SyncReport sync = await harness.RunSyncAsync();

        Assert.All(sync.Folders, folder => Assert.Null(folder.Error));
        IReadOnlyDictionary<uint, MessageFlags> local = await harness.Storage.Messages.GetUidFlagsAsync(inbox.Id);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, local[1]);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, local[2]);
        Assert.Equal(MessageFlags.None, local[3]);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, harness.Server.Folder("INBOX").Messages[1].Flags);
    }
}
