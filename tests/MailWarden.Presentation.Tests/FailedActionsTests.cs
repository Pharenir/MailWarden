using MailWarden.Infrastructure.Background;
using static MailWarden.Presentation.Tests.TestSupport.ActionsTestHelpers;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04b D2 (case 8 and the Q1 note of AP-03c): failed commands, the Actions banner and the Shift+Del hint.</summary>
[Trait("Category", "feature:ui")]
public sealed class FailedActionsTests
{
    private const string ServerText = "marker_server_text";

    // ---- Case 8 --------------------------------------------------------------------------------------------

    // Case 8: a failed delete brings the row back, is listed with a fixed text and raises the banner; discard clears it.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Failed_delete_brings_the_row_back_is_listed_with_fixed_text_and_discard_clears_it()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(3);
        MessageRowViewModel victim = harness.Shell.Messages.Items[0];
        await SelectAsync(harness, victim);
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash");
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 2, "row hidden while pending");
        Assert.Empty(harness.Shell.FailedActions);
        long commandId = Assert.Single(harness.Actions.CommandIds);

        harness.Actions.Fail(commandId, ServerText);
        await SyncCycleAsync(harness, account);

        Assert.Equal(3, harness.Shell.Messages.Items.Count);
        Assert.Contains(harness.Shell.Messages.Items, row => row.Message.Id == victim.Message.Id);
        PendingActionRow failed = Assert.Single(harness.Shell.FailedActions);
        Assert.Equal(commandId, failed.CommandId);
        Assert.False(string.IsNullOrWhiteSpace(failed.Text));
        Assert.DoesNotContain(ServerText, failed.Text, StringComparison.Ordinal);
        AssertNoPrivateText(failed.Text);
        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Banner));
        Assert.Contains("1", harness.Shell.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain(ServerText, harness.Shell.Banner, StringComparison.Ordinal);
        AssertNoPrivateText(harness.Shell.Banner);

        Assert.True(harness.Shell.DiscardFailedActionCommand.CanExecute(failed));
        harness.Shell.DiscardFailedActionCommand.Execute(failed);

        await WaitForCallsAsync(harness, "Discard");
        Assert.Equal(commandId, Assert.Single(harness.Actions.CallsNamed("Discard")).MessageIds[0]);
        await Wait.UntilAsync(() => harness.Shell.FailedActions.Count == 0, "failed list empty");
        await Wait.UntilAsync(() => harness.Shell.Banner is null, "banner cleared");
        Assert.Equal(3, harness.Shell.Messages.Items.Count);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 8: discarding a command that is still pending brings its rows back.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Discarding_a_pending_command_brings_the_rows_back()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(3);
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash");
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 2, "row hidden");

        await harness.Actions.DiscardAsync(Assert.Single(harness.Actions.CommandIds));
        await SyncCycleAsync(harness, account);

        Assert.Equal(3, harness.Shell.Messages.Items.Count);
        Assert.Empty(harness.Shell.FailedActions);
    }

    // Case 8: the banner source Actions names the count and follows discards.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Banner_names_the_number_of_failed_actions_and_follows_discards()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(4);
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        await SelectAsync(harness, rows[0]);
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash");
        await SelectAsync(harness, rows[1]);
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash", 2);
        foreach (long commandId in harness.Actions.CommandIds)
        {
            harness.Actions.Fail(commandId);
        }

        await SyncCycleAsync(harness, account);

        Assert.Equal(2, harness.Shell.FailedActions.Count);
        Assert.Contains("2", harness.Shell.Banner, StringComparison.Ordinal);

        harness.Shell.DiscardFailedActionCommand.Execute(harness.Shell.FailedActions[0]);
        await Wait.UntilAsync(() => harness.Shell.FailedActions.Count == 1, "one failed action left");
        await Wait.UntilAsync(() => harness.Shell.Banner is not null && !harness.Shell.Banner.Contains('2', StringComparison.Ordinal), "banner shows the new count");
        Assert.Contains("1", harness.Shell.Banner, StringComparison.Ordinal);
    }

    // Case 8: fixed text per kind - never the server text, never a private text, different kinds read differently.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failed_actions_of_every_kind_show_a_fixed_text_per_kind()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(5);
        MailFolder archive = harness.ReadModel.AddFolder(account, FolderRole.Archive, "Archive");
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];
        await SelectAsync(harness, rows[0]);
        harness.Shell.MarkReadCommand.Execute(null);
        await WaitForCallsAsync(harness, "SetSeen");
        await SelectAsync(harness, rows[1]);
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash");
        await SelectAsync(harness, rows[2]);
        harness.Shell.DeletePermanentlyCommand.Execute(null);
        await WaitForCallsAsync(harness, "DeletePermanently");
        await SelectAsync(harness, rows[3]);
        harness.Shell.MoveCommand.Execute(archive.Id);
        await WaitForCallsAsync(harness, "Move");
        foreach (long commandId in harness.Actions.CommandIds)
        {
            harness.Actions.Fail(commandId, ServerText);
        }

        await SyncCycleAsync(harness, account);

        Assert.Equal(4, harness.Shell.FailedActions.Count);
        string[] texts = [.. harness.Shell.FailedActions.Select(row => row.Text)];
        Assert.All(texts, text =>
        {
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain(ServerText, text, StringComparison.Ordinal);
            AssertNoPrivateText(text);
        });
        Assert.Equal(4, texts.Distinct().Count());
    }

    // Case 8 control: pending commands are not failed actions and raise no banner.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Pending_commands_are_not_listed_as_failed_and_raise_no_banner()
    {
        (ShellHarness harness, Account account, _, _) = await InitializedAsync(3);
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash");

        await SyncCycleAsync(harness, account);

        Assert.Empty(harness.Shell.FailedActions);
        Assert.Null(harness.Shell.Banner);
    }

    // ---- Q1 of AP-03c: Shift+Del without COPYUID -----------------------------------------------------------

    private const string SharedMessageId = "<mid_shared_1@example.test>";

    private static async Task<(ShellHarness Harness, Account Account, MailFolder Trash, MessageRowViewModel Row)> PermanentlyDeletedAsync()
    {
        (ShellHarness harness, Account account, MailFolder inbox, MailFolder trash) = await InitializedAsync(2);
        MailMessage tracked = harness.ReadModel.AddMessage(inbox, "marker_subject_tracked", messageId: SharedMessageId);
        await harness.Shell.Messages.ReloadAsync();
        MessageRowViewModel row = harness.Shell.Messages.Items.Single(candidate => candidate.Message.Id == tracked.Id);
        await SelectAsync(harness, row);
        harness.Shell.DeletePermanentlyCommand.Execute(null);
        await WaitForCallsAsync(harness, "DeletePermanently");
        return (harness, account, trash, row);
    }

    // Q1: the mail only reached the Trash, so the banner says so once.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Permanent_delete_that_only_reached_the_trash_raises_a_banner_once()
    {
        (ShellHarness harness, Account account, MailFolder trash, MessageRowViewModel row) = await PermanentlyDeletedAsync();
        harness.Actions.Complete(Assert.Single(harness.Actions.CommandIds));
        harness.ReadModel.AddMessage(trash, "marker_subject_tracked", messageId: SharedMessageId);
        Assert.Null(harness.Shell.Banner);

        await SyncCycleAsync(harness, account);

        string? first = harness.Shell.Banner;
        Assert.False(string.IsNullOrWhiteSpace(first));
        AssertNoPrivateText(first);
        Assert.Null(harness.ReadModel.Find(row.Message.Id));

        await SyncCycleAsync(harness, account);

        Assert.True(harness.Shell.Banner is null || harness.Shell.Banner == first, "the hint must not be repeated or doubled");
    }

    // Q1 control: a real permanent delete leaves no copy in the Trash and no banner.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Permanent_delete_without_a_trash_copy_raises_no_banner()
    {
        (ShellHarness harness, Account account, _, _) = await PermanentlyDeletedAsync();
        harness.Actions.Complete(Assert.Single(harness.Actions.CommandIds));

        await SyncCycleAsync(harness, account);
        await SyncCycleAsync(harness, account);

        Assert.Null(harness.Shell.Banner);
    }

    // Q1 control: a Trash mail with another Message-ID does not trigger the hint.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_different_message_id_in_the_trash_raises_no_banner()
    {
        (ShellHarness harness, Account account, MailFolder trash, _) = await PermanentlyDeletedAsync();
        harness.Actions.Complete(Assert.Single(harness.Actions.CommandIds));
        harness.ReadModel.AddMessage(trash, "marker_subject_other", messageId: "<mid_other_2@example.test>");

        await SyncCycleAsync(harness, account);

        Assert.Null(harness.Shell.Banner);
    }
}
