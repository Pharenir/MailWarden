using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation.Tests.TestSupport;

/// <summary>Wires a <see cref="ShellViewModel"/> to the fakes. Fixed clock: 2026-10-31 12:00 UTC.</summary>
internal sealed class ShellHarness
{
    public static readonly DateTimeOffset FixedNow = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public ShellHarness(UiSettings? initialSettings = null, TimeProvider? clock = null, Func<MailContent, bool, bool, ReadingDocument>? documentBuilder = null)
    {
        ReadModel = new FakeMailboxReadModel();
        Controller = new FakeMailboxController();
        Actions = new FakeMailboxActions(ReadModel);
        Dispatcher = new FakeUiDispatcher();
        Settings = new FakeUiSettingsStore { Stored = initialSettings ?? UiSettings.Default };
        Log = new FakeCriticalLog();
        Clock = clock ?? new FixedClock(FixedNow);
        Shell = new ShellViewModel(ReadModel, Controller, Actions, Dispatcher, Settings, Log, Clock, documentBuilder);
        Shell.ConfirmationRequested += OnConfirmationRequested;
    }

    public FakeMailboxReadModel ReadModel { get; }

    public FakeMailboxController Controller { get; }

    /// <summary>AP-04b actions fake (assumed: passed to the shell right after the controller; see test-plan question Q2).</summary>
    public FakeMailboxActions Actions { get; }

    /// <summary>
    /// How the harness answers <see cref="ShellViewModel.ConfirmationRequested"/>: true = yes, false = no, null = never
    /// answers (the request stays open).
    /// </summary>
    public bool? ConfirmationAnswer { get; set; } = true;

    /// <summary>Texts of all confirmation requests, in order.</summary>
    public List<string> ConfirmationTexts { get; } = [];

    public FakeUiDispatcher Dispatcher { get; }

    public FakeUiSettingsStore Settings { get; }

    public FakeCriticalLog Log { get; }

    public TimeProvider Clock { get; }

    public ShellViewModel Shell { get; }

    /// <summary>Seeds one account with Inbox and Trash; the Inbox holds <paramref name="messageCount"/> messages.</summary>
    public (Account Account, MailFolder Inbox, MailFolder Trash) SeedInboxAndTrash(string displayName = "Account_A", int messageCount = 3)
    {
        (Account account, MailFolder inbox) = SeedInbox(displayName, messageCount);
        MailFolder trash = ReadModel.AddFolder(account, FolderRole.Trash, "Trash");
        return (account, inbox, trash);
    }

    /// <summary>Initialises the shell, waits for the inbox list and returns once the first load settled.</summary>
    public async Task InitializeAndWaitForListAsync(int expectedRows)
    {
        await Shell.InitializeAsync();
        await Wait.UntilAsync(() => Shell.Messages.Items.Count == expectedRows, "initial list");
        await Wait.NegativeWindowAsync();
    }

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

    private void OnConfirmationRequested(object? sender, ConfirmationRequest request)
    {
        lock (ConfirmationTexts)
        {
            ConfirmationTexts.Add(request.Text);
        }

        if (ConfirmationAnswer is { } answer)
        {
            request.Answer.TrySetResult(answer);
        }
    }
}
