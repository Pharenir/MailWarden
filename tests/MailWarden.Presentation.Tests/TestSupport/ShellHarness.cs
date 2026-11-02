using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation.Tests.TestSupport;

/// <summary>Wires a <see cref="ShellViewModel"/> to the fakes. Fixed clock: 2026-10-31 12:00 UTC.</summary>
internal sealed class ShellHarness : IDisposable
{
    public static readonly DateTimeOffset FixedNow = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public ShellHarness(UiSettings? initialSettings = null, TimeProvider? clock = null, Func<MailContent, bool, bool, ReadingTheme, ReadingDocument>? documentBuilder = null)
    {
        ReadModel = new FakeMailboxReadModel();
        Controller = new FakeMailboxController();
        Actions = new FakeMailboxActions(ReadModel);
        Dispatcher = new FakeUiDispatcher();
        Settings = new FakeUiSettingsStore { Stored = initialSettings ?? UiSettings.Default };
        Log = new FakeCriticalLog();
        Clock = clock ?? new FixedClock(FixedNow);
        AttachmentHost = new FakeAttachmentHost();
        WorkRoot = Path.Combine(AppContext.BaseDirectory, "ap04c-ui", Guid.NewGuid().ToString("N"));
        AttachmentOpenFolder = Path.Combine(WorkRoot, "attachments-open");

        // ASSUMPTION (test-plan Q1): the shell takes the host and the open folder as two further constructor members.
        Shell = new ShellViewModel(
            ReadModel, Controller, Actions, Dispatcher, Settings, Log, Clock, documentBuilder,
            attachmentHost: AttachmentHost, attachmentOpenFolder: AttachmentOpenFolder);
        Shell.ConfirmationRequested += OnConfirmationRequested;
    }

    /// <summary>AP-04c attachment host fake (assumed: passed to the shell, see test-plan question Q1).</summary>
    public FakeAttachmentHost AttachmentHost { get; }

    /// <summary>Stands for <c>&lt;data&gt;\attachments-open</c>; below <see cref="WorkRoot"/>, created by the code under test.</summary>
    public string AttachmentOpenFolder { get; }

    /// <summary>Folder below the test output for files a test writes (save targets); removed by <see cref="Dispose"/>.</summary>
    public string WorkRoot { get; }

    public FakeMailboxReadModel ReadModel { get; }

    public FakeMailboxController Controller { get; }

    /// <summary>AP-04b actions fake (assumed: passed to the shell right after the controller; see test-plan question Q2).</summary>
    public FakeMailboxActions Actions { get; }

    /// <summary>
    /// How the harness answers <see cref="ShellViewModel.ConfirmationRequested"/>: true = yes, false = no, null = never
    /// answers (the request stays open).
    /// </summary>
    public bool? ConfirmationAnswer { get; set; } = true;

    /// <summary>Called with the 1-based request number just before a request is answered (to change data between preview and answer).</summary>
    public Action<int>? BeforeAnswer { get; set; }

    /// <summary>Texts of all confirmation requests, in order.</summary>
    public List<string> ConfirmationTexts { get; } = [];

    /// <summary>All confirmation requests, in order; a test with <see cref="ConfirmationAnswer"/> null answers one by hand.</summary>
    public List<ConfirmationRequest> ConfirmationRequests { get; } = [];

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

    /// <summary>Removes <see cref="WorkRoot"/> (best effort: a launched file may still be locked).</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(WorkRoot))
            {
                Directory.Delete(WorkRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort inside the test output.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort inside the test output.
        }
    }

    private void OnConfirmationRequested(object? sender, ConfirmationRequest request)
    {
        lock (ConfirmationTexts)
        {
            ConfirmationTexts.Add(request.Text);
            ConfirmationRequests.Add(request);
        }

        int number;
        lock (ConfirmationTexts)
        {
            number = ConfirmationTexts.Count;
        }

        BeforeAnswer?.Invoke(number);
        if (ConfirmationAnswer is { } answer)
        {
            request.Answer.TrySetResult(answer);
        }
    }
}
