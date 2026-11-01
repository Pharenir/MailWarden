using System.Windows.Input;
using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Composition;
using static MailWarden.Presentation.Tests.TestSupport.ActionsTestHelpers;

namespace MailWarden.Presentation.Tests;

/// <summary>
/// AP-04b fix.2 (cases 23, 24, 26-28, 30, 31): selection copy at execution (U1), two-step empty (U3), texts (U4),
/// empty-folder state after a search (U6) and the check before enqueuing (U7).
/// </summary>
[Trait("Category", "feature:ui")]
public sealed class ActionRaceTests
{
    private static IEnumerable<NavigationItem> Flatten(IEnumerable<NavigationItem> items) =>
        items.SelectMany(item => new[] { item }.Concat(Flatten(item.Children)));

    /// <summary>Inbox, Trash and Junk seeded before initialisation; the node of <paramref name="role"/> is selected.</summary>
    private static async Task<(ShellHarness Harness, Account Account, MailFolder Folder)> InitializedInFolderAsync(
        FolderRole role, int count, string accountName = "Account_A")
    {
        var harness = new ShellHarness();
        Account account = harness.ReadModel.AddAccount(accountName);
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

    private static ICommand CommandOf(ShellViewModel shell, string kind) => kind switch
    {
        "MarkRead" => shell.MarkReadCommand,
        "MarkUnread" => shell.MarkUnreadCommand,
        "ToggleFlag" => shell.ToggleFlagCommand,
        "Delete" => shell.DeleteCommand,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown command kind"),
    };

    // ---- Case 23 -------------------------------------------------------------------------------------------

    // Case 23: the action is blocked, the selection moves on, the action then runs on the row of the keystroke.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("MarkRead", "SetSeen")]
    [InlineData("MarkUnread", "SetSeen")]
    [InlineData("ToggleFlag", "SetFlagged")]
    [InlineData("Delete", "MoveToTrash")]
    public async Task A_blocked_action_acts_on_the_row_selected_at_the_keystroke(string kind, string callName)
    {
        MessageFlags flags = kind == "MarkUnread" ? MessageFlags.Seen : MessageFlags.None;
        (ShellHarness harness, _, _, _) = await InitializedAsync(3, flags);
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        MessageRowViewModel atKeystroke = rows[0];
        MessageRowViewModel selectedLater = rows[1];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        harness.Actions.Gate = async () =>
        {
            Interlocked.Increment(ref entered);
            await release.Task;
        };
        await SelectAsync(harness, atKeystroke);

        CommandOf(harness.Shell, kind).Execute(null);
        await Wait.UntilAsync(() => Volatile.Read(ref entered) == 1, "action reached the gate");
        Assert.Empty(harness.Actions.Calls); // positive control: the gate really holds the action back
        harness.Shell.Messages.SelectedRows = [selectedLater];
        release.SetResult();

        await WaitForCallsAsync(harness, callName);
        await Wait.NegativeWindowAsync();
        ActionCall call = Assert.Single(harness.Actions.Calls);
        Assert.Equal(new[] { atKeystroke.Message.Id }, call.MessageIds.ToArray());
    }

    // Case 23: move
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_blocked_move_acts_on_the_row_selected_at_the_keystroke()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(3);
        MailFolder archive = harness.ReadModel.AddFolder(account, FolderRole.Archive, "Archive");
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        harness.Actions.Gate = async () =>
        {
            Interlocked.Increment(ref entered);
            await release.Task;
        };
        await SelectAsync(harness, rows[0]);

        harness.Shell.MoveCommand.Execute(archive.Id);
        await Wait.UntilAsync(() => Volatile.Read(ref entered) == 1, "move reached the gate");
        Assert.Empty(harness.Actions.Calls);
        harness.Shell.Messages.SelectedRows = [rows[1]];
        release.SetResult();

        await WaitForCallsAsync(harness, "Move");
        ActionCall call = Assert.Single(harness.Actions.Calls);
        Assert.Equal(new[] { rows[0].Message.Id }, call.MessageIds.ToArray());
    }

    // Case 23: permanent delete with a pending confirmation likewise.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_permanent_delete_with_a_pending_confirmation_acts_on_the_row_selected_at_the_keystroke()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(3);
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        harness.ConfirmationAnswer = null;
        TaskCompletionSource<bool>? open = null;
        harness.Shell.ConfirmationRequested += (_, request) => open = request.Answer;
        await SelectAsync(harness, rows[0]);

        harness.Shell.DeletePermanentlyCommand.Execute(null);
        await Wait.UntilAsync(() => open is not null, "confirmation open");
        Assert.Empty(harness.Actions.Calls); // positive control: nothing before the answer
        harness.Shell.Messages.SelectedRows = [rows[1]];
        open!.SetResult(true);

        await WaitForCallsAsync(harness, "DeletePermanently");
        await Wait.NegativeWindowAsync();
        ActionCall call = Assert.Single(harness.Actions.Calls);
        Assert.Equal(new[] { rows[0].Message.Id }, call.MessageIds.ToArray());
    }

    // ---- Case 24 -------------------------------------------------------------------------------------------

    // Case 24: Del twice fast on one row gives one command, the next row stays untouched.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_twice_in_a_row_acts_once_and_leaves_the_next_row_alone()
    {
        const bool blockedOnGate = true; // without a block two commands in a row are correct (the first has finished)
        (ShellHarness harness, _, _, _) = await InitializedAsync(3);
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        if (blockedOnGate)
        {
            harness.Actions.Gate = async () =>
            {
                Interlocked.Increment(ref entered);
                await release.Task;
            };
        }

        await SelectAsync(harness, rows[0]);

        harness.Shell.DeleteCommand.Execute(null);
        if (blockedOnGate)
        {
            await Wait.UntilAsync(() => Volatile.Read(ref entered) == 1, "first delete reached the gate");
        }

        harness.Shell.DeleteCommand.Execute(null);
        release.TrySetResult();

        await WaitForCallsAsync(harness, "MoveToTrash");
        await Wait.NegativeWindowAsync();
        ActionCall call = Assert.Single(harness.Actions.Calls);
        Assert.Equal(new[] { rows[0].Message.Id }, call.MessageIds.ToArray());
        Assert.Contains(harness.Shell.Messages.Items, row => row.Message.Id == rows[1].Message.Id);
        Assert.Contains(harness.Shell.Messages.Items, row => row.Message.Id == rows[2].Message.Id);

        // Positive control: a later, deliberate Del works on the row that is selected now.
        await Wait.UntilAsync(
            () => harness.Shell.Messages.SelectedRows.Count == 1 && harness.Shell.Messages.SelectedRows[0].Message.Id == rows[1].Message.Id,
            "next row selected");
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash", 2);
        Assert.Equal(new[] { rows[1].Message.Id }, harness.Actions.CallsNamed("MoveToTrash")[1].MessageIds.ToArray());
    }

    // ---- Cases 26 and 4 (UI side of the two-step empty) ----------------------------------------------------

    // Case 26: a mail arrives after the preview, the empty is refused, the UI asks again with the new count.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_mail_arriving_after_the_preview_makes_the_ui_ask_again_with_the_new_count()
    {
        (ShellHarness harness, _, MailFolder junk) = await InitializedInFolderAsync(FolderRole.Junk, 4);
        harness.BeforeAnswer = number =>
        {
            if (number == 1)
            {
                harness.ReadModel.AddMessage(junk, "marker_subject_arrived", fromName: "marker_sender_x");
            }
        };

        harness.Shell.EmptyFolderCommand.Execute(null);

        await WaitForConfirmationAsync(harness, 2);
        await WaitForCallsAsync(harness, "EmptyFolder");
        string[] texts = [.. harness.ConfirmationTexts];
        Assert.Equal(2, texts.Length);
        Assert.Contains("4", texts[0], StringComparison.Ordinal);
        Assert.Contains("5", texts[1], StringComparison.Ordinal);
        Assert.DoesNotContain("5", texts[0], StringComparison.Ordinal);
        Assert.All(texts, AssertNoPrivateText);
        Assert.Equal(5, harness.Actions.ConfirmedPreviews[^1].Count);
        Assert.Equal(2, harness.Actions.ConfirmedPreviews.Count);
    }

    // Case 26: the second question answered with no stores nothing.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Answering_no_to_the_second_question_empties_nothing()
    {
        (ShellHarness harness, _, MailFolder junk) = await InitializedInFolderAsync(FolderRole.Junk, 4);
        harness.BeforeAnswer = number =>
        {
            if (number == 1)
            {
                harness.ReadModel.AddMessage(junk, "marker_subject_arrived");
            }
            else
            {
                harness.ConfirmationAnswer = false;
            }
        };

        harness.Shell.EmptyFolderCommand.Execute(null);

        await WaitForConfirmationAsync(harness, 2);
        await Wait.NegativeWindowAsync();
        Assert.Empty(harness.Actions.CallsNamed("EmptyFolder"));
        Assert.Equal(5, (await harness.ReadModel.ListMessagesAsync(new MessageQuery(FolderId: junk.Id, Limit: 100))).Count);
    }

    // Case 26: a mail that disappears after the preview does not stop the empty and asks only once.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_mail_disappearing_after_the_preview_goes_ahead_after_one_question()
    {
        (ShellHarness harness, _, MailFolder junk) = await InitializedInFolderAsync(FolderRole.Junk, 4);
        harness.BeforeAnswer = _ =>
        {
            MailMessage victim = harness.ReadModel.ListMessagesAsync(new MessageQuery(FolderId: junk.Id, Limit: 100)).GetAwaiter().GetResult()[0];
            harness.ReadModel.RemoveMessage(victim.Id);
        };

        harness.Shell.EmptyFolderCommand.Execute(null);

        await WaitForCallsAsync(harness, "EmptyFolder");
        await Wait.NegativeWindowAsync();
        Assert.Single(harness.ConfirmationTexts);
        Assert.Equal(3, Assert.Single(harness.Actions.CallsNamed("EmptyFolder")).MessageIds.Count);
    }

    // ---- Case 27 (UI side) ---------------------------------------------------------------------------------

    // Case 27: a rejection that was partly executed shows the "partly done" wording in the banner; a plain one does not.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_partly_executed_rejection_is_called_partly_done_in_the_banner()
    {
        (ShellHarness plain, _, _, _) = await InitializedAsync(2);
        plain.Actions.Failure = new MailActionException("Aktion abgelehnt.");
        await SelectAsync(plain, plain.Shell.Messages.Items[0]);
        plain.Shell.DeleteCommand.Execute(null);
        await Wait.UntilAsync(() => !string.IsNullOrWhiteSpace(plain.Shell.Banner), "banner shown");
        Assert.DoesNotContain("teilweise", plain.Shell.Banner, StringComparison.OrdinalIgnoreCase);

        (ShellHarness partial, _, _, _) = await InitializedAsync(2);
        partial.Actions.Failure = new MailActionException(MailboxActions.PartlyDoneText);
        await SelectAsync(partial, partial.Shell.Messages.Items[0]);
        partial.Shell.DeleteCommand.Execute(null);

        await Wait.UntilAsync(() => !string.IsNullOrWhiteSpace(partial.Shell.Banner), "banner shown");
        Assert.Contains("teilweise", partial.Shell.Banner, StringComparison.OrdinalIgnoreCase);
        AssertNoPrivateText(partial.Shell.Banner);
    }

    // ---- Case 28 -------------------------------------------------------------------------------------------

    // Case 28: a selection over two accounts names the number of accounts; a single account does not.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_confirmation_names_the_number_of_accounts_when_more_than_one_is_involved()
    {
        var harness = new ShellHarness();
        (Account accountA, _) = harness.SeedInbox("Account_A", 4);
        (Account accountB, _) = harness.SeedInbox("Account_B", 1);
        harness.SetStatuses(
            FakeMailboxController.Status(accountA.Id, AccountSyncState.Idle),
            FakeMailboxController.Status(accountB.Id, AccountSyncState.Idle));
        await harness.InitializeAndWaitForListAsync(5);
        harness.Shell.Messages.SelectedRows = [.. harness.Shell.Messages.Items];

        harness.Shell.DeletePermanentlyCommand.Execute(null);

        await WaitForConfirmationAsync(harness);
        string text = Assert.Single(harness.ConfirmationTexts);
        Assert.Contains("5", text, StringComparison.Ordinal);
        Assert.Contains("2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("subject_", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sender A", text, StringComparison.OrdinalIgnoreCase);
        AssertNoPrivateText(text);
    }

    // Case 28 positive control: five mails of one account do not mention a second account.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_confirmation_for_one_account_does_not_mention_a_number_of_accounts()
    {
        var harness = new ShellHarness();
        (Account account, _) = harness.SeedInbox("Account_A", 5);
        harness.SetStatuses(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        await harness.InitializeAndWaitForListAsync(5);
        harness.Shell.Messages.SelectedRows = [.. harness.Shell.Messages.Items];

        harness.Shell.DeletePermanentlyCommand.Execute(null);

        await WaitForConfirmationAsync(harness);
        string text = Assert.Single(harness.ConfirmationTexts);
        Assert.Contains("5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2", text, StringComparison.Ordinal);
    }

    // Case 28: the empty-folder confirmation names the cleaned account label, but no sender or subject.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_empty_folder_confirmation_names_the_cleaned_account_label_and_no_sender_or_subject()
    {
        (ShellHarness harness, _, _) = await InitializedInFolderAsync(FolderRole.Junk, 3, "Account‮ N");

        harness.Shell.EmptyFolderCommand.Execute(null);

        await WaitForConfirmationAsync(harness);
        string text = Assert.Single(harness.ConfirmationTexts);
        Assert.Contains("Account N", text, StringComparison.Ordinal);
        Assert.DoesNotContain('‮', text);
        Assert.Contains("3", text, StringComparison.Ordinal);
        AssertNoPrivateText(text);
    }

    // ---- Case 30 -------------------------------------------------------------------------------------------

    // Case 30: "Ordner leeren" follows a search that starts and ends in the Trash.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task CanEmptyFolder_follows_a_search_started_and_ended_in_the_trash()
    {
        (ShellHarness harness, _, _) = await InitializedInFolderAsync(FolderRole.Trash, 2);
        int changed = 0;
        harness.Shell.EmptyFolderCommand.CanExecuteChanged += (_, _) => Interlocked.Increment(ref changed);
        Assert.True(harness.Shell.CanEmptyFolder);
        Assert.True(harness.Shell.EmptyFolderCommand.CanExecute(null));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.SearchGate = (_, _) => release.Task;
        harness.Shell.Search.Query = "marker";

        Task search = harness.Shell.Search.SearchAsync();
        await Wait.UntilAsync(() => harness.ReadModel.SearchCalls.Count == 1, "search started");

        Assert.False(harness.Shell.CanEmptyFolder);
        Assert.False(harness.Shell.EmptyFolderCommand.CanExecute(null));
        release.SetResult();
        await search;
        Assert.False(harness.Shell.CanEmptyFolder);

        harness.Shell.Search.Query = string.Empty;
        await harness.Shell.Search.SearchAsync();

        Assert.True(harness.Shell.CanEmptyFolder);
        Assert.True(harness.Shell.EmptyFolderCommand.CanExecute(null));
        Assert.True(Volatile.Read(ref changed) >= 2, "CanExecuteChanged must be raised when the search starts and ends");
    }

    // ---- Case 31 -------------------------------------------------------------------------------------------

    // Case 31: Dispose during an await inside the delete stores nothing and logs nothing.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Dispose_during_an_await_in_delete_stores_nothing()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(3);
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.RoleGate = () => release.Task;

        harness.Shell.DeleteCommand.Execute(null);
        await Wait.UntilAsync(() => harness.ReadModel.RoleGateEntered >= 1, "delete is waiting inside the role lookup");
        harness.Shell.Dispose();
        release.SetResult();
        await Wait.NegativeWindowAsync();
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Actions.Calls);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 31 positive control: the same wait without Dispose does store the command.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Without_dispose_the_delete_waiting_in_the_role_lookup_is_stored()
    {
        (ShellHarness harness, _, _, _) = await InitializedAsync(3);
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.RoleGate = () => release.Task;

        harness.Shell.DeleteCommand.Execute(null);
        await Wait.UntilAsync(() => harness.ReadModel.RoleGateEntered >= 1, "delete is waiting inside the role lookup");
        Assert.Empty(harness.Actions.Calls);
        release.SetResult();

        await WaitForCallsAsync(harness, "MoveToTrash");
    }
}
