using MailWarden.Infrastructure.Background;

namespace MailWarden.Presentation.Tests.TestSupport;

/// <summary>Wires a <see cref="ShellViewModel"/> to the fakes. Fixed clock: 2026-10-31 12:00 UTC.</summary>
internal sealed class ShellHarness
{
    public static readonly DateTimeOffset FixedNow = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public ShellHarness(UiSettings? initialSettings = null, TimeProvider? clock = null)
    {
        ReadModel = new FakeMailboxReadModel();
        Controller = new FakeMailboxController();
        Dispatcher = new FakeUiDispatcher();
        Settings = new FakeUiSettingsStore { Stored = initialSettings ?? UiSettings.Default };
        Log = new FakeCriticalLog();
        Clock = clock ?? new FixedClock(FixedNow);
        Shell = new ShellViewModel(ReadModel, Controller, Dispatcher, Settings, Log, Clock);
    }

    public FakeMailboxReadModel ReadModel { get; }

    public FakeMailboxController Controller { get; }

    public FakeUiDispatcher Dispatcher { get; }

    public FakeUiSettingsStore Settings { get; }

    public FakeCriticalLog Log { get; }

    public TimeProvider Clock { get; }

    public ShellViewModel Shell { get; }

    /// <summary>Seeds one account with an Inbox holding <paramref name="messageCount"/> messages and returns both.</summary>
    public (Account Account, MailFolder Inbox) SeedInbox(string displayName = "Account_A", int messageCount = 3)
    {
        Account account = ReadModel.AddAccount(displayName);
        MailFolder inbox = ReadModel.AddFolder(account, FolderRole.Inbox, "INBOX");
        for (int index = 0; index < messageCount; index++)
        {
            ReadModel.AddMessage(inbox, subject: $"subject_{index:D3}");
        }

        return (account, inbox);
    }

    /// <summary>Marks the account as present in the controller's current status list.</summary>
    public void SetStatuses(params AccountSyncStatus[] statuses) => Controller.CurrentStatus = statuses;

    /// <summary>Finds a top-level navigation node of an account.</summary>
    public NavigationItem AccountNode(Guid accountId) =>
        Shell.Navigation.Items.Single(item => item.AccountId == accountId && item.View is null);
}
