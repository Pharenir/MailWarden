using MailWarden.Infrastructure.Background;

namespace MailWarden.Presentation.Tests.TestSupport;

/// <summary>Shared arrangement helpers for the AP-04b view-model tests. All data is artificial.</summary>
internal static class ActionsTestHelpers
{
    /// <summary>Text fragments that must never appear in a confirmation, banner or log line (sender and subject of seeded mails).</summary>
    public static readonly string[] PrivateMarkers = ["marker_subject", "marker_sender", "example.test"];

    /// <summary>One account with Inbox (<paramref name="count"/> mails, subjects containing a marker) and Trash, shell initialised.</summary>
    public static async Task<(ShellHarness Harness, Account Account, MailFolder Inbox, MailFolder Trash)> InitializedAsync(
        int count = 3,
        MessageFlags flags = MessageFlags.None,
        UiSettings? settings = null,
        TimeProvider? clock = null)
    {
        var harness = new ShellHarness(settings, clock);
        Account account = harness.ReadModel.AddAccount("Account_A");
        MailFolder inbox = harness.ReadModel.AddFolder(account, FolderRole.Inbox, "INBOX");
        MailFolder trash = harness.ReadModel.AddFolder(account, FolderRole.Trash, "Trash");
        for (int index = 0; index < count; index++)
        {
            harness.ReadModel.AddMessage(inbox, $"marker_subject_{index:D3}", fromName: "marker_sender_x", flags: flags);
        }

        harness.SetStatuses(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        await harness.InitializeAndWaitForListAsync(count);
        return (harness, account, inbox, trash);
    }

    /// <summary>Selects the given rows and waits until the reading pane settled (single row: Ready, otherwise Empty).</summary>
    public static async Task SelectAsync(ShellHarness harness, params MessageRowViewModel[] rows)
    {
        harness.Shell.Messages.SelectedRows = rows;
        if (rows.Length == 1)
        {
            await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Ready, "reading pane shows the selected row");
        }
        else
        {
            await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Empty, "reading pane empty");
        }
    }

    /// <summary>Runs a Running then Idle transition (what a sync after a command looks like) and waits for the reload.</summary>
    public static async Task SyncCycleAsync(ShellHarness harness, Account account)
    {
        int before = harness.ReadModel.ListMessagesCalls;
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        await Wait.UntilAsync(() => harness.ReadModel.ListMessagesCalls > before, "reload after the sync cycle");
        await Wait.NegativeWindowAsync();
    }

    /// <summary>Waits until the fake actions recorded <paramref name="count"/> calls with the name.</summary>
    public static Task WaitForCallsAsync(ShellHarness harness, string name, int count = 1) =>
        Wait.UntilAsync(() => harness.Actions.CallsNamed(name).Count >= count, $"{count} call(s) of {name}");

    /// <summary>Waits until a confirmation was requested.</summary>
    public static Task WaitForConfirmationAsync(ShellHarness harness, int count = 1) =>
        Wait.UntilAsync(() => harness.ConfirmationTexts.Count >= count, "confirmation requested");

    /// <summary>Asserts that no private marker (sender, subject, address) occurs in the text.</summary>
    public static void AssertNoPrivateText(string? text)
    {
        foreach (string marker in PrivateMarkers)
        {
            Assert.DoesNotContain(marker, text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }
}
