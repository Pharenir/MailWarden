using MailWarden.Core.Commands;

namespace MailWarden.Infrastructure.Tests.Commands;

/// <summary>AP-03c cases 1, 2, 3 and 5: enqueue, validation, empty folder, list/discard/due.</summary>
[Trait("Category", "feature:sync")]
[Trait("Category", "feature:storage")]
public sealed class CommandEnqueueTests
{
    private static async Task AssertRejectedAsync(CommandQueueFixture fixture, Func<Task> enqueue)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(enqueue);
        Assert.Equal(0, await fixture.CommandRowCountAsync());
    }

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task SetFlags_is_stored_pending_with_ascending_uids_and_changes_nothing_else()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        await fixture.Storage.AddMessageAsync(fixture.Inbox, 5);
        await fixture.Storage.AddMessageAsync(fixture.Inbox, 2);

        MailCommand command = await fixture.Queue.EnqueueSetFlagsAsync(
            fixture.Inbox.Id, [5, 2], MessageFlags.Seen, MessageFlags.None);

        Assert.True(command.Id > 0);
        Assert.Equal(fixture.Account.Id, command.AccountId);
        Assert.Equal(MailCommandKind.SetFlags, command.Kind);
        Assert.Equal(fixture.Inbox.Id, command.FolderId);
        Assert.Equal(fixture.Inbox.UidValidity, command.FolderUidValidity);
        Assert.Equal(new uint[] { 2, 5 }, command.Uids);
        Assert.Equal(MessageFlags.Seen, command.FlagsToAdd);
        Assert.Equal(MessageFlags.None, command.FlagsToRemove);
        Assert.Null(command.TargetFolderId);
        Assert.Equal(MailCommandStatus.Pending, command.Status);
        Assert.Equal(0, command.Attempts);
        Assert.Equal(SyncHarness.FixedNow, command.CreatedAt);
        Assert.Null(command.NextAttemptAt);
        Assert.Null(command.LastError);
        MailCommand listed = Assert.Single(await fixture.Queue.ListAsync(fixture.Account.Id));
        Assert.Equal(command.Id, listed.Id);
        Assert.Equal(new uint[] { 2, 5 }, listed.Uids);
        IReadOnlyDictionary<uint, MessageFlags> flags = await fixture.Storage.Messages.GetUidFlagsAsync(fixture.Inbox.Id);
        Assert.All(flags.Values, value => Assert.Equal(MessageFlags.None, value));
        Assert.Equal(2, flags.Count);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Duplicate_uids_are_removed_and_the_stored_list_is_ascending()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        MailCommand command = await fixture.Queue.EnqueueSetFlagsAsync(
            fixture.Inbox.Id, [9, 3, 9, 1, 3], MessageFlags.Flagged, MessageFlags.None);

        Assert.Equal(new uint[] { 1, 3, 9 }, command.Uids);
        Assert.Equal(new uint[] { 1, 3, 9 }, (await fixture.Queue.ListAsync(fixture.Account.Id)).Single().Uids);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Exactly_1000_uids_are_accepted()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        uint[] uids = Enumerable.Range(1, 1000).Select(value => (uint)value).ToArray();

        MailCommand command = await fixture.Queue.EnqueueMoveAsync(fixture.Inbox.Id, uids, fixture.Archive.Id);

        Assert.Equal(uids, command.Uids);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Each_allowed_flag_can_be_added_and_removed()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        MailCommand add = await fixture.Queue.EnqueueSetFlagsAsync(
            fixture.Inbox.Id, [1], MessageFlags.Seen | MessageFlags.Flagged | MessageFlags.Answered | MessageFlags.Draft,
            MessageFlags.None);
        MailCommand remove = await fixture.Queue.EnqueueSetFlagsAsync(
            fixture.Inbox.Id, [1], MessageFlags.None, MessageFlags.Seen | MessageFlags.Flagged);
        MailCommand both = await fixture.Queue.EnqueueSetFlagsAsync(
            fixture.Inbox.Id, [1], MessageFlags.Seen, MessageFlags.Flagged);

        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged | MessageFlags.Answered | MessageFlags.Draft, add.FlagsToAdd);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, remove.FlagsToRemove);
        Assert.Equal((MessageFlags.Seen, MessageFlags.Flagged), (both.FlagsToAdd, both.FlagsToRemove));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Move_stores_the_target_and_the_source_uid_validity()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        MailCommand command = await fixture.Queue.EnqueueMoveAsync(fixture.Inbox.Id, [4], fixture.Archive.Id);

        Assert.Equal(MailCommandKind.Move, command.Kind);
        Assert.Equal(fixture.Archive.Id, command.TargetFolderId);
        Assert.Equal(fixture.Inbox.UidValidity, command.FolderUidValidity);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task MoveToTrash_resolves_the_trash_folder_now()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        MailCommand command = await fixture.Queue.EnqueueMoveToTrashAsync(fixture.Inbox.Id, [4, 6]);

        Assert.Equal(MailCommandKind.MoveToTrash, command.Kind);
        Assert.Equal(fixture.Trash.Id, command.TargetFolderId);
        Assert.Equal(new uint[] { 4, 6 }, command.Uids);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task DeletePermanently_is_allowed_from_every_folder_including_trash()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        MailCommand fromInbox = await fixture.Queue.EnqueueDeletePermanentlyAsync(fixture.Inbox.Id, [1]);
        MailCommand fromTrash = await fixture.Queue.EnqueueDeletePermanentlyAsync(fixture.Trash.Id, [2]);

        Assert.Equal(MailCommandKind.DeletePermanently, fromInbox.Kind);
        Assert.Null(fromInbox.TargetFolderId);
        Assert.Equal(fixture.Trash.Id, fromTrash.FolderId);
    }

    // Case 2 – uid list
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_uid_list_is_rejected_for_every_kind()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [], MessageFlags.Seen, MessageFlags.None));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveAsync(fixture.Inbox.Id, [], fixture.Archive.Id));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveToTrashAsync(fixture.Inbox.Id, []));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueDeletePermanentlyAsync(fixture.Inbox.Id, []));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Uid_zero_is_rejected_for_every_kind()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [1, 0], MessageFlags.Seen, MessageFlags.None));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveAsync(fixture.Inbox.Id, [0], fixture.Archive.Id));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveToTrashAsync(fixture.Inbox.Id, [0]));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueDeletePermanentlyAsync(fixture.Inbox.Id, [0, 2]));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task More_than_1000_uids_are_rejected_for_every_kind()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        uint[] tooMany = Enumerable.Range(1, 1001).Select(value => (uint)value).ToArray();

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, tooMany, MessageFlags.Seen, MessageFlags.None));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveAsync(fixture.Inbox.Id, tooMany, fixture.Archive.Id));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveToTrashAsync(fixture.Inbox.Id, tooMany));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueDeletePermanentlyAsync(fixture.Inbox.Id, tooMany));
    }

    // Case 2 – folder
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unknown_source_folder_is_rejected_for_every_kind()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        const long unknownFolderId = 987_654;

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueSetFlagsAsync(unknownFolderId, [1], MessageFlags.Seen, MessageFlags.None));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveAsync(unknownFolderId, [1], fixture.Archive.Id));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveToTrashAsync(unknownFolderId, [1]));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueDeletePermanentlyAsync(unknownFolderId, [1]));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueEmptyFolderAsync(unknownFolderId));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Never_synced_folder_with_uid_validity_zero_is_rejected()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        MailFolder unsynced = await fixture.Storage.Folders.UpsertAsync(
            TestData.NewFolder(fixture.Account.Id, "Unsynced", FolderRole.Junk, uidValidity: 0));

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueSetFlagsAsync(unsynced.Id, [1], MessageFlags.Seen, MessageFlags.None));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveAsync(unsynced.Id, [1], fixture.Archive.Id));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveToTrashAsync(unsynced.Id, [1]));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueDeletePermanentlyAsync(unsynced.Id, [1]));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueEmptyFolderAsync(unsynced.Id));
    }

    // Case 2 – flags
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task SetFlags_with_overlapping_add_and_remove_is_rejected()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueSetFlagsAsync(
            fixture.Inbox.Id, [1], MessageFlags.Seen | MessageFlags.Flagged, MessageFlags.Flagged));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task SetFlags_with_neither_add_nor_remove_is_rejected()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueSetFlagsAsync(
            fixture.Inbox.Id, [1], MessageFlags.None, MessageFlags.None));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task SetFlags_with_the_deleted_flag_is_rejected_in_add_and_remove()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueSetFlagsAsync(
            fixture.Inbox.Id, [1], MessageFlags.Deleted, MessageFlags.None));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueSetFlagsAsync(
            fixture.Inbox.Id, [1], MessageFlags.Seen | MessageFlags.Deleted, MessageFlags.None));
        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueSetFlagsAsync(
            fixture.Inbox.Id, [1], MessageFlags.None, MessageFlags.Deleted));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task SetFlags_with_an_undefined_flag_bit_is_rejected()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueSetFlagsAsync(
            fixture.Inbox.Id, [1], (MessageFlags)64, MessageFlags.None));
    }

    // Case 2 – move target
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Move_to_the_same_folder_is_rejected()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveAsync(fixture.Inbox.Id, [1], fixture.Inbox.Id));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Move_to_an_unknown_target_is_rejected()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveAsync(fixture.Inbox.Id, [1], 987_654));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Move_to_a_folder_of_another_account_is_rejected()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        Account other = await fixture.Storage.AddAccountAsync("Account_B");
        MailFolder foreign = await fixture.Storage.AddFolderAsync(other.Id, FolderRole.Archive, "Archive");

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveAsync(fixture.Inbox.Id, [1], foreign.Id));
    }

    // Case 2 – trash
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task MoveToTrash_from_the_trash_folder_is_rejected()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueMoveToTrashAsync(fixture.Trash.Id, [1]));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task MoveToTrash_without_a_trash_folder_throws_InvalidOperation_and_stores_nothing()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync(withTrash: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Queue.EnqueueMoveToTrashAsync(fixture.Inbox.Id, [1]));

        Assert.Equal(0, await fixture.CommandRowCountAsync());
    }

    // Case 2 – empty folder
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Inbox)]
    [InlineData(FolderRole.Archive)]
    [InlineData(FolderRole.None)]
    public async Task EmptyFolder_on_a_folder_that_is_not_trash_or_junk_is_rejected(FolderRole role)
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        MailFolder folder = await fixture.Storage.AddFolderAsync(fixture.Account.Id, role, $"empty_{role}");
        await fixture.Storage.AddMessageAsync(folder, 1);

        await AssertRejectedAsync(fixture, () => fixture.Queue.EnqueueEmptyFolderAsync(folder.Id));
    }

    // Case 3
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Junk)]
    [InlineData(FolderRole.Trash)]
    public async Task EmptyFolder_takes_exactly_the_uids_stored_now_and_ignores_later_arrivals(FolderRole role)
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        MailFolder folder = role == FolderRole.Junk ? fixture.Junk : fixture.Trash;
        foreach (uint uid in new uint[] { 7, 3, 5 })
        {
            await fixture.Storage.AddMessageAsync(folder, uid);
        }

        MailCommand? command = await fixture.Queue.EnqueueEmptyFolderAsync(folder.Id);
        await fixture.Storage.AddMessageAsync(folder, 9);

        Assert.NotNull(command);
        Assert.Equal(MailCommandKind.DeletePermanently, command.Kind);
        Assert.Equal(folder.Id, command.FolderId);
        Assert.Equal(new uint[] { 3, 5, 7 }, command.Uids);
        MailCommand stored = Assert.Single(await fixture.Queue.ListAsync(fixture.Account.Id));
        Assert.Equal(new uint[] { 3, 5, 7 }, stored.Uids);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task EmptyFolder_returns_null_and_stores_nothing_for_a_folder_without_local_messages()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        MailCommand? command = await fixture.Queue.EnqueueEmptyFolderAsync(fixture.Junk.Id);

        Assert.Null(command);
        Assert.Equal(0, await fixture.CommandRowCountAsync());
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task List_returns_pending_and_failed_commands_of_the_account_oldest_first()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        Account other = await fixture.Storage.AddAccountAsync("Account_B");
        MailFolder otherInbox = await fixture.Storage.AddFolderAsync(other.Id, FolderRole.Inbox, "INBOX");
        MailCommand first = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [1], MessageFlags.Seen, MessageFlags.None);
        fixture.Clock.Now += TimeSpan.FromSeconds(1);
        MailCommand second = await fixture.Queue.EnqueueDeletePermanentlyAsync(fixture.Junk.Id, [2]);
        fixture.Clock.Now += TimeSpan.FromSeconds(1);
        MailCommand third = await fixture.Queue.EnqueueMoveAsync(fixture.Inbox.Id, [3], fixture.Archive.Id);
        await fixture.Queue.EnqueueSetFlagsAsync(otherInbox.Id, [1], MessageFlags.Seen, MessageFlags.None);
        await fixture.Queue.RecordFailureAsync(second.Id, "boom", null, permanent: true);

        IReadOnlyList<MailCommand> listed = await fixture.Queue.ListAsync(fixture.Account.Id);

        Assert.Equal(new[] { first.Id, second.Id, third.Id }, listed.Select(command => command.Id).ToArray());
        Assert.Equal(MailCommandStatus.Failed, listed[1].Status);
        Assert.Single(await fixture.Queue.ListAsync(other.Id));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Discard_removes_pending_and_failed_commands_and_unknown_id_is_a_no_op()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        MailCommand pending = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [1], MessageFlags.Seen, MessageFlags.None);
        MailCommand failed = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [2], MessageFlags.Seen, MessageFlags.None);
        MailCommand kept = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [3], MessageFlags.Seen, MessageFlags.None);
        await fixture.Queue.RecordFailureAsync(failed.Id, "boom", null, permanent: true);

        await fixture.Queue.DiscardAsync(pending.Id);
        await fixture.Queue.DiscardAsync(failed.Id);
        await fixture.Queue.DiscardAsync(987_654);

        Assert.Equal(kept.Id, Assert.Single(await fixture.Queue.ListAsync(fixture.Account.Id)).Id);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task ListDue_returns_only_pending_commands_that_are_due_oldest_first()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        DateTimeOffset now = fixture.Clock.Now;
        MailCommand noRetry = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [1], MessageFlags.Seen, MessageFlags.None);
        fixture.Clock.Now += TimeSpan.FromSeconds(1);
        MailCommand past = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [2], MessageFlags.Seen, MessageFlags.None);
        MailCommand future = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [3], MessageFlags.Seen, MessageFlags.None);
        MailCommand exactlyNow = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [4], MessageFlags.Seen, MessageFlags.None);
        MailCommand failedInPast = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [5], MessageFlags.Seen, MessageFlags.None);
        await fixture.Queue.RecordFailureAsync(past.Id, "boom", now - TimeSpan.FromMinutes(5), permanent: false);
        await fixture.Queue.RecordFailureAsync(future.Id, "boom", now + TimeSpan.FromMinutes(5), permanent: false);
        await fixture.Queue.RecordFailureAsync(exactlyNow.Id, "boom", now, permanent: false);
        await fixture.Queue.RecordFailureAsync(failedInPast.Id, "boom", now - TimeSpan.FromMinutes(5), permanent: true);

        IReadOnlyList<MailCommand> due = await fixture.Queue.ListDueAsync(fixture.Account.Id, now);

        Assert.Equal(new[] { noRetry.Id, past.Id, exactlyNow.Id }, due.Select(command => command.Id).ToArray());
        IReadOnlyList<MailCommand> later = await fixture.Queue.ListDueAsync(fixture.Account.Id, now + TimeSpan.FromMinutes(6));
        Assert.Equal(
            new[] { noRetry.Id, past.Id, future.Id, exactlyNow.Id },
            later.Select(command => command.Id).ToArray());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task ListDue_does_not_return_commands_of_another_account()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        Account other = await fixture.Storage.AddAccountAsync("Account_B");
        await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [1], MessageFlags.Seen, MessageFlags.None);

        Assert.Empty(await fixture.Queue.ListDueAsync(other.Id, fixture.Clock.Now));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task RecordFailure_counts_the_attempt_and_stores_error_and_next_attempt()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        MailCommand command = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [1], MessageFlags.Seen, MessageFlags.None);
        DateTimeOffset next = fixture.Clock.Now + TimeSpan.FromMinutes(2);

        await fixture.Queue.RecordFailureAsync(command.Id, "ProtocolFailure", next, permanent: false);
        await fixture.Queue.RecordFailureAsync(command.Id, "ProtocolFailure", next, permanent: false);

        MailCommand stored = Assert.Single(await fixture.Queue.ListAsync(fixture.Account.Id));
        Assert.Equal(2, stored.Attempts);
        Assert.Equal("ProtocolFailure", stored.LastError);
        Assert.Equal(next, stored.NextAttemptAt);
        Assert.Equal(MailCommandStatus.Pending, stored.Status);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task RecordFailure_permanent_sets_status_failed()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        MailCommand command = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [1], MessageFlags.Seen, MessageFlags.None);

        await fixture.Queue.RecordFailureAsync(command.Id, "UIDVALIDITY changed", null, permanent: true);

        MailCommand stored = Assert.Single(await fixture.Queue.ListAsync(fixture.Account.Id));
        Assert.Equal(MailCommandStatus.Failed, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal("UIDVALIDITY changed", stored.LastError);
        Assert.Empty(await fixture.Queue.ListDueAsync(fixture.Account.Id, fixture.Clock.Now + TimeSpan.FromDays(1)));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Complete_removes_only_that_command()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        MailCommand done = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [1], MessageFlags.Seen, MessageFlags.None);
        MailCommand kept = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, [2], MessageFlags.Seen, MessageFlags.None);

        await fixture.Queue.CompleteAsync(done.Id);

        Assert.Equal(kept.Id, Assert.Single(await fixture.Queue.ListAsync(fixture.Account.Id)).Id);
    }

    // Case 25
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task EmptyFolder_has_no_1000_uid_limit_while_the_other_enqueue_methods_keep_it()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        for (uint uid = 1; uid <= 1500; uid++)
        {
            await fixture.Storage.AddMessageAsync(fixture.Trash, uid);
        }

        MailCommand? command = await fixture.Queue.EnqueueEmptyFolderAsync(fixture.Trash.Id);

        Assert.NotNull(command);
        Assert.Equal(Enumerable.Range(1, 1500).Select(value => (uint)value), command.Uids);
        Assert.Equal(MailCommandKind.DeletePermanently, command.Kind);
        Assert.Equal(1L, await fixture.CommandRowCountAsync());
        uint[] tooMany = Enumerable.Range(1, 1001).Select(value => (uint)value).ToArray();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, tooMany, MessageFlags.Seen, MessageFlags.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueMoveAsync(fixture.Inbox.Id, tooMany, fixture.Archive.Id));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueMoveToTrashAsync(fixture.Inbox.Id, tooMany));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Queue.EnqueueDeletePermanentlyAsync(fixture.Inbox.Id, tooMany));
        Assert.Equal(1L, await fixture.CommandRowCountAsync());
    }
}
