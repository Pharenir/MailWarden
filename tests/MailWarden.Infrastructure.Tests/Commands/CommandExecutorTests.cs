using MailWarden.Core.Commands;
using MailWarden.Infrastructure.Commands;

namespace MailWarden.Infrastructure.Tests.Commands;

/// <summary>
/// AP-03c cases 6–13 (executor per kind, UIDVALIDITY guard, idle run) with the safety assertions of the contract:
/// every deleting call carries exactly the command's UIDs (or the mapped Trash UIDs), nothing else is touched.
/// </summary>
[Trait("Category", "feature:sync")]
public sealed class CommandExecutorTests
{
    private static string[] Texts(CommandHarness harness) => harness.Server.CommandCallTexts.ToArray();

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task SetFlags_add_selects_the_folder_adds_exactly_the_uids_and_updates_the_local_flags()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 3);
        await harness.Queue.EnqueueSetFlagsAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [2, 1], MessageFlags.Seen, MessageFlags.None);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.Equal(new[] { "Select:INBOX", "AddFlags:INBOX:1,2:Seen" }, Texts(harness));
        IReadOnlyDictionary<uint, MessageFlags> local = await harness.Storage.Messages.GetUidFlagsAsync(inbox.Folder.Id);
        Assert.Equal(MessageFlags.Seen, local[1]);
        Assert.Equal(MessageFlags.Seen, local[2]);
        Assert.Equal(MessageFlags.None, local[3]);
        Assert.Equal(MessageFlags.None, inbox.Server.Messages[3].Flags);
        Assert.Empty(await harness.ListAsync());
        Assert.Equal(1, harness.Server.CommandConnectCalls);
        Assert.Equal(0, harness.Server.ConnectCalls);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task SetFlags_add_and_remove_adds_first_and_keeps_the_other_stored_flags()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox);
        uint first = await harness.AddMessageAsync(inbox, MessageFlags.Seen | MessageFlags.Answered);
        uint second = await harness.AddMessageAsync(inbox, MessageFlags.Seen);
        await harness.Queue.EnqueueSetFlagsAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [first, second], MessageFlags.Flagged, MessageFlags.Seen);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.Equal(
            new[] { "Select:INBOX", "AddFlags:INBOX:1,2:Flagged", "RemoveFlags:INBOX:1,2:Seen" },
            Texts(harness));
        IReadOnlyDictionary<uint, MessageFlags> local = await harness.Storage.Messages.GetUidFlagsAsync(inbox.Folder.Id);
        Assert.Equal(MessageFlags.Answered | MessageFlags.Flagged, local[first]);
        Assert.Equal(MessageFlags.Flagged, local[second]);
        Assert.Equal(MessageFlags.Answered | MessageFlags.Flagged, inbox.Server.Messages[first].Flags);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task SetFlags_changes_the_local_store_only_for_locally_stored_uids()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        uint serverOnly = await harness.AddMessageAsync(inbox);
        await harness.Queue.EnqueueSetFlagsAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [1, serverOnly], MessageFlags.Seen, MessageFlags.None);
        await harness.Messages.DeleteByUidsAsync(inbox.Folder.Id, [serverOnly]); // a sync removed the local row meanwhile

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.Equal(new uint[] { 1 }, await harness.LocalUidsAsync(inbox.Folder));
        Assert.Equal(MessageFlags.Seen, inbox.Server.Messages[serverOnly].Flags);
    }

    // Case 7
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Move_moves_exactly_the_uids_and_removes_source_rows_and_eml_files()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 3);
        SeededFolder archive = await harness.SeedFolderAsync("Archive", FolderRole.Archive);
        string[] movedPaths = await harness.EmlPathsAsync(inbox.Folder, 1, 2);
        string[] keptPaths = await harness.EmlPathsAsync(inbox.Folder, 3);
        await harness.Queue.EnqueueMoveAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [1, 2], archive.Folder.Id);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.Equal(new[] { "Select:INBOX", "Move:INBOX:1,2>Archive" }, Texts(harness));
        Assert.Equal(new uint[] { 3 }, await harness.LocalUidsAsync(inbox.Folder));
        Assert.Equal(2, movedPaths.Length);
        Assert.All(movedPaths, path => Assert.False(harness.EmlExists(path)));
        Assert.All(keptPaths, path => Assert.True(harness.EmlExists(path)));
        Assert.Empty(await harness.LocalUidsAsync(archive.Folder));
        Assert.Equal(new uint[] { 3 }, harness.Server.ServerUids("INBOX"));
        Assert.Equal(2, harness.Server.ServerUids("Archive").Length);
        Assert.Empty(await harness.ListAsync());
    }

    // Case 8
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task MoveToTrash_moves_to_the_trash_folder_and_removes_source_rows_and_eml_files()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 3);
        SeededFolder trash = await harness.SeedFolderAsync("Trash", FolderRole.Trash);
        string[] movedPaths = await harness.EmlPathsAsync(inbox.Folder, 2);
        await harness.Queue.EnqueueMoveToTrashAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [2]);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.Equal(new[] { "Select:INBOX", "Move:INBOX:2>Trash" }, Texts(harness));
        Assert.Equal(new uint[] { 1, 3 }, await harness.LocalUidsAsync(inbox.Folder));
        Assert.All(movedPaths, path => Assert.False(harness.EmlExists(path)));
        Assert.Equal(new uint[] { 1, 3 }, harness.Server.ServerUids("INBOX"));
        Assert.Single(harness.Server.ServerUids("Trash"));
        Assert.Empty(await harness.LocalUidsAsync(trash.Folder));
        Assert.DoesNotContain(harness.Server.CommandCalls, call => call.Kind == CommandCall.Expunge);
    }

    // Case 9
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Junk, "Junk")]
    [InlineData(FolderRole.Trash, "Trash")]
    public async Task DeletePermanently_in_junk_or_trash_expunges_exactly_the_uids_and_never_moves(FolderRole role, string name)
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder folder = await harness.SeedFolderAsync(name, role, messageCount: 4);
        string[] deletedPaths = await harness.EmlPathsAsync(folder.Folder, 1, 3);
        string[] keptPaths = await harness.EmlPathsAsync(folder.Folder, 2, 4);
        await harness.Queue.EnqueueDeletePermanentlyAsync(folder.Folder.Id, folder.Folder.UidValidity, [3, 1]);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.Equal(new[] { $"Select:{name}", $"Expunge:{name}:1,3" }, Texts(harness));
        Assert.DoesNotContain(harness.Server.CommandCalls, call => call.Kind == CommandCall.Move);
        Assert.Equal(new uint[] { 2, 4 }, harness.Server.ServerUids(name));
        Assert.Equal(new uint[] { 2, 4 }, await harness.LocalUidsAsync(folder.Folder));
        Assert.All(deletedPaths, path => Assert.False(harness.EmlExists(path)));
        Assert.All(keptPaths, path => Assert.True(harness.EmlExists(path)));
        Assert.Empty(await harness.ListAsync());
        harness.AssertExpungesOnly(new Dictionary<string, uint[]> { [name] = [1, 3] });
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task EmptyFolder_never_touches_a_message_that_arrived_on_the_server_after_enqueue()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder junk = await harness.SeedFolderAsync("Junk", FolderRole.Junk, messageCount: 3);
        await harness.EnqueueEmptyConfirmedAsync(junk.Folder.Id);
        uint late = junk.Server.AddMessage(SyncTestEml.Plain("late_arrival", "body"));

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.Equal(new[] { "Select:Junk", "Expunge:Junk:1,2,3" }, Texts(harness));
        Assert.Equal(new[] { late }, harness.Server.ServerUids("Junk"));
        Assert.Empty(await harness.LocalUidsAsync(junk.Folder));
    }

    // Case 10
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Inbox, "INBOX")]
    [InlineData(FolderRole.Archive, "Archive")]
    [InlineData(FolderRole.None, "Projects")]
    public async Task DeletePermanently_elsewhere_moves_to_trash_then_expunges_only_the_mapped_trash_uids(FolderRole role, string name)
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder source = await harness.SeedFolderAsync(name, role, messageCount: 3);
        SeededFolder trash = await harness.SeedFolderAsync("Trash", FolderRole.Trash);
        uint preExisting = trash.Server.AddMessage(SyncTestEml.Plain("old_in_trash", "body"));
        string[] deletedPaths = await harness.EmlPathsAsync(source.Folder, 1, 3);
        await harness.Queue.EnqueueDeletePermanentlyAsync(source.Folder.Id, source.Folder.UidValidity, [1, 3]);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        CommandCall[] calls = harness.Server.CommandCalls.ToArray();
        string[] texts = calls.Select(call => call.Text).ToArray();
        Assert.Equal($"Select:{name}", texts[0]);
        Assert.Equal($"Move:{name}:1,3>Trash", texts[1]);
        int expungeIndex = Array.FindIndex(calls, call => call.Kind == CommandCall.Expunge);
        Assert.Equal(calls.Length - 1, expungeIndex);
        Assert.Equal("Trash", calls[expungeIndex].Folder);
        Assert.Equal(new uint[] { 2, 3 }, calls[expungeIndex].Uids.OrderBy(uid => uid).ToArray());
        Assert.Single(calls, call => call.Kind == CommandCall.Expunge);
        Assert.Equal(new[] { preExisting }, harness.Server.ServerUids("Trash"));
        Assert.Equal(new uint[] { 2 }, harness.Server.ServerUids(name));
        Assert.Equal(new uint[] { 2 }, await harness.LocalUidsAsync(source.Folder));
        Assert.All(deletedPaths, path => Assert.False(harness.EmlExists(path)));
        Assert.Empty(await harness.ListAsync());
        harness.AssertExpungesOnly(new Dictionary<string, uint[]> { ["Trash"] = [2, 3] });
    }

    // Case 11
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task DeletePermanently_with_an_empty_copyuid_map_expunges_nothing_and_completes()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 3);
        SeededFolder trash = await harness.SeedFolderAsync("Trash", FolderRole.Trash);
        uint preExisting = trash.Server.AddMessage(SyncTestEml.Plain("old_in_trash", "body"));
        harness.Server.OmitCopyUid = true;
        await harness.Queue.EnqueueDeletePermanentlyAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [1, 2]);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.DoesNotContain(harness.Server.CommandCalls, call => call.Kind == CommandCall.Expunge);
        Assert.Equal(3, harness.Server.ServerUids("Trash").Length);
        Assert.Contains(preExisting, harness.Server.ServerUids("Trash"));
        Assert.Equal(new uint[] { 3 }, harness.Server.ServerUids("INBOX"));
        Assert.Equal(new uint[] { 3 }, await harness.LocalUidsAsync(inbox.Folder));
        Assert.Empty(await harness.ListAsync());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task DeletePermanently_with_a_partial_copyuid_map_expunges_only_the_mapped_uid()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 2);
        SeededFolder trash = await harness.SeedFolderAsync("Trash", FolderRole.Trash);
        harness.Server.OmitCopyUidFor.Add(2);
        await harness.Queue.EnqueueDeletePermanentlyAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [1, 2]);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        CommandCall expunge = Assert.Single(harness.Server.CommandCalls, call => call.Kind == CommandCall.Expunge);
        Assert.Equal("Trash", expunge.Folder);
        Assert.Equal(new uint[] { 1 }, expunge.Uids);
        Assert.Single(harness.Server.ServerUids("Trash"));
        Assert.Empty(harness.Server.ServerUids("INBOX"));
        Assert.Empty(await harness.LocalUidsAsync(inbox.Folder));
        Assert.Empty(await harness.LocalUidsAsync(trash.Folder));
        Assert.Empty(await harness.ListAsync());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task DeletePermanently_without_a_trash_folder_fails_permanently_and_sends_nothing_destructive()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 2);
        await harness.Queue.EnqueueDeletePermanentlyAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [1, 2]);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(0, 0, 1), report);
        Assert.DoesNotContain(harness.Server.CommandCalls, call => call.Kind is CommandCall.Move or CommandCall.Expunge);
        Assert.Equal(new uint[] { 1, 2 }, harness.Server.ServerUids("INBOX"));
        Assert.Equal(new uint[] { 1, 2 }, await harness.LocalUidsAsync(inbox.Folder));
        MailCommand command = await harness.SingleCommandAsync();
        Assert.Equal(MailCommandStatus.Failed, command.Status);
        Assert.Empty(await harness.Queue.ListDueAsync(harness.Account.Id, harness.Advance(TimeSpan.FromDays(1))));
    }

    // Case 12
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("SetFlags")]
    [InlineData("Move")]
    [InlineData("MoveToTrash")]
    [InlineData("DeleteJunk")]
    [InlineData("DeleteInbox")]
    public async Task Uid_validity_mismatch_makes_no_write_call_and_fails_the_command_permanently(string scenario)
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        bool inJunk = scenario == "DeleteJunk";
        SeededFolder source = inJunk
            ? await harness.SeedFolderAsync("Junk", FolderRole.Junk, messageCount: 2)
            : await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 2);
        SeededFolder archive = await harness.SeedFolderAsync("Archive", FolderRole.Archive);
        await harness.SeedFolderAsync("Trash", FolderRole.Trash);
        _ = scenario switch
        {
            "SetFlags" => await harness.Queue.EnqueueSetFlagsAsync(source.Folder.Id, source.Folder.UidValidity, [1, 2], MessageFlags.Seen, MessageFlags.None),
            "Move" => await harness.Queue.EnqueueMoveAsync(source.Folder.Id, source.Folder.UidValidity, [1, 2], archive.Folder.Id),
            "MoveToTrash" => await harness.Queue.EnqueueMoveToTrashAsync(source.Folder.Id, source.Folder.UidValidity, [1, 2]),
            _ => await harness.Queue.EnqueueDeletePermanentlyAsync(source.Folder.Id, source.Folder.UidValidity, [1, 2]),
        };
        string[] paths = await harness.EmlPathsAsync(source.Folder, 1, 2);
        source.Server.UidValidity = 999;

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(0, 0, 1), report);
        Assert.NotEmpty(harness.Server.CommandCalls);
        Assert.All(harness.Server.CommandCalls, call => Assert.Equal(CommandCall.Select, call.Kind));
        Assert.Empty(harness.Server.WriteCalls);
        Assert.Equal(new uint[] { 1, 2 }, harness.Server.ServerUids(source.Folder.FullName));
        Assert.Empty(harness.Server.ServerUids("Archive"));
        Assert.Empty(harness.Server.ServerUids("Trash"));
        Assert.Equal(new uint[] { 1, 2 }, await harness.LocalUidsAsync(source.Folder));
        Assert.All(paths, path => Assert.True(harness.EmlExists(path)));
        MailCommand command = await harness.SingleCommandAsync();
        Assert.Equal(MailCommandStatus.Failed, command.Status);
        Assert.NotNull(command.LastError);
        Assert.Contains("UIDVALIDITY", command.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await harness.Queue.ListDueAsync(harness.Account.Id, harness.Advance(TimeSpan.FromDays(1))));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Uid_validity_mismatch_of_one_command_does_not_stop_the_next_command()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder junk = await harness.SeedFolderAsync("Junk", FolderRole.Junk, messageCount: 1);
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        await harness.Queue.EnqueueDeletePermanentlyAsync(junk.Folder.Id, junk.Folder.UidValidity, [1]);
        harness.Advance(TimeSpan.FromSeconds(1));
        await harness.Queue.EnqueueSetFlagsAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [1], MessageFlags.Seen, MessageFlags.None);
        junk.Server.UidValidity = 999;

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 1), report);
        Assert.Equal(new uint[] { 1 }, harness.Server.ServerUids("Junk"));
        Assert.Equal(MessageFlags.Seen, inbox.Server.Messages[1].Flags);
        Assert.DoesNotContain(harness.Server.CommandCalls, call => call.Kind == CommandCall.Expunge);
    }

    // Case 13
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task No_command_means_no_connection()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(0, 0, 0), report);
        Assert.Equal(0, harness.Server.CommandConnectCalls);
        Assert.Equal(0, harness.Server.ConnectCalls);
        Assert.Empty(harness.Server.CommandTransports);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Only_not_yet_due_or_failed_commands_mean_no_connection()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 2);
        MailCommand later = await harness.Queue.EnqueueSetFlagsAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [1], MessageFlags.Seen, MessageFlags.None);
        MailCommand dead = await harness.Queue.EnqueueSetFlagsAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [2], MessageFlags.Seen, MessageFlags.None);
        await harness.Queue.RecordFailureAsync(later.Id, "ProtocolFailure", harness.Clock.Now + TimeSpan.FromMinutes(10), permanent: false);
        await harness.Queue.RecordFailureAsync(dead.Id, "ProtocolFailure", null, permanent: true);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(0, 0, 0), report);
        Assert.Equal(0, harness.Server.CommandConnectCalls);
        Assert.Equal(2, (await harness.ListAsync()).Count);
    }

    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(0, false)]
    [InlineData(21, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(20, true)]
    public async Task Max_attempts_must_be_between_1_and_20(int maxAttempts, bool valid)
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();

        if (valid)
        {
            Assert.NotNull(harness.CreateExecutor(maxAttempts));
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => harness.CreateExecutor(maxAttempts));
        }
    }
}
