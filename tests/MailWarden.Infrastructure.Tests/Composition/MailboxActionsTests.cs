using MailWarden.Core.Commands;
using MailWarden.Infrastructure.Composition;

namespace MailWarden.Infrastructure.Tests.Composition;

/// <summary>AP-04b D1 (cases 1-6): <see cref="IMailboxActions"/> against the real queue and SQLite.</summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
[Trait("Category", "feature:storage")]
public sealed class MailboxActionsTests
{
    private static readonly string[] ForbiddenInMessages = ["subject_", "sender_a", "example.test"];

    private static EmptyFolderPreview Confirmed(uint uidValidity, params uint[] uids) => new(uidValidity, new HashSet<uint>(uids));

    private static void AssertFixedText(MailActionException exception)
    {
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        foreach (string forbidden in ForbiddenInMessages)
        {
            Assert.DoesNotContain(forbidden, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Case 1 --------------------------------------------------------------------------------------------

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task SetSeen_enqueues_one_SetFlags_command_with_the_folder_uid_validity_and_triggers_the_account()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox, "INBOX", uidValidity: 4242);
        IReadOnlyList<MailMessage> messages = await harness.AddMessagesAsync(inbox, 3, firstUid: 10);

        await harness.Actions.SetSeenAsync(messages, seen: true);

        MailCommand command = Assert.Single(await harness.CommandsAsync(account));
        Assert.Equal(MailCommandKind.SetFlags, command.Kind);
        Assert.Equal(inbox.Id, command.FolderId);
        Assert.Equal(4242u, command.FolderUidValidity);
        Assert.Equal(new uint[] { 10, 11, 12 }, command.Uids);
        Assert.Equal(MessageFlags.Seen, command.FlagsToAdd);
        Assert.Equal(MessageFlags.None, command.FlagsToRemove);
        Assert.Equal(MailCommandStatus.Pending, command.Status);
        Assert.Equal(account.Id, Assert.Single(harness.Triggered));
    }

    // Case 1
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetSeen_false_removes_the_flag_and_true_adds_it(bool seen)
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        IReadOnlyList<MailMessage> messages = await harness.AddMessagesAsync(inbox, 1);

        await harness.Actions.SetSeenAsync(messages, seen);

        MailCommand command = Assert.Single(await harness.CommandsAsync(account));
        Assert.Equal(seen ? MessageFlags.Seen : MessageFlags.None, command.FlagsToAdd);
        Assert.Equal(seen ? MessageFlags.None : MessageFlags.Seen, command.FlagsToRemove);
    }

    // Case 1
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetFlagged_adds_or_removes_the_flagged_flag(bool flagged)
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        IReadOnlyList<MailMessage> messages = await harness.AddMessagesAsync(inbox, 2);

        await harness.Actions.SetFlaggedAsync(messages, flagged);

        MailCommand command = Assert.Single(await harness.CommandsAsync(account));
        Assert.Equal(MailCommandKind.SetFlags, command.Kind);
        Assert.Equal(flagged ? MessageFlags.Flagged : MessageFlags.None, command.FlagsToAdd);
        Assert.Equal(flagged ? MessageFlags.None : MessageFlags.Flagged, command.FlagsToRemove);
        Assert.Equal(account.Id, Assert.Single(harness.Triggered));
    }

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task MoveToTrash_enqueues_a_MoveToTrash_command_targeting_the_trash_folder()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailFolder trash = await harness.AddFolderAsync(account, FolderRole.Trash, "Trash");
        IReadOnlyList<MailMessage> messages = await harness.AddMessagesAsync(inbox, 2);

        await harness.Actions.MoveToTrashAsync(messages);

        MailCommand command = Assert.Single(await harness.CommandsAsync(account));
        Assert.Equal(MailCommandKind.MoveToTrash, command.Kind);
        Assert.Equal(inbox.Id, command.FolderId);
        Assert.Equal(trash.Id, command.TargetFolderId);
        Assert.Equal(account.Id, Assert.Single(harness.Triggered));
    }

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task DeletePermanently_enqueues_a_DeletePermanently_command_also_from_the_trash()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailFolder trash = await harness.AddFolderAsync(account, FolderRole.Trash, "Trash");
        IReadOnlyList<MailMessage> fromInbox = await harness.AddMessagesAsync(inbox, 1);
        IReadOnlyList<MailMessage> fromTrash = await harness.AddMessagesAsync(trash, 1);

        await harness.Actions.DeletePermanentlyAsync([.. fromInbox, .. fromTrash]);

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync(account);
        Assert.Equal(2, commands.Count);
        Assert.All(commands, command => Assert.Equal(MailCommandKind.DeletePermanently, command.Kind));
        Assert.Equal(new[] { inbox.Id, trash.Id }.Order().ToArray(), commands.Select(command => command.FolderId).Order().ToArray());
    }

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Move_enqueues_a_Move_command_with_the_target_folder()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailFolder archive = await harness.AddFolderAsync(account, FolderRole.Archive, "Archive");
        IReadOnlyList<MailMessage> messages = await harness.AddMessagesAsync(inbox, 2);

        await harness.Actions.MoveAsync(messages, archive.Id);

        MailCommand command = Assert.Single(await harness.CommandsAsync(account));
        Assert.Equal(MailCommandKind.Move, command.Kind);
        Assert.Equal(archive.Id, command.TargetFolderId);
        Assert.Equal(inbox.Id, command.FolderId);
        Assert.Equal(account.Id, Assert.Single(harness.Triggered));
    }

    // Case 1: several folders of one account give one command per folder, each with its own UIDVALIDITY.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Messages_of_several_folders_give_one_command_per_folder_with_its_own_uid_validity()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox, "INBOX", uidValidity: 111);
        MailFolder junk = await harness.AddFolderAsync(account, FolderRole.Junk, "Junk", uidValidity: 222);
        IReadOnlyList<MailMessage> fromInbox = await harness.AddMessagesAsync(inbox, 2, firstUid: 1);
        IReadOnlyList<MailMessage> fromJunk = await harness.AddMessagesAsync(junk, 3, firstUid: 1);

        await harness.Actions.SetFlaggedAsync([.. fromInbox, .. fromJunk], flagged: true);

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync(account);
        Assert.Equal(2, commands.Count);
        MailCommand inboxCommand = commands.Single(command => command.FolderId == inbox.Id);
        MailCommand junkCommand = commands.Single(command => command.FolderId == junk.Id);
        Assert.Equal(111u, inboxCommand.FolderUidValidity);
        Assert.Equal(222u, junkCommand.FolderUidValidity);
        Assert.Equal(new uint[] { 1, 2 }, inboxCommand.Uids);
        Assert.Equal(new uint[] { 1, 2, 3 }, junkCommand.Uids);
        Assert.NotEmpty(harness.Triggered);
        Assert.All(harness.Triggered, triggered => Assert.Equal(account.Id, triggered));
    }

    // Case 1: several accounts give commands per account and a trigger for each account.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Messages_of_several_accounts_trigger_each_account()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account first = await harness.AddAccountAsync("Account_A");
        Account second = await harness.AddAccountAsync("Account_B");
        MailFolder firstInbox = await harness.AddFolderAsync(first, FolderRole.Inbox);
        MailFolder secondInbox = await harness.AddFolderAsync(second, FolderRole.Inbox);
        IReadOnlyList<MailMessage> firstMessages = await harness.AddMessagesAsync(firstInbox, 2);
        IReadOnlyList<MailMessage> secondMessages = await harness.AddMessagesAsync(secondInbox, 2);

        await harness.Actions.SetSeenAsync([.. firstMessages, .. secondMessages], seen: true);

        Assert.Single(await harness.CommandsAsync(first));
        Assert.Single(await harness.CommandsAsync(second));
        Assert.Contains(first.Id, harness.Triggered);
        Assert.Contains(second.Id, harness.Triggered);
        Assert.Equal(2, harness.Triggered.Distinct().Count());
    }

    // Case 1 (edge): an empty list stores nothing and triggers nothing (a no-op or a MailActionException are both fine).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_message_list_enqueues_nothing_and_triggers_nothing()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        await harness.AddFolderAsync(account, FolderRole.Inbox);

        Exception? failure = await Record.ExceptionAsync(() => harness.Actions.SetSeenAsync([], seen: true));

        Assert.True(failure is null or MailActionException, "empty list may be a no-op or a MailActionException, nothing else");
        Assert.Equal(0, await harness.CommandCountAsync(account));
        Assert.Empty(harness.Triggered);
    }

    // ---- Case 2 --------------------------------------------------------------------------------------------

    // Case 2: the 1,000 UID limit of the queue is respected by splitting; all UIDs are covered exactly once.
    // Timeout tripled: seeding 2,500 rows one by one into the file database takes a few seconds.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 3)]
    public async Task Twenty_five_hundred_mails_are_split_into_commands_of_at_most_1000_uids_covering_all()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        IReadOnlyList<MailMessage> messages = await harness.AddMessagesAsync(inbox, 2500);

        await harness.Actions.SetSeenAsync(messages, seen: true);

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync(account);
        Assert.Equal(3, commands.Count);
        Assert.All(commands, command => Assert.True(command.Uids.Count <= 1000));
        Assert.All(commands, command => Assert.Equal(inbox.UidValidity, command.FolderUidValidity));
        uint[] covered = [.. commands.SelectMany(command => command.Uids).Order()];
        Assert.Equal(Enumerable.Range(1, 2500).Select(value => (uint)value).ToArray(), covered);
        Assert.Equal(account.Id, harness.Triggered.Distinct().Single());
    }

    // Case 2 boundary: exactly 1,000 gives one command, 1,001 gives two. Timeout doubled for the same reason.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    [InlineData(1000, 1)]
    [InlineData(1001, 2)]
    public async Task Boundary_counts_give_the_expected_number_of_commands(int count, int expectedCommands)
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        IReadOnlyList<MailMessage> messages = await harness.AddMessagesAsync(inbox, count);

        await harness.Actions.SetFlaggedAsync(messages, flagged: true);

        Assert.Equal(expectedCommands, (await harness.CommandsAsync(account)).Count);
    }

    // ---- Case 3 --------------------------------------------------------------------------------------------

    // Case 3
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task MoveToTrash_from_the_trash_throws_MailActionException_and_stores_nothing()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailFolder trash = await harness.AddFolderAsync(account, FolderRole.Trash, "Trash");
        IReadOnlyList<MailMessage> inTrash = await harness.AddMessagesAsync(trash, 2);

        MailActionException exception = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.MoveToTrashAsync(inTrash));

        AssertFixedText(exception);
        Assert.Equal(0, await harness.CommandCountAsync(account));
        Assert.Empty(harness.Triggered);
    }

    // Case 3
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Move_to_an_unknown_folder_throws_MailActionException_and_stores_nothing()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        IReadOnlyList<MailMessage> messages = await harness.AddMessagesAsync(inbox, 2);

        MailActionException exception = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.MoveAsync(messages, 987_654));

        AssertFixedText(exception);
        Assert.Equal(0, await harness.CommandCountAsync(account));
        Assert.Empty(harness.Triggered);
    }

    // Case 3
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Move_to_a_folder_of_another_account_throws_MailActionException_and_stores_nothing()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account first = await harness.AddAccountAsync("Account_A");
        Account second = await harness.AddAccountAsync("Account_B");
        MailFolder firstInbox = await harness.AddFolderAsync(first, FolderRole.Inbox);
        MailFolder secondArchive = await harness.AddFolderAsync(second, FolderRole.Archive, "Archive");
        IReadOnlyList<MailMessage> messages = await harness.AddMessagesAsync(firstInbox, 2);

        MailActionException exception = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.MoveAsync(messages, secondArchive.Id));

        AssertFixedText(exception);
        Assert.Equal(0, await harness.CommandCountAsync(first, second));
        Assert.Empty(harness.Triggered);
    }

    // Case 3: a message whose folder is not stored (stale row) is rejected as well.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Message_of_an_unknown_folder_throws_MailActionException()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailMessage stray = TestData.NewMessage(account.Id, 987_654, 1) with { Id = 1 };

        await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.SetSeenAsync([stray], seen: true));

        Assert.Equal(0, await harness.CommandCountAsync(account));
    }

    // Case 3: "nothing partial stays hidden" - an accepted group may stay stored, the refused group never does.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mixed_trash_and_inbox_selection_refuses_the_trash_group_and_stores_nothing_for_it()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailFolder trash = await harness.AddFolderAsync(account, FolderRole.Trash, "Trash");
        IReadOnlyList<MailMessage> fromInbox = await harness.AddMessagesAsync(inbox, 1);
        IReadOnlyList<MailMessage> fromTrash = await harness.AddMessagesAsync(trash, 1);

        await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.MoveToTrashAsync([.. fromInbox, .. fromTrash]));

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync(account);
        Assert.DoesNotContain(commands, command => command.FolderId == trash.Id);
        Assert.True(commands.Count <= 1);
    }

    // ---- Case 4 --------------------------------------------------------------------------------------------

    // Case 4
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Trash)]
    [InlineData(FolderRole.Junk)]
    public async Task Preview_equals_the_local_mails_and_EmptyFolder_enqueues_one_command(FolderRole role)
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailFolder folder = await harness.AddFolderAsync(account, role, role.ToString());
        await harness.AddMessagesAsync(folder, 4);

        EmptyFolderPreview preview = await harness.Actions.PreviewEmptyAsync(folder.Id);
        await harness.Actions.EmptyFolderAsync(folder.Id, preview);

        Assert.Equal(4, preview.Count);
        Assert.Equal(1000u, preview.UidValidity);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, preview.Uids.Order().ToArray());
        MailCommand command = Assert.Single(await harness.CommandsAsync(account));
        Assert.Equal(folder.Id, command.FolderId);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, command.Uids);
        Assert.Equal(account.Id, Assert.Single(harness.Triggered));
    }

    // Case 4: the count follows the local mails right now.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Preview_follows_the_current_local_state()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder junk = await harness.AddFolderAsync(account, FolderRole.Junk, "Junk");
        Assert.True(Confirmed(1000).HasSameContent(await harness.Actions.PreviewEmptyAsync(junk.Id)));
        await harness.AddMessagesAsync(junk, 3);
        Assert.True(Confirmed(1000, 1, 2, 3).HasSameContent(await harness.Actions.PreviewEmptyAsync(junk.Id)));

        await harness.Messages.DeleteByUidsAsync(junk.Id, [1u]);

        Assert.True(Confirmed(1000, 2, 3).HasSameContent(await harness.Actions.PreviewEmptyAsync(junk.Id)));
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task EmptyFolder_on_the_inbox_is_rejected_with_MailActionException()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        await harness.AddMessagesAsync(inbox, 3);

        MailActionException exception = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.EmptyFolderAsync(inbox.Id, Confirmed(1000, 1, 2, 3)));

        AssertFixedText(exception);
        Assert.Equal(0, await harness.CommandCountAsync(account));
        Assert.Empty(harness.Triggered);
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task EmptyFolder_on_an_unknown_folder_is_rejected_with_MailActionException()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        await harness.AddAccountAsync();

        await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.EmptyFolderAsync(987_654, Confirmed(1000)));
    }

    // Case 4: nothing to delete is no command and no trigger.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task EmptyFolder_without_local_mails_stores_nothing_and_triggers_nothing()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder junk = await harness.AddFolderAsync(account, FolderRole.Junk, "Junk");

        await harness.Actions.EmptyFolderAsync(junk.Id, Confirmed(1000));

        Assert.Equal(0, await harness.CommandCountAsync(account));
        Assert.Empty(harness.Triggered);
    }

    // Case 4 + 2: a large Junk folder is covered completely by commands of at most 1,000 UIDs.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 3)]
    public async Task EmptyFolder_with_2500_mails_covers_all_uids()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder junk = await harness.AddFolderAsync(account, FolderRole.Junk, "Junk");
        await harness.AddMessagesAsync(junk, 2500);

        EmptyFolderPreview preview = await harness.Actions.PreviewEmptyAsync(junk.Id);
        Assert.Equal(2500, preview.Count);
        await harness.Actions.EmptyFolderAsync(junk.Id, preview);

        // AP-03c: EnqueueEmptyFolder has no 1,000 UID limit, so one command may carry all UIDs.
        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync(account);
        Assert.Equal(2500, commands.SelectMany(command => command.Uids).Distinct().Count());
    }

    // ---- Case 5 --------------------------------------------------------------------------------------------

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task ListPending_returns_pending_and_failed_commands_with_resolved_message_ids()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        await harness.AddFolderAsync(account, FolderRole.Trash, "Trash");
        IReadOnlyList<MailMessage> pendingMessages = await harness.AddMessagesAsync(inbox, 2, firstUid: 1);
        IReadOnlyList<MailMessage> failedMessages = await harness.AddMessagesAsync(inbox, 1, firstUid: 10);
        await harness.Actions.SetSeenAsync(pendingMessages, seen: true);
        await harness.Actions.MoveToTrashAsync(failedMessages);
        MailCommand failedCommand = (await harness.CommandsAsync(account)).Single(command => command.Kind == MailCommandKind.MoveToTrash);
        await harness.Queue.RecordFailureAsync(failedCommand.Id, "marker_server_text", null, permanent: true);

        IReadOnlyList<PendingAction> pending = await harness.Actions.ListPendingAsync();

        Assert.Equal(2, pending.Count);
        PendingAction seenAction = pending.Single(action => action.Kind == MailCommandKind.SetFlags);
        PendingAction failedAction = pending.Single(action => action.Kind == MailCommandKind.MoveToTrash);
        Assert.Equal(MailCommandStatus.Pending, seenAction.Status);
        Assert.Equal(MailCommandStatus.Failed, failedAction.Status);
        Assert.Equal(account.Id, seenAction.AccountId);
        Assert.Equal(pendingMessages.Select(message => message.Id).Order().ToArray(), seenAction.MessageIds.Order().ToArray());
        Assert.Equal(failedMessages.Select(message => message.Id).ToArray(), failedAction.MessageIds.ToArray());
        Assert.Equal(failedCommand.Id, failedAction.CommandId);
        Assert.NotNull(failedAction.LastError);
        Assert.Null(seenAction.LastError);
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task ListPending_skips_a_uid_that_is_no_longer_stored()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        IReadOnlyList<MailMessage> messages = await harness.AddMessagesAsync(inbox, 3);
        await harness.Actions.SetSeenAsync(messages, seen: true);
        await harness.Messages.DeleteByUidsAsync(inbox.Id, [2u]);

        PendingAction action = Assert.Single(await harness.Actions.ListPendingAsync());

        Assert.Equal(new[] { messages[0].Id, messages[2].Id }, action.MessageIds.Order().ToArray());
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task ListPending_is_empty_without_commands_and_covers_all_accounts()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account first = await harness.AddAccountAsync("Account_A");
        Account second = await harness.AddAccountAsync("Account_B");
        Assert.Empty(await harness.Actions.ListPendingAsync());
        MailFolder firstInbox = await harness.AddFolderAsync(first, FolderRole.Inbox);
        MailFolder secondInbox = await harness.AddFolderAsync(second, FolderRole.Inbox);
        await harness.Actions.SetFlaggedAsync(await harness.AddMessagesAsync(firstInbox, 1), flagged: true);
        await harness.Actions.SetFlaggedAsync(await harness.AddMessagesAsync(secondInbox, 1), flagged: true);

        IReadOnlyList<PendingAction> pending = await harness.Actions.ListPendingAsync();

        Assert.Equal(new[] { first.Id, second.Id }.Order().ToArray(), pending.Select(action => action.AccountId).Order().ToArray());
    }

    // ---- Case 6 --------------------------------------------------------------------------------------------

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Discard_removes_the_command_and_an_unknown_id_is_a_no_op()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        await harness.Actions.SetSeenAsync(await harness.AddMessagesAsync(inbox, 1, firstUid: 1), seen: true);
        long discardedId = Assert.Single(await harness.CommandsAsync(account)).Id;
        await harness.Actions.SetFlaggedAsync(await harness.AddMessagesAsync(inbox, 1, firstUid: 5), flagged: true);

        await harness.Actions.DiscardAsync(discardedId);
        Exception? unknown = await Record.ExceptionAsync(() => harness.Actions.DiscardAsync(987_654));

        Assert.Null(unknown);
        MailCommand kept = Assert.Single(await harness.CommandsAsync(account));
        Assert.NotEqual(discardedId, kept.Id);
        Assert.DoesNotContain(await harness.Actions.ListPendingAsync(), action => action.CommandId == discardedId);
    }

    // Case 6: a failed command can be discarded like a pending one.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Discard_removes_a_failed_command()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        await harness.Actions.SetSeenAsync(await harness.AddMessagesAsync(inbox, 1), seen: true);
        MailCommand command = Assert.Single(await harness.CommandsAsync(account));
        await harness.Queue.RecordFailureAsync(command.Id, "marker_server_text", null, permanent: true);

        await harness.Actions.DiscardAsync(command.Id);

        Assert.Equal(0, await harness.CommandCountAsync(account));
        Assert.Empty(await harness.Actions.ListPendingAsync());
    }

    // ---- Serialisation -------------------------------------------------------------------------------------

    // D1 "serialised": parallel calls on the one instance lose no command and do not fail.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Parallel_calls_on_one_instance_are_serialised_and_lose_nothing()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        IReadOnlyList<MailMessage> messages = await harness.AddMessagesAsync(inbox, 20);

        await Task.WhenAll(messages.Select(message => harness.Actions.SetFlaggedAsync([message], flagged: true)));

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync(account);
        Assert.Equal(20, commands.Count);
        Assert.Equal(20, commands.SelectMany(command => command.Uids).Distinct().Count());
    }

    // ---- Cases 25-27 and 4 (fix.2: U2, U3, U4) --------------------------------------------------------------

    private static async Task<(ActionsHarness Harness, Account Account, MailFolder Inbox, MailFolder Junk, MailFolder Archive)> JunkWithMailsAsync(int junkCount = 3)
    {
        ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailFolder junk = await harness.AddFolderAsync(account, FolderRole.Junk, "Junk");
        MailFolder archive = await harness.AddFolderAsync(account, FolderRole.Archive, "Archive");
        await harness.AddFolderAsync(account, FolderRole.Trash, "Trash");
        await harness.AddMessagesAsync(junk, junkCount);
        return (harness, account, inbox, junk, archive);
    }

    // Case 25: a pending Move out of the folder blocks preview and empty; nothing new is stored.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_pending_move_from_the_folder_blocks_preview_and_empty()
    {
        (ActionsHarness harness, Account account, _, MailFolder junk, MailFolder archive) = await JunkWithMailsAsync();
        await using ActionsHarness owned = harness;
        IReadOnlyList<MailMessage> stored = await harness.Messages.ListAsync(new MessageQuery(FolderId: junk.Id));
        await harness.Actions.MoveAsync([stored[0]], archive.Id);
        long commandsBefore = await harness.CommandCountAsync(account);

        MailActionException previewFailure = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.PreviewEmptyAsync(junk.Id));
        MailActionException emptyFailure = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.EmptyFolderAsync(junk.Id, Confirmed(1000, 1, 2, 3)));

        AssertFixedText(previewFailure);
        AssertFixedText(emptyFailure);
        Assert.Equal(commandsBefore, await harness.CommandCountAsync(account));
    }

    // Case 25: the same for MoveToTrash.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_pending_move_to_trash_from_the_folder_blocks_empty()
    {
        (ActionsHarness harness, Account account, _, MailFolder junk, _) = await JunkWithMailsAsync();
        await using ActionsHarness owned = harness;
        IReadOnlyList<MailMessage> stored = await harness.Messages.ListAsync(new MessageQuery(FolderId: junk.Id));
        await harness.Actions.MoveToTrashAsync([stored[0]]);

        await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.EmptyFolderAsync(junk.Id, Confirmed(1000, 1, 2, 3)));

        Assert.Equal(1, await harness.CommandCountAsync(account));
    }

    // Case 25: also while the failed attempt waits in backoff (status stays Pending with a later next attempt).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_pending_move_in_backoff_still_blocks_empty()
    {
        (ActionsHarness harness, Account account, _, MailFolder junk, MailFolder archive) = await JunkWithMailsAsync();
        await using ActionsHarness owned = harness;
        IReadOnlyList<MailMessage> stored = await harness.Messages.ListAsync(new MessageQuery(FolderId: junk.Id));
        await harness.Actions.MoveAsync([stored[0]], archive.Id);
        MailCommand move = Assert.Single(await harness.CommandsAsync(account));
        await harness.Queue.RecordFailureAsync(move.Id, "marker_server_text", harness.Clock.Now.AddMinutes(5), permanent: false);
        Assert.Equal(MailCommandStatus.Pending, Assert.Single(await harness.CommandsAsync(account)).Status);

        await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.PreviewEmptyAsync(junk.Id));
        await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.EmptyFolderAsync(junk.Id, Confirmed(1000, 1, 2, 3)));

        Assert.Single(await harness.CommandsAsync(account));
    }

    // Case 25 positive controls: a pending Move in another folder and a pending SetFlags in this folder do not block.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Other_pending_commands_do_not_block_empty()
    {
        (ActionsHarness harness, Account account, MailFolder inbox, MailFolder junk, MailFolder archive) = await JunkWithMailsAsync();
        await using ActionsHarness owned = harness;
        IReadOnlyList<MailMessage> inboxMails = await harness.AddMessagesAsync(inbox, 1);
        IReadOnlyList<MailMessage> junkMails = await harness.Messages.ListAsync(new MessageQuery(FolderId: junk.Id));
        await harness.Actions.MoveAsync(inboxMails, archive.Id);
        await harness.Actions.SetFlaggedAsync([junkMails[0]], flagged: true);

        EmptyFolderPreview preview = await harness.Actions.PreviewEmptyAsync(junk.Id);
        await harness.Actions.EmptyFolderAsync(junk.Id, preview);

        Assert.Equal(3, (await harness.CommandsAsync(account)).Count);
    }

    // Case 26: a mail that arrived after the preview refuses the empty; a fresh preview then goes ahead.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_mail_arriving_after_the_preview_refuses_the_empty_until_it_is_previewed_again()
    {
        (ActionsHarness harness, Account account, _, MailFolder junk, _) = await JunkWithMailsAsync();
        await using ActionsHarness owned = harness;
        EmptyFolderPreview preview = await harness.Actions.PreviewEmptyAsync(junk.Id);
        await harness.AddMessagesAsync(junk, 1, firstUid: 4);

        MailActionException refusal = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.EmptyFolderAsync(junk.Id, preview));

        AssertFixedText(refusal);
        Assert.Equal(0, await harness.CommandCountAsync(account));
        Assert.Empty(harness.Triggered);
        EmptyFolderPreview fresh = await harness.Actions.PreviewEmptyAsync(junk.Id);
        Assert.True(Confirmed(1000, 1, 2, 3, 4).HasSameContent(fresh));
        await harness.Actions.EmptyFolderAsync(junk.Id, fresh);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, Assert.Single(await harness.CommandsAsync(account)).Uids);
    }

    // Case 26: a mail that disappeared after the preview does not stop the empty.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(1u)]
    [InlineData(3u)]
    public async Task A_mail_disappearing_after_the_preview_does_not_stop_the_empty(uint disappearingUid)
    {
        (ActionsHarness harness, Account account, _, MailFolder junk, _) = await JunkWithMailsAsync();
        await using ActionsHarness owned = harness;
        EmptyFolderPreview preview = await harness.Actions.PreviewEmptyAsync(junk.Id);
        await harness.Messages.DeleteByUidsAsync(junk.Id, [disappearingUid]);

        await harness.Actions.EmptyFolderAsync(junk.Id, preview);

        uint[] expected = new uint[] { 1, 2, 3 }.Where(uid => uid != disappearingUid).ToArray();
        Assert.Equal(expected, Assert.Single(await harness.CommandsAsync(account)).Uids);
    }

    // Case 27: a rejection after one group was stored says "partly done"; a rejection with nothing stored does not.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_rejection_after_a_stored_group_says_partly_done_and_one_without_storage_does_not()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailFolder trash = await harness.AddFolderAsync(account, FolderRole.Trash, "Trash");
        IReadOnlyList<MailMessage> fromInbox = await harness.AddMessagesAsync(inbox, 1);
        IReadOnlyList<MailMessage> fromTrash = await harness.AddMessagesAsync(trash, 1);
        MailActionException none = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.MoveToTrashAsync(fromTrash));
        Assert.NotEqual(MailboxActions.PartlyDoneText, none.Message);
        Assert.Equal(0, await harness.CommandCountAsync(account));

        MailActionException mixed = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.MoveToTrashAsync([.. fromInbox, .. fromTrash]));

        long stored = await harness.CommandCountAsync(account);
        Assert.Equal(stored > 0, mixed.Message == MailboxActions.PartlyDoneText);
        AssertFixedText(mixed);
    }

    // ---- fix.2 additions: lower-UID arrival, StorageException after a stored group --------------------------

    // A mail with a LOWER uid that was loaded after the preview raises the count above the confirmed count: refused, queue stays empty.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_late_loaded_mail_with_a_lower_uid_refuses_the_empty_and_leaves_the_queue_empty()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailFolder junk = await harness.AddFolderAsync(account, FolderRole.Junk, "Junk");
        await harness.AddMessagesAsync(junk, 3, firstUid: 2);
        EmptyFolderPreview preview = await harness.Actions.PreviewEmptyAsync(junk.Id);
        Assert.True(Confirmed(1000, 2, 3, 4).HasSameContent(preview));
        await harness.AddMessagesAsync(junk, 1, firstUid: 1);

        MailActionException refusal = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.EmptyFolderAsync(junk.Id, preview));

        AssertFixedText(refusal);
        Assert.Equal(0, await harness.CommandCountAsync(account));
        Assert.Empty(harness.Triggered);
    }

    // Positive control: fewer mails than confirmed (the highest UID unchanged) is allowed.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Fewer_mails_than_confirmed_with_the_same_highest_uid_are_emptied()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder junk = await harness.AddFolderAsync(account, FolderRole.Junk, "Junk");
        await harness.AddMessagesAsync(junk, 3, firstUid: 2);
        EmptyFolderPreview preview = await harness.Actions.PreviewEmptyAsync(junk.Id);
        await harness.Messages.DeleteByUidsAsync(junk.Id, [2u]);

        await harness.Actions.EmptyFolderAsync(junk.Id, preview);

        Assert.Equal(new uint[] { 3, 4 }, Assert.Single(await harness.CommandsAsync(account)).Uids);
    }

    private static async Task InstallFailOnSecondCommandAsync(ActionsHarness harness)
    {
        await using Microsoft.Data.Sqlite.SqliteCommand command = harness.SeedDatabase.Connection.CreateCommand();
        command.CommandText =
            "CREATE TRIGGER fail_on_second_command BEFORE INSERT ON mail_commands " +
            "WHEN (SELECT COUNT(*) FROM mail_commands) >= 1 BEGIN SELECT RAISE(ABORT, 'marker_storage_failure'); END;";
        await command.ExecuteNonQueryAsync();
    }

    // A storage failure after one stored group reports the partly-done text (a trigger makes the second insert fail).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_storage_failure_after_a_stored_group_reports_the_partly_done_text()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailFolder junk = await harness.AddFolderAsync(account, FolderRole.Junk, "Junk");
        IReadOnlyList<MailMessage> mails = [.. await harness.AddMessagesAsync(inbox, 1), .. await harness.AddMessagesAsync(junk, 1)];
        await InstallFailOnSecondCommandAsync(harness);

        MailActionException failure = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.SetFlaggedAsync(mails, flagged: true));

        Assert.Equal(MailboxActions.PartlyDoneText, failure.Message);
        Assert.DoesNotContain("marker_storage_failure", failure.Message, StringComparison.Ordinal);
        Assert.Contains("StorageException", harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("marker_storage_failure", harness.Log.AllText, StringComparison.Ordinal);
        Assert.Equal(1, await harness.CommandCountAsync(account));
    }

    // Positive control: with a single group the same trigger does not fire (first insert) and nothing is reported.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_same_trigger_does_not_affect_a_single_group()
    {
        await using ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        MailFolder inbox = await harness.AddFolderAsync(account, FolderRole.Inbox);
        IReadOnlyList<MailMessage> mails = await harness.AddMessagesAsync(inbox, 2);
        await InstallFailOnSecondCommandAsync(harness);

        await harness.Actions.SetFlaggedAsync(mails, flagged: true);

        Assert.Equal(1, await harness.CommandCountAsync(account));
    }

    // ---- fix.4 W-a .. W-e and atomicity (real queue, real SQLite) -------------------------------------------

    private static async Task<(ActionsHarness Harness, Account Account, MailFolder Junk)> JunkWithUidsAsync(params uint[] uids)
    {
        ActionsHarness harness = await ActionsHarness.CreateAsync();
        Account account = await harness.AddAccountAsync();
        await harness.AddFolderAsync(account, FolderRole.Inbox);
        MailFolder junk = await harness.AddFolderAsync(account, FolderRole.Junk, "Junk", uidValidity: 1000);
        foreach (uint uid in uids)
        {
            await harness.AddMessagesAsync(junk, 1, firstUid: uid);
        }

        return (harness, account, junk);
    }

    // W-a: confirmed {10, 20}, local {15, 20}: same count, same highest UID, still refused; nothing stored.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task W_a_same_count_and_highest_uid_but_another_uid_is_refused_and_nothing_is_stored()
    {
        (ActionsHarness harness, Account account, MailFolder junk) = await JunkWithUidsAsync(10, 20);
        await using ActionsHarness owned = harness;
        EmptyFolderPreview confirmed = Confirmed(1000, 10, 20);
        await harness.Messages.DeleteByUidsAsync(junk.Id, [10u]);
        await harness.AddMessagesAsync(junk, 1, firstUid: 15);

        MailActionException refusal = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.EmptyFolderAsync(junk.Id, confirmed));

        AssertFixedText(refusal);
        Assert.NotEqual(MailboxActions.PartlyDoneText, refusal.Message);
        Assert.Equal(0, await harness.CommandCountAsync(account));
        Assert.Empty(await harness.Queue.ListAsync(account.Id));
        Assert.Empty(harness.Triggered);
    }

    // W-b: UIDVALIDITY changed, local UIDs a subset of the confirmed ones: refused; nothing stored.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task W_b_a_changed_uid_validity_is_refused_and_nothing_is_stored()
    {
        (ActionsHarness harness, Account account, MailFolder junk) = await JunkWithUidsAsync(1, 2, 3);
        await using ActionsHarness owned = harness;
        EmptyFolderPreview confirmed = Confirmed(1000, 1, 2, 3);
        await harness.Folders.UpsertAsync(junk with { UidValidity = 2000 });
        Assert.Equal(2000u, (await harness.Folders.GetAsync(junk.Id))!.UidValidity);
        Assert.Equal(3, (await harness.Messages.ListAsync(new MessageQuery(FolderId: junk.Id))).Count);

        MailActionException refusal = await Assert.ThrowsAsync<MailActionException>(() => harness.Actions.EmptyFolderAsync(junk.Id, confirmed));

        AssertFixedText(refusal);
        Assert.NotEqual(MailboxActions.PartlyDoneText, refusal.Message);
        Assert.Empty(await harness.Queue.ListAsync(account.Id));
        Assert.Empty(harness.Triggered);
    }

    // W-c: local UIDs are a proper subset of the confirmed ones: the command holds exactly the local UIDs.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task W_c_a_proper_subset_stores_exactly_the_local_uids()
    {
        (ActionsHarness harness, Account account, MailFolder junk) = await JunkWithUidsAsync(1, 2, 3);
        await using ActionsHarness owned = harness;

        await harness.Actions.EmptyFolderAsync(junk.Id, Confirmed(1000, 1, 2, 3, 99));

        MailCommand command = Assert.Single(await harness.Queue.ListAsync(account.Id));
        Assert.Equal(new uint[] { 1, 2, 3 }, command.Uids);
        Assert.Equal(junk.Id, command.FolderId);
        Assert.Equal(1000u, command.FolderUidValidity);
    }

    // W-d: nothing local any more: no command and no failure.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task W_d_nothing_local_any_more_stores_nothing_and_does_not_fail()
    {
        (ActionsHarness harness, Account account, MailFolder junk) = await JunkWithUidsAsync(1, 2);
        await using ActionsHarness owned = harness;
        EmptyFolderPreview confirmed = Confirmed(1000, 1, 2);
        await harness.Messages.DeleteByUidsAsync(junk.Id, [1u, 2u]);

        Exception? failure = await Record.ExceptionAsync(() => harness.Actions.EmptyFolderAsync(junk.Id, confirmed));

        Assert.Null(failure);
        Assert.Empty(await harness.Queue.ListAsync(account.Id));
        Assert.Empty(harness.Triggered);
    }

    private static async Task ExecuteAsync(ActionsHarness harness, string sql)
    {
        await using Microsoft.Data.Sqlite.SqliteCommand command = harness.SeedDatabase.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // W-e: the insert fails (trigger): nothing stored, the error reaches the caller; the same call works once the trigger is gone.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task W_e_a_failing_insert_stores_nothing_reaches_the_caller_and_the_retry_works()
    {
        (ActionsHarness harness, Account account, MailFolder junk) = await JunkWithUidsAsync(1, 2, 3);
        await using ActionsHarness owned = harness;
        EmptyFolderPreview confirmed = Confirmed(1000, 1, 2, 3);
        await ExecuteAsync(harness, "CREATE TRIGGER fail_every_command BEFORE INSERT ON mail_commands BEGIN SELECT RAISE(ABORT, 'marker_storage_failure'); END;");

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => harness.Actions.EmptyFolderAsync(junk.Id, confirmed));

        Assert.NotNull(failure);
        Assert.Empty(await harness.Queue.ListAsync(account.Id));
        await ExecuteAsync(harness, "DROP TRIGGER fail_every_command;");

        await harness.Actions.EmptyFolderAsync(junk.Id, confirmed);

        Assert.Equal(new uint[] { 1, 2, 3 }, Assert.Single(await harness.Queue.ListAsync(account.Id)).Uids);
    }

    // Atomicity: another connection holds the write lock and changes a UID; the empty waits for it and then refuses.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Check_and_insert_are_one_transaction_a_concurrent_uid_change_is_seen()
    {
        (ActionsHarness harness, Account account, MailFolder junk) = await JunkWithUidsAsync(1, 2, 3);
        await using ActionsHarness owned = harness;
        EmptyFolderPreview confirmed = Confirmed(1000, 1, 2, 3);
        await ExecuteAsync(harness, "BEGIN IMMEDIATE;");

        Task<MailActionException> call = Assert.ThrowsAsync<MailActionException>(() => harness.Actions.EmptyFolderAsync(junk.Id, confirmed));
        await Task.Delay(500);
        Assert.False(call.IsCompleted, "the call must wait for the write lock instead of reading around it");
        await ExecuteAsync(harness, $"UPDATE messages SET uid = 7 WHERE folder_id = {junk.Id} AND uid = 3;");
        await ExecuteAsync(harness, "COMMIT;");

        MailActionException refusal = await call;
        AssertFixedText(refusal);
        Assert.NotEqual(MailboxActions.PartlyDoneText, refusal.Message);
        Assert.Empty(await harness.Queue.ListAsync(account.Id));
    }
}
