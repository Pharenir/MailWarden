using Microsoft.Data.Sqlite;
using MailWarden.Core.Commands;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Commands;

namespace MailWarden.Infrastructure.Tests.Commands;

/// <summary>AP-03c fix.2 cases 26–31: expected UIDVALIDITY, locally stored UIDs, COPYUID map checks, corrupt rows.</summary>
[Trait("Category", "feature:sync")]
[Trait("Category", "feature:storage")]
public sealed class CommandHardeningTests
{
    private const string SecretMarker = "SECRET_MARKER_c41d02";

    // Case 26
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_wrong_expected_uid_validity_is_rejected_for_all_four_methods_and_nothing_is_stored()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        uint wrong = fixture.Inbox.UidValidity + 1;
        long inbox = fixture.Inbox.Id;

        ArgumentException[] errors =
        [
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueSetFlagsAsync(inbox, wrong, [1], MessageFlags.Seen, MessageFlags.None)),
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueMoveAsync(inbox, wrong, [1], fixture.Archive.Id)),
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueMoveToTrashAsync(inbox, wrong, [1])),
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueDeletePermanentlyAsync(inbox, wrong, [1])),
        ];

        Assert.All(errors, error => Assert.Equal("expectedUidValidity", error.ParamName));
        Assert.Equal(0, await fixture.CommandRowCountAsync());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_matching_expected_uid_validity_is_accepted_and_stored()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        MailCommand command = await fixture.Queue.EnqueueDeletePermanentlyAsync(fixture.Inbox.Id, fixture.Inbox.UidValidity, [1]);

        Assert.Equal(fixture.Inbox.UidValidity, command.FolderUidValidity);
    }

    // Case 27
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_uid_that_is_not_stored_locally_is_rejected_for_all_four_methods_and_nothing_is_stored()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        uint valid = fixture.Inbox.UidValidity;
        long inbox = fixture.Inbox.Id;

        ArgumentException[] errors =
        [
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueSetFlagsAsync(inbox, valid, [1, 500], MessageFlags.Seen, MessageFlags.None)),
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueMoveAsync(inbox, valid, [1, 500], fixture.Archive.Id)),
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueMoveToTrashAsync(inbox, valid, [500])),
            await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueDeletePermanentlyAsync(inbox, valid, [2, 500])),
        ];

        Assert.All(errors, error => Assert.Equal("uids", error.ParamName));
        Assert.Equal(0, await fixture.CommandRowCountAsync());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_uid_stored_in_another_folder_only_is_rejected()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        await fixture.SeedAsync(fixture.Junk, 77);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueDeletePermanentlyAsync(
            fixture.Inbox.Id, fixture.Inbox.UidValidity, [77]));

        Assert.Equal(0, await fixture.CommandRowCountAsync());
    }

    // Case 28
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("DuplicateDestination")]
    [InlineData("KeyOutsideCommand")]
    [InlineData("ZeroDestination")]
    [InlineData("ValidityZero")]
    public async Task An_implausible_copyuid_map_expunges_nothing_and_the_command_completes(string defect)
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 3);
        SeededFolder trash = await harness.SeedFolderAsync("Trash", FolderRole.Trash);
        uint preExisting = trash.Server.AddMessage(SyncTestEml.Plain("old_in_trash", "body"));
        harness.Server.CopyUidMapTransform = map => defect switch
        {
            "DuplicateDestination" => new CopyUidMap(map.DestinationUidValidity, new Dictionary<uint, uint> { [1] = 5, [2] = 5 }),
            "KeyOutsideCommand" => new CopyUidMap(map.DestinationUidValidity, new Dictionary<uint, uint>(map.Uids) { [3] = preExisting }),
            "ZeroDestination" => new CopyUidMap(map.DestinationUidValidity, new Dictionary<uint, uint> { [1] = 0, [2] = 2 }),
            _ => new CopyUidMap(0, map.Uids),
        };
        await harness.Queue.EnqueueDeletePermanentlyAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [1, 2]);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.DoesNotContain(harness.Server.CommandCalls, call => call.Kind == CommandCall.Expunge);
        Assert.DoesNotContain(harness.Server.CommandCalls, call => call is { Kind: CommandCall.Select, Folder: "Trash" });
        Assert.Contains(preExisting, harness.Server.ServerUids("Trash"));
        Assert.Equal(3, harness.Server.ServerUids("Trash").Length);
        Assert.Equal(new uint[] { 3 }, await harness.LocalUidsAsync(inbox.Folder));
        Assert.Empty(await harness.ListAsync());
    }

    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_map_validity_that_differs_from_the_trash_select_expunges_nothing_and_the_command_completes()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 2);
        SeededFolder trash = await harness.SeedFolderAsync("Trash", FolderRole.Trash, uidValidity: 100);
        harness.Server.CopyUidMapTransform = map => new CopyUidMap(777, map.Uids);
        await harness.Queue.EnqueueDeletePermanentlyAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [1, 2]);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.DoesNotContain(harness.Server.CommandCalls, call => call.Kind == CommandCall.Expunge);
        Assert.Equal(2, harness.Server.ServerUids("Trash").Length);
        Assert.Empty(await harness.LocalUidsAsync(inbox.Folder));
        Assert.Empty(await harness.LocalUidsAsync(trash.Folder));
        Assert.Empty(await harness.ListAsync());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_map_whose_validity_equals_the_trash_select_still_expunges_the_mapped_uids()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 2);
        await harness.SeedFolderAsync("Trash", FolderRole.Trash, uidValidity: 321);
        await harness.Queue.EnqueueDeletePermanentlyAsync(inbox.Folder.Id, inbox.Folder.UidValidity, [1, 2]);

        await harness.ExecuteAsync();

        CommandCall expunge = Assert.Single(harness.Server.CommandCalls, call => call.Kind == CommandCall.Expunge);
        Assert.Equal("Trash", expunge.Folder);
        Assert.Equal(new uint[] { 1, 2 }, expunge.Uids.OrderBy(uid => uid).ToArray());
        Assert.Empty(harness.Server.ServerUids("Trash"));
    }

    // Case 30 (executor part). The best-effort removal of \Deleted after a failed UID EXPUNGE (F3) lives inside the
    // MailKit transport and cannot be expressed against the fake; only the executor behaviour is pinned here.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failed_expunge_records_the_original_failure_and_the_executor_sends_no_further_write_call()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder junk = await harness.SeedFolderAsync("Junk", FolderRole.Junk, messageCount: 2);
        await harness.Queue.EnqueueDeletePermanentlyAsync(junk.Folder.Id, junk.Folder.UidValidity, [1]);
        harness.Server.CommandFailure = call => call.Kind == CommandCall.Expunge
            ? new MailTransportException(MailTransportFailure.Protocol, SecretMarker)
            : null;

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(0, 1, 0), report);
        Assert.Equal(new[] { "Select:Junk", "Expunge:Junk:1" }, harness.Server.CommandCallTexts.ToArray());
        MailCommand command = await harness.SingleCommandAsync();
        Assert.Equal(1, command.Attempts);
        Assert.DoesNotContain(SecretMarker, command.LastError, StringComparison.Ordinal);
        Assert.Equal(new uint[] { 1, 2 }, harness.Server.ServerUids("Junk"));
    }

    // Case 31
    private static async Task CorruptUidsAsync(CommandQueueFixture fixture, long commandId)
    {
        await using SqliteCommand update = fixture.Storage.Database.Connection.CreateCommand();
        update.CommandText = "UPDATE mail_commands SET uids = 'x' WHERE id = $id";
        update.Parameters.AddWithValue("$id", commandId);
        await update.ExecuteNonQueryAsync();
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_corrupt_row_is_skipped_by_ListDue_and_does_not_hide_the_other_commands()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        MailCommand good = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, fixture.Inbox.UidValidity, [1], MessageFlags.Seen, MessageFlags.None);
        MailCommand broken = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, fixture.Inbox.UidValidity, [2], MessageFlags.Seen, MessageFlags.None);
        await CorruptUidsAsync(fixture, broken.Id);

        IReadOnlyList<MailCommand> due = await fixture.Queue.ListDueAsync(fixture.Account.Id, fixture.Clock.Now);

        Assert.Equal(good.Id, Assert.Single(due).Id);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_corrupt_row_is_listed_as_failed_with_a_fixed_error_text_and_can_be_discarded()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        MailCommand good = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, fixture.Inbox.UidValidity, [1], MessageFlags.Seen, MessageFlags.None);
        MailCommand broken = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, fixture.Inbox.UidValidity, [2], MessageFlags.Seen, MessageFlags.None);
        await CorruptUidsAsync(fixture, broken.Id);

        IReadOnlyList<MailCommand> listed = await fixture.Queue.ListAsync(fixture.Account.Id);

        Assert.Equal(2, listed.Count);
        MailCommand shown = Assert.Single(listed, command => command.Id == broken.Id);
        Assert.Equal(MailCommandStatus.Failed, shown.Status);
        Assert.Equal(SqliteMailCommandQueue.UnreadableRowError, shown.LastError);
        Assert.Equal(MailCommandStatus.Pending, Assert.Single(listed, command => command.Id == good.Id).Status);
        Assert.Equal(good.Id, Assert.Single(await fixture.Queue.ListDueAsync(fixture.Account.Id, fixture.Clock.Now)).Id);
        await fixture.Queue.DiscardAsync(broken.Id);
        Assert.Equal(good.Id, Assert.Single(await fixture.Queue.ListAsync(fixture.Account.Id)).Id);
    }
}
