using MailWarden.Infrastructure.Background;
using static MailWarden.Presentation.Tests.TestSupport.ActionsTestHelpers;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04b D2 (cases 7, 9-12, 15, 16, 20): actions of <see cref="ShellViewModel"/> against the fakes.</summary>
[Trait("Category", "feature:ui")]
public sealed class ShellActionsTests
{
    private const string TrashWordPattern = "Papierkorb|Gelöscht|Trash";

    private static bool MentionsAny(string text, params string[] words) =>
        words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    // ---- Case 7 --------------------------------------------------------------------------------------------

    // Case 7: a pending delete hides the row at once, the next row is selected and shown, a reload keeps it hidden.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Delete_hides_the_row_at_once_selects_the_next_row_and_keeps_it_hidden_across_reloads()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(3);
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        await SelectAsync(harness, rows[1]);
        Assert.True(harness.Shell.DeleteCommand.CanExecute(null));

        harness.Shell.DeleteCommand.Execute(null);

        await WaitForCallsAsync(harness, "MoveToTrash");
        ActionCall call = Assert.Single(harness.Actions.CallsNamed("MoveToTrash"));
        Assert.Equal(new[] { rows[1].Message.Id }, call.MessageIds.ToArray());
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 2, "row hidden");
        Assert.DoesNotContain(harness.Shell.Messages.Items, row => row.Message.Id == rows[1].Message.Id);
        await Wait.UntilAsync(
            () => harness.Shell.Messages.SelectedRows.Count == 1 && harness.Shell.Messages.SelectedRows[0].Message.Id == rows[2].Message.Id,
            "next row selected");
        await Wait.UntilAsync(
            () => harness.Shell.Reading.State == ReadingState.Ready && harness.Shell.Reading.Subject == rows[2].Message.Subject,
            "reading pane shows the next row");

        await harness.Shell.Messages.ReloadAsync();
        await SyncCycleAsync(harness, account);

        Assert.Equal(2, harness.Shell.Messages.Items.Count);
        Assert.DoesNotContain(harness.Shell.Messages.Items, row => row.Message.Id == rows[1].Message.Id);
    }

    // Case 7: deleting the last row selects the previous one.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Deleting_the_last_row_selects_the_previous_row()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(3);
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        await SelectAsync(harness, rows[2]);

        harness.Shell.DeleteCommand.Execute(null);

        await Wait.UntilAsync(
            () => harness.Shell.Messages.SelectedRows.Count == 1 && harness.Shell.Messages.SelectedRows[0].Message.Id == rows[1].Message.Id,
            "previous row selected");
    }

    // Case 7: deleting the only row leaves an empty list and an empty reading pane.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Deleting_the_only_row_empties_list_and_reading_pane()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(1);
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);

        harness.Shell.DeleteCommand.Execute(null);

        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 0, "row hidden");
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Empty, "reading pane empty");
        Assert.Empty(harness.Shell.Messages.SelectedRows);
    }

    // Case 7: the server confirmed the command, the message is really gone and stays gone.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Confirmed_delete_leaves_the_row_gone_after_the_reload()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(3);
        MessageRowViewModel victim = harness.Shell.Messages.Items[0];
        await SelectAsync(harness, victim);
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash");

        harness.Actions.Complete(Assert.Single(harness.Actions.CommandIds));
        await SyncCycleAsync(harness, account);

        Assert.Equal(2, harness.Shell.Messages.Items.Count);
        Assert.DoesNotContain(harness.Shell.Messages.Items, row => row.Message.Id == victim.Message.Id);
    }

    // Case 7: "also in other views for the same message".
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_pending_delete_hides_the_message_in_other_views_too()
    {
        (ShellHarness harness, Account account, MailFolder inbox, _) = await InitializedAsync(3);
        MessageRowViewModel victim = harness.Shell.Messages.Items[0];
        await SelectAsync(harness, victim);
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash");
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 2, "row hidden");

        await harness.Shell.Messages.LoadAsync(MailView.Folder(account.Id, inbox.Id));

        Assert.Equal(2, harness.Shell.Messages.Items.Count);
        Assert.DoesNotContain(harness.Shell.Messages.Items, row => row.Message.Id == victim.Message.Id);

        await harness.Shell.Messages.LoadAsync(MailView.GroupForAccount(FolderRole.Inbox, account.Id));

        Assert.DoesNotContain(harness.Shell.Messages.Items, row => row.Message.Id == victim.Message.Id);
    }

    // Case 7 positive control: without the pending command the row would be listed (the hiding above is not vacuous).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Without_a_delete_all_rows_stay_listed_after_a_reload()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(3);

        await SyncCycleAsync(harness, account);

        Assert.Equal(3, harness.Shell.Messages.Items.Count);
    }

    // Case 7: a rejection by the queue is shown without a private text, the row stays.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_rejected_delete_keeps_the_row_and_shows_a_fixed_banner_without_private_text()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(2);
        harness.Actions.Failure = new MailWarden.Infrastructure.Composition.MailActionException("fixed rejection text");
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);

        harness.Shell.DeleteCommand.Execute(null);

        await Wait.UntilAsync(() => !string.IsNullOrWhiteSpace(harness.Shell.Banner), "banner shown");
        Assert.Equal(2, harness.Shell.Messages.Items.Count);
        AssertNoPrivateText(harness.Shell.Banner);
        Assert.Empty(harness.Actions.Calls);
    }

    // ---- Case 9 --------------------------------------------------------------------------------------------

    // Case 9: no
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Permanent_delete_answered_no_enqueues_nothing_and_the_row_stays()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(3);
        harness.ConfirmationAnswer = false;
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);

        harness.Shell.DeletePermanentlyCommand.Execute(null);

        await WaitForConfirmationAsync(harness);
        await Wait.NegativeWindowAsync();
        Assert.Empty(harness.Actions.Calls);
        Assert.Equal(3, harness.Shell.Messages.Items.Count);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 9: no answer at all counts as no.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Permanent_delete_without_an_answer_enqueues_nothing()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(3);
        harness.ConfirmationAnswer = null;
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);

        harness.Shell.DeletePermanentlyCommand.Execute(null);

        await WaitForConfirmationAsync(harness);
        await Wait.NegativeWindowAsync();
        Assert.Empty(harness.Actions.Calls);
        Assert.Equal(3, harness.Shell.Messages.Items.Count);
    }

    // Case 9: yes, with the count in the text and no private text.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Permanent_delete_answered_yes_enqueues_the_command_and_the_text_names_the_count_only()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(4);
        harness.ConfirmationAnswer = true;
        MessageRowViewModel[] chosen = [.. harness.Shell.Messages.Items.Take(3)];
        harness.Shell.Messages.SelectedRows = chosen;
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Empty, "reading pane empty");

        harness.Shell.DeletePermanentlyCommand.Execute(null);

        await WaitForCallsAsync(harness, "DeletePermanently");
        ActionCall call = Assert.Single(harness.Actions.CallsNamed("DeletePermanently"));
        Assert.Equal(chosen.Select(row => row.Message.Id).Order().ToArray(), call.MessageIds.Order().ToArray());
        string text = Assert.Single(harness.ConfirmationTexts);
        Assert.Contains("3", text, StringComparison.Ordinal);
        AssertNoPrivateText(text);
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 1, "rows hidden");
    }

    // Case 9: nothing selected, nothing to ask.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Permanent_delete_without_a_selection_is_disabled()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(2);

        Assert.False(harness.Shell.DeletePermanentlyCommand.CanExecute(null));
        harness.Shell.DeletePermanentlyCommand.Execute(null);
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.ConfirmationTexts);
        Assert.Empty(harness.Actions.Calls);
    }

    // ---- Case 10 -------------------------------------------------------------------------------------------

    // Case 10: Del on a mail in the Trash asks and then deletes permanently.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_in_the_trash_asks_and_then_deletes_permanently()
    {
        (ShellHarness harness, Account account, _, MailFolder trash) = await InitializedAsync(1);
        harness.ReadModel.AddMessage(trash, "marker_subject_in_trash", fromName: "marker_sender_x");
        await harness.Shell.Messages.LoadAsync(MailView.Folder(account.Id, trash.Id));
        await SelectAsync(harness, Assert.Single(harness.Shell.Messages.Items));

        harness.Shell.DeleteCommand.Execute(null);

        await WaitForConfirmationAsync(harness);
        await WaitForCallsAsync(harness, "DeletePermanently");
        Assert.Empty(harness.Actions.CallsNamed("MoveToTrash"));
        AssertNoPrivateText(Assert.Single(harness.ConfirmationTexts));
    }

    // Case 10: no answer keeps the mail in the Trash.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_in_the_trash_answered_no_changes_nothing()
    {
        (ShellHarness harness, Account account, _, MailFolder trash) = await InitializedAsync(1);
        harness.ReadModel.AddMessage(trash, "marker_subject_in_trash");
        harness.ConfirmationAnswer = false;
        await harness.Shell.Messages.LoadAsync(MailView.Folder(account.Id, trash.Id));
        await SelectAsync(harness, Assert.Single(harness.Shell.Messages.Items));

        harness.Shell.DeleteCommand.Execute(null);

        await WaitForConfirmationAsync(harness);
        await Wait.NegativeWindowAsync();
        Assert.Empty(harness.Actions.Calls);
        Assert.Single(harness.Shell.Messages.Items);
    }

    // Case 10 control: Del outside the Trash does not ask.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_outside_the_trash_moves_to_the_trash_without_asking()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(2);
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);

        harness.Shell.DeleteCommand.Execute(null);

        await WaitForCallsAsync(harness, "MoveToTrash");
        Assert.Empty(harness.ConfirmationTexts);
        Assert.Empty(harness.Actions.CallsNamed("DeletePermanently"));
    }

    // ---- Case 11 -------------------------------------------------------------------------------------------

    private static IEnumerable<NavigationItem> Flatten(IEnumerable<NavigationItem> items) =>
        items.SelectMany(item => new[] { item }.Concat(Flatten(item.Children)));

    /// <summary>Seeds Inbox, Trash and Junk before initialisation and selects the folder node of <paramref name="role"/>.</summary>
    private static async Task<(ShellHarness Harness, Account Account, MailFolder Folder)> InitializedInFolderAsync(FolderRole role, int count)
    {
        var harness = new ShellHarness();
        Account account = harness.ReadModel.AddAccount("Account_A");
        harness.ReadModel.AddMessage(harness.ReadModel.AddFolder(account, FolderRole.Inbox, "INBOX"), "marker_subject_inbox");
        MailFolder trash = harness.ReadModel.AddFolder(account, FolderRole.Trash, "Trash");
        MailFolder junk = harness.ReadModel.AddFolder(account, FolderRole.Junk, "Junk");
        MailFolder folder = role == FolderRole.Trash ? trash : junk;
        for (int index = 0; index < count; index++)
        {
            harness.ReadModel.AddMessage(folder, $"marker_subject_{role}_{index}", fromName: "marker_sender_x", fromAddress: "sender_a@example.test");
        }

        harness.SetStatuses(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        await harness.Shell.InitializeAsync();
        NavigationItem node = Flatten(harness.Shell.Navigation.Items).First(item =>
            item.View == MailView.Folder(account.Id, folder.Id) || item.View == MailView.GroupForAccount(role, account.Id));
        harness.Shell.Navigation.SelectedItem = node;
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == count, "folder listed");
        await Wait.NegativeWindowAsync();
        return (harness, account, folder);
    }

    // Case 11: the count comes from CountForEmptyAsync, the words of the role are named, no sender or subject.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_folder_asks_with_the_snapshot_count_and_the_role_word_and_then_enqueues()
    {
        (ShellHarness harness, _, MailFolder junk) = await InitializedInFolderAsync(FolderRole.Junk, 4);
        harness.Actions.OverrideEmptyCount(junk.Id, 7);
        Assert.True(harness.Shell.EmptyFolderCommand.CanExecute(null));

        harness.Shell.EmptyFolderCommand.Execute(null);

        await WaitForConfirmationAsync(harness);
        await WaitForCallsAsync(harness, "EmptyFolder");
        string text = Assert.Single(harness.ConfirmationTexts);
        Assert.Contains("7", text, StringComparison.Ordinal);
        Assert.True(MentionsAny(text, "Junk", "Spam"), $"role word missing in: {text}");
        AssertNoPrivateText(text);
        Assert.Equal(junk.Id, Assert.Single(harness.Actions.CallsNamed("EmptyFolder")).FolderId);
    }

    // Case 11: no answer, no command.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Empty_folder_without_a_yes_enqueues_nothing(bool? answer)
    {
        (ShellHarness harness, _, _) = await InitializedInFolderAsync(FolderRole.Junk, 4);
        harness.ConfirmationAnswer = answer;

        harness.Shell.EmptyFolderCommand.Execute(null);

        await WaitForConfirmationAsync(harness);
        await Wait.NegativeWindowAsync();
        Assert.Empty(harness.Actions.Calls);
        Assert.Equal(4, harness.Shell.Messages.Items.Count);
    }

    // Case 11: Trash is allowed and names its own word.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_folder_on_the_trash_asks_with_the_trash_word()
    {
        (ShellHarness harness, _, _) = await InitializedInFolderAsync(FolderRole.Trash, 2);
        Assert.True(harness.Shell.EmptyFolderCommand.CanExecute(null));

        harness.Shell.EmptyFolderCommand.Execute(null);

        await WaitForConfirmationAsync(harness);
        string text = Assert.Single(harness.ConfirmationTexts);
        Assert.Contains("2", text, StringComparison.Ordinal);
        Assert.Matches(TrashWordPattern, text);
        AssertNoPrivateText(text);
    }

    // Case 11: Inbox and other folders cannot be emptied.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_folder_is_disabled_in_the_inbox()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(2);

        Assert.False(harness.Shell.EmptyFolderCommand.CanExecute(null));
        harness.Shell.EmptyFolderCommand.Execute(null);
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.ConfirmationTexts);
        Assert.Empty(harness.Actions.Calls);
    }

    // ---- Case 12 -------------------------------------------------------------------------------------------

    // Case 12
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mark_read_on_three_rows_shows_the_state_at_once_sends_one_command_and_follows_the_server_after_reload()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(3);
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        Assert.All(rows, row => Assert.True(row.IsUnread));
        harness.Shell.Messages.SelectedRows = rows;

        harness.Shell.MarkReadCommand.Execute(null);

        await WaitForCallsAsync(harness, "SetSeen");
        ActionCall call = Assert.Single(harness.Actions.CallsNamed("SetSeen"));
        Assert.True(call.Value);
        Assert.Equal(rows.Select(row => row.Message.Id).Order().ToArray(), call.MessageIds.Order().ToArray());
        await Wait.UntilAsync(() => rows.All(row => !row.IsUnread), "rows show read at once");

        foreach (long commandId in harness.Actions.CommandIds)
        {
            harness.Actions.Complete(commandId);
        }

        await SyncCycleAsync(harness, account);

        Assert.All(harness.Shell.Messages.Items, row => Assert.False(row.IsUnread));
        Assert.Equal(3, harness.Shell.Messages.Items.Count);
    }

    // Case 12
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mark_unread_flips_read_rows_and_the_server_state_wins_after_a_failed_command()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(3, MessageFlags.Seen);
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        Assert.All(rows, row => Assert.False(row.IsUnread));
        harness.Shell.Messages.SelectedRows = rows;

        harness.Shell.MarkUnreadCommand.Execute(null);

        await WaitForCallsAsync(harness, "SetSeen");
        Assert.False(Assert.Single(harness.Actions.CallsNamed("SetSeen")).Value);
        await Wait.UntilAsync(() => rows.All(row => row.IsUnread), "rows show unread at once");

        // The command fails for good: the server never changed, the reload shows the old truth again.
        foreach (long commandId in harness.Actions.CommandIds)
        {
            harness.Actions.Fail(commandId);
        }

        await SyncCycleAsync(harness, account);

        Assert.All(harness.Shell.Messages.Items, row => Assert.False(row.IsUnread));
    }

    // Case 12
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Toggle_flag_flags_unflagged_rows_and_unflags_flagged_rows()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(2);
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        harness.Shell.Messages.SelectedRows = rows;

        harness.Shell.ToggleFlagCommand.Execute(null);

        await WaitForCallsAsync(harness, "SetFlagged");
        Assert.True(harness.Actions.CallsNamed("SetFlagged")[0].Value);
        await Wait.UntilAsync(() => rows.All(row => row.IsFlagged), "rows show flagged at once");
        foreach (long commandId in harness.Actions.CommandIds)
        {
            harness.Actions.Complete(commandId);
        }

        await SyncCycleAsync(harness, account);
        MessageRowViewModel[] reloaded = [.. harness.Shell.Messages.Items];
        Assert.All(reloaded, row => Assert.True(row.IsFlagged));
        harness.Shell.Messages.SelectedRows = reloaded;

        harness.Shell.ToggleFlagCommand.Execute(null);

        await WaitForCallsAsync(harness, "SetFlagged", 2);
        Assert.False(harness.Actions.CallsNamed("SetFlagged")[1].Value);
        await Wait.UntilAsync(() => reloaded.All(row => !row.IsFlagged), "rows show unflagged at once");
    }

    // ---- Case 15 -------------------------------------------------------------------------------------------

    // Case 15: a target of the same account is accepted and the rows leave the list.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Move_to_a_folder_of_the_same_account_enqueues_one_command_and_hides_the_rows()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(3);
        MailFolder archive = harness.ReadModel.AddFolder(account, FolderRole.Archive, "Archive");
        MessageRowViewModel[] chosen = [.. harness.Shell.Messages.Items.Take(2)];
        harness.Shell.Messages.SelectedRows = chosen;
        Assert.True(harness.Shell.MoveCommand.CanExecute(archive.Id));

        harness.Shell.MoveCommand.Execute(archive.Id);

        await WaitForCallsAsync(harness, "Move");
        ActionCall call = Assert.Single(harness.Actions.CallsNamed("Move"));
        Assert.Equal(archive.Id, call.FolderId);
        Assert.Equal(chosen.Select(row => row.Message.Id).Order().ToArray(), call.MessageIds.Order().ToArray());
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 1, "moved rows hidden");
    }

    // Case 15: a target of another account is refused before the queue.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Move_to_a_folder_of_another_account_is_refused_before_the_queue()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(2);
        Account other = harness.ReadModel.AddAccount("Account_B");
        MailFolder foreignArchive = harness.ReadModel.AddFolder(other, FolderRole.Archive, "Archive");
        harness.Shell.Messages.SelectedRows = [harness.Shell.Messages.Items[0]];

        Exception? failure = Record.Exception(() => harness.Shell.MoveCommand.Execute(foreignArchive.Id));
        await Wait.NegativeWindowAsync();

        Assert.Null(failure);
        Assert.Empty(harness.Actions.Calls);
        Assert.Equal(2, harness.Shell.Messages.Items.Count);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 15: a multi-selection over several accounts disables move.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Move_is_disabled_for_a_selection_over_several_accounts()
    {
        var harness = new ShellHarness();
        (Account accountA, MailFolder inboxA) = harness.SeedInbox("Account_A", 1);
        (Account accountB, MailFolder inboxB) = harness.SeedInbox("Account_B", 1);
        MailFolder archiveA = harness.ReadModel.AddFolder(accountA, FolderRole.Archive, "Archive");
        harness.SetStatuses(
            FakeMailboxController.Status(accountA.Id, AccountSyncState.Idle),
            FakeMailboxController.Status(accountB.Id, AccountSyncState.Idle));
        await harness.InitializeAndWaitForListAsync(2);
        Assert.NotEqual(inboxA.AccountId, inboxB.AccountId);

        harness.Shell.Messages.SelectedRows = [.. harness.Shell.Messages.Items];

        Assert.False(harness.Shell.MoveCommand.CanExecute(archiveA.Id));
        harness.Shell.MoveCommand.Execute(archiveA.Id);
        await Wait.NegativeWindowAsync();
        Assert.Empty(harness.Actions.Calls);

        // Control: the same command is enabled for a single account's selection.
        harness.Shell.Messages.SelectedRows = [harness.Shell.Messages.Items.Single(row => row.Message.AccountId == accountA.Id)];
        Assert.True(harness.Shell.MoveCommand.CanExecute(archiveA.Id));
    }

    // ---- Case 16 -------------------------------------------------------------------------------------------

    // Case 16: 0, 1 and 2 selected rows.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Selected_rows_drive_the_reading_pane_and_the_enabled_state_of_the_commands()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(3);
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        System.Windows.Input.ICommand[] rowCommands =
        [
            harness.Shell.MarkReadCommand,
            harness.Shell.MarkUnreadCommand,
            harness.Shell.ToggleFlagCommand,
            harness.Shell.DeleteCommand,
            harness.Shell.DeletePermanentlyCommand,
        ];

        harness.Shell.Messages.SelectedRows = [];
        Assert.Equal(ReadingState.Empty, harness.Shell.Reading.State);
        Assert.All(rowCommands, command => Assert.False(command.CanExecute(null)));

        harness.Shell.Messages.SelectedRows = [rows[0]];
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Ready, "single row shown");
        Assert.Equal(rows[0].Message.Subject, harness.Shell.Reading.Subject);
        Assert.All(rowCommands, command => Assert.True(command.CanExecute(null)));

        harness.Shell.Messages.SelectedRows = [rows[0], rows[1]];
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Empty, "two rows leave the pane empty");
        Assert.All(rowCommands, command => Assert.True(command.CanExecute(null)));

        harness.Shell.Messages.SelectedRows = [];
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Empty, "empty again");
        Assert.All(rowCommands, command => Assert.False(command.CanExecute(null)));
    }

    // Case 16: with no selection an executed command does nothing.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Commands_without_a_selection_enqueue_nothing()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(2);

        harness.Shell.MarkReadCommand.Execute(null);
        harness.Shell.MarkUnreadCommand.Execute(null);
        harness.Shell.ToggleFlagCommand.Execute(null);
        harness.Shell.DeleteCommand.Execute(null);
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Actions.Calls);
        Assert.Empty(harness.ConfirmationTexts);
    }

    // ---- Case 20 -------------------------------------------------------------------------------------------

    // Case 20: an action that is still running when the shell is disposed leaves no trace afterwards.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task An_action_in_flight_during_dispose_causes_no_further_read_no_banner_and_no_log_line()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(3);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int gateEntered = 0;
        harness.Actions.Gate = async () =>
        {
            Interlocked.Increment(ref gateEntered);
            await release.Task;
        };
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);
        harness.Shell.DeleteCommand.Execute(null);
        await Wait.UntilAsync(() => Volatile.Read(ref gateEntered) == 1, "action started and is blocked");

        harness.Shell.Dispose();
        int listCalls = harness.ReadModel.ListMessagesCalls;
        int pendingCalls = harness.Actions.ListPendingCalls;
        int readCalls = harness.ReadModel.ReadCalls;
        release.SetResult();
        await Wait.NegativeWindowAsync();
        await Wait.NegativeWindowAsync();

        Assert.Equal(listCalls, harness.ReadModel.ListMessagesCalls);
        Assert.Equal(pendingCalls, harness.Actions.ListPendingCalls);
        Assert.Equal(readCalls, harness.ReadModel.ReadCalls);
        Assert.Empty(harness.Log.Entries);
        Assert.Null(harness.Shell.Banner);
    }

    // Case 20: commands executed after Dispose do nothing and do not throw.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Commands_after_dispose_do_nothing()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(3);
        harness.Shell.Messages.SelectedRows = [harness.Shell.Messages.Items[0]];
        harness.Shell.Dispose();

        Exception? failure = Record.Exception(() =>
        {
            harness.Shell.MarkReadCommand.Execute(null);
            harness.Shell.DeleteCommand.Execute(null);
            harness.Shell.DeletePermanentlyCommand.Execute(null);
            harness.Shell.ToggleFlagCommand.Execute(null);
        });
        await Wait.NegativeWindowAsync();

        Assert.Null(failure);
        Assert.Empty(harness.Actions.Calls);
        Assert.Empty(harness.ConfirmationTexts);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 20: a pending confirmation that is answered after Dispose enqueues nothing.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_confirmation_answered_after_dispose_enqueues_nothing()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(3);
        harness.ConfirmationAnswer = null;
        TaskCompletionSource<bool>? open = null;
        harness.Shell.ConfirmationRequested += (_, request) => open = request.Answer;
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);
        harness.Shell.DeletePermanentlyCommand.Execute(null);
        await Wait.UntilAsync(() => open is not null, "confirmation open");

        harness.Shell.Dispose();
        open!.TrySetResult(true);
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Actions.Calls);
        Assert.Empty(harness.Log.Entries);
    }
}
