using System.ComponentModel;
using System.Reflection;
using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04a cases 38-41 (D10): status handling, refresh, settings and the read-only guarantee.</summary>
[Trait("Category", "feature:ui")]
public sealed class ShellViewModelTests
{
    private const string MarkerSecret = "marker_secret_text";

    private static async Task<(ShellHarness Harness, Account Account, MailFolder Inbox)> InitializedAsync(int messageCount = 3)
    {
        var harness = new ShellHarness();
        (Account account, MailFolder inbox) = harness.SeedInbox("Account_A", messageCount);
        harness.SetStatuses(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        await harness.Shell.InitializeAsync();
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == messageCount, "inbox listed");
        await Wait.NegativeWindowAsync();
        return (harness, account, inbox);
    }

    private static SyncProgress ProgressOf(Account account, MailFolder inbox) =>
        new(account.Id, inbox.Id, FolderRole.Inbox, 1, 3, 10, 100);

    // ---- Case 38 -------------------------------------------------------------------------------------------

    // Case 38
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Progress_only_update_changes_the_status_text_but_does_not_reload()
    {
        (ShellHarness harness, Account account, MailFolder inbox) = await InitializedAsync();
        string? idleText = harness.AccountNode(account.Id).StatusText;
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await Wait.NegativeWindowAsync();
        int listCalls = harness.ReadModel.ListMessagesCalls;
        int countCalls = harness.ReadModel.CountUnreadCalls;
        int accountCalls = harness.ReadModel.ListAccountsCalls;

        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running, ProgressOf(account, inbox)));
        await Wait.NegativeWindowAsync();

        Assert.False(string.IsNullOrWhiteSpace(harness.AccountNode(account.Id).StatusText));
        Assert.NotEqual(idleText, harness.AccountNode(account.Id).StatusText);
        Assert.Equal(listCalls, harness.ReadModel.ListMessagesCalls);
        Assert.Equal(countCalls, harness.ReadModel.CountUnreadCalls);
        Assert.Equal(accountCalls, harness.ReadModel.ListAccountsCalls);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 38
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Status_of_an_unknown_account_is_ignored()
    {
        (ShellHarness harness, _, _) = await InitializedAsync();
        Guid stranger = Guid.NewGuid();
        int listCalls = harness.ReadModel.ListMessagesCalls;
        int countCalls = harness.ReadModel.CountUnreadCalls;
        string?[] textsBefore = [.. harness.Shell.Navigation.Items.Select(item => item.StatusText)];

        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(stranger, AccountSyncState.Running));
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(stranger, AccountSyncState.Idle));
        await Wait.NegativeWindowAsync();

        Assert.Equal(listCalls, harness.ReadModel.ListMessagesCalls);
        Assert.Equal(countCalls, harness.ReadModel.CountUnreadCalls);
        Assert.Equal(textsBefore, harness.Shell.Navigation.Items.Select(item => item.StatusText).ToArray());
        Assert.Empty(harness.Log.Entries);
    }

    // Case 38
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Transition_from_running_to_idle_reloads_navigation_and_list()
    {
        (ShellHarness harness, Account account, MailFolder inbox) = await InitializedAsync();
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await Wait.NegativeWindowAsync();
        int listCalls = harness.ReadModel.ListMessagesCalls;
        int countCalls = harness.ReadModel.CountUnreadCalls;
        Assert.Equal(3, harness.Shell.Navigation.Items[0].UnreadCount);
        harness.ReadModel.AddMessage(inbox, "arrived_during_sync");

        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));

        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 4, "list reloaded with the new message");
        await Wait.UntilAsync(() => harness.Shell.Navigation.Items[0].UnreadCount == 4, "navigation reloaded with the new count");
        Assert.True(harness.ReadModel.ListMessagesCalls > listCalls);
        Assert.True(harness.ReadModel.CountUnreadCalls > countCalls);
    }

    // Case 38
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Burst_of_idle_transitions_during_a_running_reload_coalesces_into_one_more_reload()
    {
        (ShellHarness harness, Account account, _) = await InitializedAsync();
        int baseline = harness.ReadModel.ListMessagesCalls;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.ListGate = (_, callNumber) => callNumber == baseline + 1 ? releaseFirst.Task : Task.CompletedTask;

        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        await Wait.UntilAsync(() => harness.ReadModel.ListMessagesCalls == baseline + 1, "first reload started and is blocked");
        for (int round = 0; round < 4; round++)
        {
            await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
            await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        }

        await Wait.NegativeWindowAsync();
        Assert.Equal(baseline + 1, harness.ReadModel.ListMessagesCalls);

        releaseFirst.SetResult();
        await Wait.UntilAsync(() => harness.ReadModel.ListMessagesCalls >= baseline + 2, "one more reload ran");
        await Wait.NegativeWindowAsync();
        await Wait.NegativeWindowAsync();

        Assert.Equal(baseline + 2, harness.ReadModel.ListMessagesCalls);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 38: the event arrives on a thread-pool thread and is marshalled through the UI dispatcher.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Status_event_is_marshalled_through_the_dispatcher()
    {
        (ShellHarness harness, Account account, _) = await InitializedAsync();
        string? before = harness.AccountNode(account.Id).StatusText;
        harness.Dispatcher.Queueing = true;
        int postsBefore = harness.Dispatcher.PostCount;

        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.NeedsSignIn));

        Assert.True(harness.Dispatcher.PostCount > postsBefore);
        Assert.Equal(before, harness.AccountNode(account.Id).StatusText);

        harness.Dispatcher.Pump();

        Assert.NotEqual(before, harness.AccountNode(account.Id).StatusText);
    }

    // Case 38 (G3 of AP-03d): an exception in the handler is logged by type name and never propagates.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Exception_in_the_status_handler_is_logged_by_type_name_and_does_not_propagate()
    {
        (ShellHarness harness, Account account, _) = await InitializedAsync();
        harness.Dispatcher.FailureOnPost = new InvalidOperationException(MarkerSecret);

        Exception? failure = Record.Exception(() => harness.Controller.Raise(FakeMailboxController.Status(account.Id, AccountSyncState.Running)));

        Assert.Null(failure);
        Assert.Contains("InvalidOperationException", harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerSecret, harness.Log.AllText, StringComparison.Ordinal);
    }

    // Case 38: a failing reload after Idle is logged by type name as well.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failing_reload_after_idle_is_logged_by_type_name_and_never_shows_the_message_text()
    {
        (ShellHarness harness, Account account, _) = await InitializedAsync();
        harness.ReadModel.ListGate = (_, _) => throw new InvalidOperationException(MarkerSecret);

        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        Exception? failure = await Record.ExceptionAsync(() => harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Idle)));

        Assert.Null(failure);
        await Wait.UntilAsync(() => harness.Log.AllText.Contains("InvalidOperationException", StringComparison.Ordinal), "failure logged");
        Assert.DoesNotContain(MarkerSecret, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerSecret, harness.Shell.Banner ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- Case 39 -------------------------------------------------------------------------------------------

    // Case 39
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Refresh_retries_accounts_needing_sign_in_or_waiting_after_error_and_triggers_the_others()
    {
        var harness = new ShellHarness();
        Account idle = harness.ReadModel.AddAccount("Account_Idle");
        Account needsSignIn = harness.ReadModel.AddAccount("Account_SignIn");
        Account waiting = harness.ReadModel.AddAccount("Account_Waiting");
        Account running = harness.ReadModel.AddAccount("Account_Running");
        harness.SetStatuses(
            FakeMailboxController.Status(idle.Id, AccountSyncState.Idle),
            FakeMailboxController.Status(needsSignIn.Id, AccountSyncState.NeedsSignIn),
            FakeMailboxController.Status(waiting.Id, AccountSyncState.WaitingAfterError, lastErrorType: "MailTransportException(Network)"),
            FakeMailboxController.Status(running.Id, AccountSyncState.Running));
        await harness.Shell.InitializeAsync();

        Assert.True(harness.Shell.RefreshCommand.CanExecute(null));
        harness.Shell.RefreshCommand.Execute(null);

        await Wait.UntilAsync(() => harness.Controller.Calls.Count >= 4, "four controller calls");
        await Wait.NegativeWindowAsync();
        (string Kind, Guid AccountId)[] calls = [.. harness.Controller.Calls];
        Assert.Equal(4, calls.Length);
        Assert.Contains(("Trigger", idle.Id), calls);
        Assert.Contains(("Retry", needsSignIn.Id), calls);
        Assert.Contains(("Retry", waiting.Id), calls);
        Assert.Contains(("Trigger", running.Id), calls);
    }

    // Case 39
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Refresh_without_accounts_does_nothing()
    {
        var harness = new ShellHarness();
        await harness.Shell.InitializeAsync();

        harness.Shell.RefreshCommand.Execute(null);
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Controller.Calls);
    }

    // ---- Case 40 -------------------------------------------------------------------------------------------

    // Case 40
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Stored_settings_are_applied_after_initialisation()
    {
        var harness = new ShellHarness(new UiSettings(ReadingPaneLayout.ListAbove, AppTheme.Dark, 300, 500, 350));

        await harness.Shell.InitializeAsync();

        Assert.Equal(ReadingPaneLayout.ListAbove, harness.Shell.Layout);
        Assert.Equal(AppTheme.Dark, harness.Shell.Theme);
        Assert.Equal(300d, harness.Shell.NavigationWidth);
        Assert.Equal(500d, harness.Shell.ListWidth);
        Assert.Equal(350d, harness.Shell.ListHeight);
    }

    // Case 40
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Layout_and_theme_changes_raise_property_changed_and_save_all_four_values()
    {
        var harness = new ShellHarness();
        harness.SeedInbox();
        await harness.Shell.InitializeAsync();
        List<string?> changed = [];
        ((INotifyPropertyChanged)harness.Shell).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        harness.Shell.Layout = ReadingPaneLayout.ListAbove;
        harness.Shell.Theme = AppTheme.Dark;

        Assert.Contains(nameof(ShellViewModel.Layout), changed);
        Assert.Contains(nameof(ShellViewModel.Theme), changed);
        Assert.Equal(ReadingPaneLayout.ListAbove, harness.Shell.Layout);
        Assert.Equal(AppTheme.Dark, harness.Shell.Theme);
        UiSettings saved = harness.Settings.Saved.Last();
        Assert.Equal(new UiSettings(ReadingPaneLayout.ListAbove, AppTheme.Dark, UiSettings.Default.NavigationWidth, UiSettings.Default.ListWidth, UiSettings.Default.ListHeight), saved);
        Assert.Null(harness.Shell.Banner);
    }

    // Case 40
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Size_changes_are_saved()
    {
        var harness = new ShellHarness();
        await harness.Shell.InitializeAsync();

        harness.Shell.NavigationWidth = 321;
        harness.Shell.ListWidth = 654;
        harness.Shell.ListHeight = 222;

        Assert.Equal(321d, harness.Shell.NavigationWidth);
        Assert.Equal(654d, harness.Shell.ListWidth);
        Assert.Equal(222d, harness.Shell.ListHeight);
        UiSettings last = harness.Settings.Saved.Last();
        Assert.Equal(321d, last.NavigationWidth);
        Assert.Equal(654d, last.ListWidth);
        Assert.Equal(222d, last.ListHeight);
    }

    // Case 55 (K9): each layout keeps its own list size; changing one does not touch the other.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task List_width_and_list_height_are_saved_independently_and_raise_property_changed()
    {
        var harness = new ShellHarness();
        await harness.Shell.InitializeAsync();
        List<string?> changed = [];
        ((INotifyPropertyChanged)harness.Shell).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        harness.Shell.ListWidth = 600;
        UiSettings afterWidth = harness.Settings.Saved.Last();
        harness.Shell.ListHeight = 250;
        UiSettings afterHeight = harness.Settings.Saved.Last();

        Assert.Equal(600d, afterWidth.ListWidth);
        Assert.Equal(UiSettings.Default.ListHeight, afterWidth.ListHeight);
        Assert.Equal(600d, afterHeight.ListWidth);
        Assert.Equal(250d, afterHeight.ListHeight);
        Assert.Contains(nameof(ShellViewModel.ListWidth), changed);
        Assert.Contains(nameof(ShellViewModel.ListHeight), changed);
    }
    // Case 40
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Toggle_layout_command_switches_between_the_two_layouts_and_saves_each_time()
    {
        var harness = new ShellHarness();
        await harness.Shell.InitializeAsync();
        Assert.Equal(ReadingPaneLayout.SideBySide, harness.Shell.Layout);
        Assert.True(harness.Shell.ToggleLayoutCommand.CanExecute(null));

        harness.Shell.ToggleLayoutCommand.Execute(null);
        Assert.Equal(ReadingPaneLayout.ListAbove, harness.Shell.Layout);
        Assert.Equal(ReadingPaneLayout.ListAbove, harness.Settings.Saved.Last().Layout);

        harness.Shell.ToggleLayoutCommand.Execute(null);
        Assert.Equal(ReadingPaneLayout.SideBySide, harness.Shell.Layout);
        Assert.Equal(ReadingPaneLayout.SideBySide, harness.Settings.Saved.Last().Layout);
    }

    // Case 40
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failed_save_sets_the_banner_and_logs_the_type_name_but_keeps_the_app_running()
    {
        var harness = new ShellHarness();
        harness.SeedInbox();
        await harness.Shell.InitializeAsync();
        harness.Settings.SaveFailure = new IOException(MarkerSecret);

        Exception? failure = Record.Exception(() => harness.Shell.Layout = ReadingPaneLayout.ListAbove);

        Assert.Null(failure);
        Assert.Equal(ReadingPaneLayout.ListAbove, harness.Shell.Layout);
        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Banner));
        Assert.DoesNotContain(MarkerSecret, harness.Shell.Banner, StringComparison.Ordinal);
        Assert.Contains("IOException", harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerSecret, harness.Log.AllText, StringComparison.Ordinal);
    }

    // Case 40
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failed_load_uses_the_defaults_and_sets_the_banner()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.RootPath, UiSettingsFile.FileName);
        await File.WriteAllTextAsync(path, "{broken");
        var readModel = new FakeMailboxReadModel();
        readModel.AddFolder(readModel.AddAccount(), FolderRole.Inbox, "INBOX");
        var log = new FakeCriticalLog();
        var shell = new ShellViewModel(
            readModel,
            new FakeMailboxController(),
            new FakeMailboxActions(readModel),
            new FakeUiDispatcher(),
            new UiSettingsFile(path),
            log,
            new FixedClock(ShellHarness.FixedNow));

        await shell.InitializeAsync();

        Assert.Equal(UiSettings.Default.Layout, shell.Layout);
        Assert.Equal(UiSettings.Default.Theme, shell.Theme);
        Assert.Equal(UiSettings.Default.NavigationWidth, shell.NavigationWidth);
        Assert.Equal(UiSettings.Default.ListWidth, shell.ListWidth);
        Assert.Equal(UiSettings.Default.ListHeight, shell.ListHeight);
        Assert.False(string.IsNullOrWhiteSpace(shell.Banner));
        Assert.Equal(7, shell.Navigation.Items.Count);
    }

    // Case 40: both defaults and the failed-load banner survive a later change (the app keeps working).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task App_keeps_working_after_a_failed_load()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.RootPath, UiSettingsFile.FileName);
        await File.WriteAllTextAsync(path, "{broken");
        var readModelForActions = new FakeMailboxReadModel();
        var shell = new ShellViewModel(
            readModelForActions,
            new FakeMailboxController(),
            new FakeMailboxActions(readModelForActions),
            new FakeUiDispatcher(),
            new UiSettingsFile(path),
            new FakeCriticalLog(),
            new FixedClock(ShellHarness.FixedNow));
        await shell.InitializeAsync();

        Exception? failure = Record.Exception(() => shell.Theme = AppTheme.Light);

        Assert.Null(failure);
        Assert.Equal(AppTheme.Light, shell.Theme);
    }

    // ---- Case 41 -------------------------------------------------------------------------------------------

    // Case 41
    [Fact]
    public void Read_model_interface_has_only_read_members()
    {
        string[] methods = [.. typeof(IMailboxReadModel).GetMethods().Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal)];

        Assert.Equal(
            new[] { "CopyAttachmentAsync", "CountUnreadAsync", "GetFolderRoleAsync", "ListAccountsAsync", "ListFoldersAsync", "ListMessagesAsync", "ReadContentAsync", "SearchAsync" },
            methods);
        Assert.Empty(typeof(IMailboxReadModel).GetProperties());
        Assert.Empty(typeof(IMailboxReadModel).GetEvents());
    }

    // Case 41
    [Fact]
    public void Controller_interface_offers_only_trigger_and_retry_as_actions()
    {
        string[] actions = [.. typeof(IMailboxController).GetMethods().Where(method => !method.IsSpecialName).Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal)];

        Assert.Equal(new[] { "RetryAccount", "TriggerAccount" }, actions);
    }

    // Case 41
    [Fact]
    public void View_models_expose_no_write_style_methods()
    {
        string[] forbidden = ["Delete", "Move", "Mark", "Flag", "Archive", "Seen", "Empty", "Send", "Save"];
        Type[] types = [typeof(ShellViewModel), typeof(NavigationViewModel), typeof(MessageListViewModel), typeof(ReadingPaneViewModel)];

        string[] offenders =
        [
            .. types.SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .Where(method => method.Name != nameof(ShellViewModel.SaveWindowBounds)) // AP-04b D4: window settings, not mail
                .Where(method => !(type == typeof(ReadingPaneViewModel) && method.Name == nameof(ReadingPaneViewModel.SaveAttachmentAsync))) // AP-04c: writes a local file after the dialog, never the mailbox
                .Where(method => forbidden.Any(word => method.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Select(method => $"{type.Name}.{method.Name}")),
        ];
        Assert.Empty(offenders);
    }

    // Case 41: a full reading session records no controller call and leaves every message unread.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reading_session_makes_no_controller_call_and_does_not_mark_messages_read()
    {
        (ShellHarness harness, Account account, MailFolder inbox) = await InitializedAsync(5);
        int unreadBefore = await harness.ReadModel.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox));

        harness.Shell.Messages.SelectedRow = harness.Shell.Messages.Items[0];
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Ready, "first message shown");
        harness.Shell.Messages.SelectedRow = harness.Shell.Messages.Items[1];
        await Wait.UntilAsync(() => harness.ReadModel.ReadCalls >= 2, "second message read");
        harness.Shell.Navigation.SelectedItem = harness.Shell.Navigation.Items[1];
        await harness.Shell.Messages.ReloadAsync();
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Controller.Calls);
        Assert.Equal(unreadBefore, await harness.ReadModel.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox)));
        Assert.Equal(5, await harness.ReadModel.CountUnreadAsync(new MessageQuery(FolderId: inbox.Id)));
    }

    // Case 41: only the refresh command talks to the controller, and only with the two documented actions.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Only_refresh_calls_the_controller_with_trigger_or_retry()
    {
        (ShellHarness harness, Account account, _) = await InitializedAsync();

        harness.Shell.RefreshCommand.Execute(null);
        await Wait.UntilAsync(() => harness.Controller.Calls.Count >= 1, "refresh reached the controller");
        await Wait.NegativeWindowAsync();

        (string Kind, Guid AccountId) call = Assert.Single(harness.Controller.Calls);
        Assert.Equal(("Trigger", account.Id), call);
    }

    // ---- Case 62 (K15) -------------------------------------------------------------------------------------

    // Case 62
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(AccountSyncState.WaitingAfterError)]
    [InlineData(AccountSyncState.NeedsSignIn)]
    public async Task Transition_to_an_error_or_sign_in_state_reloads_once(AccountSyncState target)
    {
        (ShellHarness harness, Account account, MailFolder inbox) = await InitializedAsync();
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await Wait.NegativeWindowAsync();
        int listCalls = harness.ReadModel.ListMessagesCalls;
        int countCalls = harness.ReadModel.CountUnreadCalls;
        harness.ReadModel.AddMessage(inbox, "stored_by_the_interrupted_pass");

        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, target, lastErrorType: "MailTransportException(Network)"));

        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 4, "list reloaded");
        await Wait.UntilAsync(() => harness.Shell.Navigation.Items[0].UnreadCount == 4, "navigation reloaded");
        await Wait.NegativeWindowAsync();
        Assert.Equal(listCalls + 1, harness.ReadModel.ListMessagesCalls);
        Assert.True(harness.ReadModel.CountUnreadCalls > countCalls);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 62: a burst of such transitions during a running reload coalesces into one more reload.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Burst_of_error_and_sign_in_transitions_coalesces_into_one_more_reload()
    {
        (ShellHarness harness, Account account, _) = await InitializedAsync();
        int baseline = harness.ReadModel.ListMessagesCalls;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.ListGate = (_, callNumber) => callNumber == baseline + 1 ? releaseFirst.Task : Task.CompletedTask;

        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.WaitingAfterError));
        await Wait.UntilAsync(() => harness.ReadModel.ListMessagesCalls == baseline + 1, "first reload started and is blocked");
        for (int round = 0; round < 3; round++)
        {
            await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
            await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.NeedsSignIn));
            await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
            await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.WaitingAfterError));
        }

        await Wait.NegativeWindowAsync();
        Assert.Equal(baseline + 1, harness.ReadModel.ListMessagesCalls);

        releaseFirst.SetResult();
        await Wait.UntilAsync(() => harness.ReadModel.ListMessagesCalls >= baseline + 2, "one more reload ran");
        await Wait.NegativeWindowAsync();
        await Wait.NegativeWindowAsync();

        Assert.Equal(baseline + 2, harness.ReadModel.ListMessagesCalls);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 62 positive control: a progress-only update in Running still does not reload.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Staying_in_the_error_state_does_not_reload_again_without_a_transition_back()
    {
        (ShellHarness harness, Account account, _) = await InitializedAsync();
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.WaitingAfterError));
        await Wait.NegativeWindowAsync();
        int listCalls = harness.ReadModel.ListMessagesCalls;

        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.WaitingAfterError, lastErrorType: "MailTransportException(Network)"));
        await Wait.NegativeWindowAsync();

        Assert.Equal(listCalls, harness.ReadModel.ListMessagesCalls);
    }

    // ---- Case 63 (K4) --------------------------------------------------------------------------------------

    // Case 63
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Dispose_during_a_slow_reload_makes_no_further_read_model_call_and_logs_nothing()
    {
        (ShellHarness harness, Account account, _) = await InitializedAsync();
        int baseline = harness.ReadModel.ListMessagesCalls;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.ListGate = (_, callNumber) => callNumber == baseline + 1 ? releaseFirst.Task : Task.CompletedTask;
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        await Wait.UntilAsync(() => harness.ReadModel.ListMessagesCalls == baseline + 1, "reload started and is blocked");
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));

        harness.Shell.Dispose();
        releaseFirst.SetResult();
        await Wait.NegativeWindowAsync();
        await Wait.NegativeWindowAsync();
        int accountCalls = harness.ReadModel.ListAccountsCalls;
        int countCalls = harness.ReadModel.CountUnreadCalls;
        int readCalls = harness.ReadModel.ReadCalls;
        await Wait.NegativeWindowAsync();

        Assert.Equal(baseline + 1, harness.ReadModel.ListMessagesCalls);
        Assert.Equal(accountCalls, harness.ReadModel.ListAccountsCalls);
        Assert.Equal(countCalls, harness.ReadModel.CountUnreadCalls);
        Assert.Equal(readCalls, harness.ReadModel.ReadCalls);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 63: after Dispose, status events and settings changes are ignored without an exception or a log line.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Events_after_dispose_are_ignored()
    {
        (ShellHarness harness, Account account, _) = await InitializedAsync();
        harness.Shell.Dispose();
        int listCalls = harness.ReadModel.ListMessagesCalls;
        int countCalls = harness.ReadModel.CountUnreadCalls;

        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
            await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        });
        await Wait.NegativeWindowAsync();

        Assert.Null(failure);
        Assert.Equal(listCalls, harness.ReadModel.ListMessagesCalls);
        Assert.Equal(countCalls, harness.ReadModel.CountUnreadCalls);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 63: Dispose is idempotent.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Dispose_twice_is_fine()
    {
        (ShellHarness harness, _, _) = await InitializedAsync();

        harness.Shell.Dispose();
        Exception? failure = Record.Exception(harness.Shell.Dispose);

        Assert.Null(failure);
    }

    // Case 63 positive control: a shell that is not disposed keeps reloading (the guard above is not vacuous).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Without_dispose_the_pending_second_reload_does_run()
    {
        (ShellHarness harness, Account account, _) = await InitializedAsync();
        int baseline = harness.ReadModel.ListMessagesCalls;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.ListGate = (_, callNumber) => callNumber == baseline + 1 ? releaseFirst.Task : Task.CompletedTask;
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        await Wait.UntilAsync(() => harness.ReadModel.ListMessagesCalls == baseline + 1, "reload started and is blocked");
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Running));
        await harness.Controller.RaiseOnPoolAsync(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));

        releaseFirst.SetResult();

        await Wait.UntilAsync(() => harness.ReadModel.ListMessagesCalls >= baseline + 2, "second reload ran");
    }

    // ---- AP-04e case 22 (E1, E2) ---------------------------------------------------------------------------

    // Case 22: Nightshade is chosen like the other themes: change event, saved with all other values kept.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Choosing_nightshade_raises_property_changed_and_saves_it()
    {
        UiSettings stored = new(ReadingPaneLayout.ListAbove, AppTheme.Light, 300, 500, 350);
        var harness = new ShellHarness(stored);
        harness.SeedInbox();
        await harness.Shell.InitializeAsync();
        Assert.Equal(AppTheme.Light, harness.Shell.Theme);
        List<string?> changed = [];
        ((INotifyPropertyChanged)harness.Shell).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        harness.Shell.Theme = AppTheme.Nightshade;

        Assert.Contains(nameof(ShellViewModel.Theme), changed);
        Assert.Equal(AppTheme.Nightshade, harness.Shell.Theme);
        Assert.Equal(stored with { Theme = AppTheme.Nightshade }, harness.Settings.Saved.Last());
        Assert.Null(harness.Shell.Banner);
    }

    // Case 22 / E2: without a stored choice the shell starts on Nightshade, and a change to another theme and back is saved each time.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Shell_starts_on_nightshade_by_default_and_every_change_is_saved()
    {
        var harness = new ShellHarness();
        harness.SeedInbox();
        await harness.Shell.InitializeAsync();
        Assert.Equal(AppTheme.Nightshade, harness.Shell.Theme);
        int savesBefore = harness.Settings.Saved.Count;

        harness.Shell.Theme = AppTheme.Dark;
        harness.Shell.Theme = AppTheme.Nightshade;

        Assert.Equal(AppTheme.Nightshade, harness.Shell.Theme);
        Assert.Equal(savesBefore + 2, harness.Settings.Saved.Count);
        Assert.Equal(AppTheme.Dark, harness.Settings.Saved[^2].Theme);
        Assert.Equal(AppTheme.Nightshade, harness.Settings.Saved[^1].Theme);
    }

    // Case 22 / E2: a stored theme is kept by the shell, whichever it is.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(AppTheme.System)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Nightshade)]
    public async Task A_stored_theme_is_applied_by_the_shell(AppTheme theme)
    {
        var harness = new ShellHarness(UiSettings.Default with { Theme = theme });

        await harness.Shell.InitializeAsync();

        Assert.Equal(theme, harness.Shell.Theme);
    }
}
