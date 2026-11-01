using System.ComponentModel;
using System.Windows.Input;
using MailWarden.Core.Commands;
using MailWarden.Core.Model;
using MailWarden.Core.Text;
using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation;

/// <summary>
/// A failed command in the list of failed actions.
/// </summary>
/// <param name="CommandId">Id of the command in the queue.</param>
/// <param name="Text">Fixed text per kind of command, never the stored error.</param>
public sealed record PendingActionRow(long CommandId, string Text);

/// <summary>
/// A question to the user before something cannot be undone. The text holds counts and fixed words only, never a sender or a
/// subject. The window answers true to go on; anything else stops the action.
/// </summary>
/// <param name="Text">The question.</param>
/// <param name="Answer">Completed with the answer of the user.</param>
public sealed record ConfirmationRequest(string Text, TaskCompletionSource<bool> Answer);

/// <summary>
/// Root view model of the main window: connects navigation, message list, reading pane, search and background service, and
/// keeps the settings. Its commands only store commands in the queue (<see cref="IMailboxActions"/>); it never changes
/// anything on a server itself. Actions run one after the other in the order of their start, each on the current selection.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    // How long a mail must be shown before the switch marks it as read.
    private static readonly TimeSpan ReadOnSelectDelay = TimeSpan.FromSeconds(1.5);

    // How often "Ordner leeren" asks again when new mail keeps arriving during the question.
    private const int MaxEmptyConfirmations = 3;

    private const int MaxMoveTargetCharacters = 200;

    private readonly IMailboxReadModel readModel;
    private readonly IMailboxActions actions;
    private readonly TimeProvider timeProvider;
    private readonly IMailboxController controller;
    private readonly IUiDispatcher dispatcher;
    private readonly IUiSettingsStore settings;
    private readonly ICriticalLog log;
    private readonly ShellBanner banner = new();
    private readonly RelayCommand toggleLayoutCommand;
    private readonly RelayCommand refreshCommand;
    private readonly RelayCommand markReadCommand;
    private readonly RelayCommand markUnreadCommand;
    private readonly RelayCommand toggleFlagCommand;
    private readonly RelayCommand deleteCommand;
    private readonly RelayCommand deletePermanentlyCommand;
    private readonly RelayCommand emptyFolderCommand;
    private readonly ParameterRelayCommand moveCommand;
    private readonly ParameterRelayCommand discardFailedActionCommand;
    private readonly PendingMailState pendingMail;
    private readonly PermanentDeleteWatcher permanentDeleteWatcher;

    // Coalesces reload requests: several requests while one reload runs cause exactly one more.
    private readonly Lock reloadGate = new();
    private bool isReloadRunning;
    private bool isReloadPending;

    private ReadingPaneLayout layout;
    private AppTheme theme;
    private double navigationWidth;
    private double listWidth;
    private double listHeight;
    private bool markReadOnSelect;
    private WindowBounds windowBounds = new(null, null, null, null, false);
    private IReadOnlyList<PendingActionRow> failedActions = new List<PendingActionRow>();
    private IReadOnlyList<NavigationItem> moveTargets = new List<NavigationItem>();
    private int moveTargetGeneration;
    private MailFolder? emptyTarget;
    private int emptyTargetGeneration;

    // The tail of the running actions; it never faults.
    private Task actionTail = Task.CompletedTask;
    private TaskCompletionSource<bool>? openConfirmation;

    // The message the switch has last looked at, and the wait for it: a reload that replaces the row object restarts nothing.
    private long? readOnSelectMessageId;
    private CancellationTokenSource? readOnSelectCancellation;

    private Task pendingSelectionLoad = Task.CompletedTask;
    private bool isSubscribed;
    private bool isDisposed;

    // Last known state per account; a failed cycle reloads only when the state changes.
    private readonly Dictionary<Guid, AccountSyncState> lastStates = [];

    // Classification results per known account since the app started; touched on the UI thread only.
    private readonly Dictionary<Guid, ClassificationTally> classificationTallies = [];

    /// <summary>Creates the shell and loads the settings. Nothing else runs before <see cref="InitializeAsync"/>.</summary>
    /// <param name="readModel">Read access to the stored mail.</param>
    /// <param name="controller">The background service.</param>
    /// <param name="actions">Stores server changes in the command queue.</param>
    /// <param name="dispatcher">Runs status changes on the UI thread.</param>
    /// <param name="settings">Store of the UI settings.</param>
    /// <param name="log">Critical log.</param>
    /// <param name="timeProvider">Clock of the date texts, of the time limit of the document build and of the read-on-select wait.</param>
    /// <param name="documentBuilder">
    /// Builds the reading document from content, approval of remote images and approval of embedded images; null means
    /// <see cref="ReadingDocumentBuilder.Build"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public ShellViewModel(
        IMailboxReadModel readModel,
        IMailboxController controller,
        IMailboxActions actions,
        IUiDispatcher dispatcher,
        IUiSettingsStore settings,
        ICriticalLog log,
        TimeProvider timeProvider,
        Func<MailContent, bool, bool, ReadingDocument>? documentBuilder = null)
    {
        this.readModel = readModel ?? throw new ArgumentNullException(nameof(readModel));
        this.actions = actions ?? throw new ArgumentNullException(nameof(actions));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.log = log ?? throw new ArgumentNullException(nameof(log));

        Navigation = new NavigationViewModel(readModel, controller, log, banner);
        pendingMail = new PendingMailState(actions, log, banner);
        permanentDeleteWatcher = new PermanentDeleteWatcher(readModel, banner);
        Messages = new MessageListViewModel(readModel, log, banner, timeProvider, pendingMail);
        Search = new SearchViewModel(Messages);
        Reading = new ReadingPaneViewModel(readModel, log, timeProvider, documentBuilder ?? ReadingDocumentBuilder.Build);
        toggleLayoutCommand = new RelayCommand(ToggleLayout);
        refreshCommand = new RelayCommand(Refresh);
        markReadCommand = new RelayCommand(() => StartUserAction(() => ChangeSeenAsync(Messages.SelectedRows, true)), HasSelection);
        markUnreadCommand = new RelayCommand(() => StartUserAction(() => ChangeSeenAsync(Messages.SelectedRows, false)), HasSelection);
        toggleFlagCommand = new RelayCommand(() => StartUserAction(ToggleFlagAsync), HasSelection);
        deleteCommand = new RelayCommand(() => StartUserAction(DeleteAsync), HasSelection);
        deletePermanentlyCommand = new RelayCommand(() => StartUserAction(DeletePermanentlyAsync), HasSelection);
        emptyFolderCommand = new RelayCommand(() => StartUserAction(EmptyFolderAsync), () => CanEmptyFolder);
        moveCommand = new ParameterRelayCommand(ExecuteMove, _ => CanMove);
        discardFailedActionCommand = new ParameterRelayCommand(ExecuteDiscardFailedAction, parameter => parameter is PendingActionRow);

        banner.Changed += OnBannerChanged;
        pendingMail.Refreshed += OnPendingMailRefreshed;
        Search.PropertyChanged += OnSearchPropertyChanged;
        Navigation.PropertyChanged += OnNavigationPropertyChanged;
        Messages.PropertyChanged += OnMessagesPropertyChanged;
        ApplySettings(LoadSettings());
    }

    /// <summary>The navigation tree.</summary>
    public NavigationViewModel Navigation { get; }

    /// <summary>The message list.</summary>
    public MessageListViewModel Messages { get; }

    /// <summary>The reading pane.</summary>
    public ReadingPaneViewModel Reading { get; }

    /// <summary>The search.</summary>
    public SearchViewModel Search { get; }

    /// <summary>
    /// True when a mail shown for at least 1.5 s is marked as read; a change is saved. Off, selecting changes nothing on a server.
    /// </summary>
    public bool MarkReadOnSelect
    {
        get => markReadOnSelect;
        set
        {
            if (SetProperty(ref markReadOnSelect, value))
            {
                SaveSettings();
                readOnSelectMessageId = null;
                UpdateReadOnSelectWait();
            }
        }
    }

    /// <summary>Failed commands, until they are discarded.</summary>
    public IReadOnlyList<PendingActionRow> FailedActions => failedActions;

    /// <summary>True when <see cref="FailedActions"/> has entries.</summary>
    public bool HasFailedActions => failedActions.Count > 0;

    /// <summary>
    /// Target folders of <see cref="MoveCommand"/>: the folders of the account of the selection without the source folder,
    /// Gmail "All Mail" and the local Outbox. Empty without a selection and for a selection over several accounts.
    /// </summary>
    public IReadOnlyList<NavigationItem> MoveTargets => moveTargets;

    /// <summary>True when the selection can be moved: at least one row, all of one account.</summary>
    public bool CanMove => SingleAccountOfSelection() is not null;

    /// <summary>
    /// True when the list shows exactly one folder with role Trash or Junk. A group view over several accounts has more than
    /// one such folder and cannot be emptied at once.
    /// </summary>
    public bool CanEmptyFolder => emptyTarget is not null;

    /// <summary>The stored size and position of the main window.</summary>
    public WindowBounds WindowBounds => windowBounds;

    /// <summary>Arrangement of list and reading pane; a change is saved.</summary>
    public ReadingPaneLayout Layout
    {
        get => layout;
        set
        {
            if (SetProperty(ref layout, value))
            {
                SaveSettings();
            }
        }
    }

    /// <summary>Theme of the shell; a change is saved.</summary>
    public AppTheme Theme
    {
        get => theme;
        set
        {
            if (SetProperty(ref theme, value))
            {
                SaveSettings();
            }
        }
    }

    /// <summary>Width of the navigation, limited to the range of <see cref="UiSettings"/>; a change is saved.</summary>
    public double NavigationWidth
    {
        get => navigationWidth;
        set
        {
            if (SetProperty(ref navigationWidth, Math.Clamp(value, UiSettings.MinimumSize, UiSettings.MaximumSize)))
            {
                SaveSettings();
            }
        }
    }

    /// <summary>Width of the list side by side, limited to the range of <see cref="UiSettings"/>; a change is saved.</summary>
    public double ListWidth
    {
        get => listWidth;
        set
        {
            if (SetProperty(ref listWidth, Math.Clamp(value, UiSettings.MinimumSize, UiSettings.MaximumSize)))
            {
                SaveSettings();
            }
        }
    }

    /// <summary>Height of the list above the pane, limited to the range of <see cref="UiSettings"/>; a change is saved.</summary>
    public double ListHeight
    {
        get => listHeight;
        set
        {
            if (SetProperty(ref listHeight, Math.Clamp(value, UiSettings.MinimumSize, UiSettings.MaximumSize)))
            {
                SaveSettings();
            }
        }
    }

    /// <summary>Fixed German text about current problems; null when nothing is to report.</summary>
    public string? Banner => banner.Text;

    /// <summary>Switches between the two layouts.</summary>
    public ICommand ToggleLayoutCommand => toggleLayoutCommand;

    /// <summary>Fetches mail now: per account a retry after an error or sign-in need, otherwise a trigger.</summary>
    public ICommand RefreshCommand => refreshCommand;

    /// <summary>Marks the selected rows as read.</summary>
    public ICommand MarkReadCommand => markReadCommand;

    /// <summary>Marks the selected rows as unread.</summary>
    public ICommand MarkUnreadCommand => markUnreadCommand;

    /// <summary>Sets the flag on all selected rows, or removes it when all of them have it.</summary>
    public ICommand ToggleFlagCommand => toggleFlagCommand;

    /// <summary>Del: moves the selected rows to the Trash; asks first when a mail already lies in the Trash, and deletes that one permanently.</summary>
    public ICommand DeleteCommand => deleteCommand;

    /// <summary>Shift+Del: always asks, then deletes the selected rows permanently.</summary>
    public ICommand DeletePermanentlyCommand => deletePermanentlyCommand;

    /// <summary>"Ordner leeren": only for a Trash or Junk folder; asks with the number of mails.</summary>
    public ICommand EmptyFolderCommand => emptyFolderCommand;

    /// <summary>Moves the selected rows; the parameter is the id of the target folder (long).</summary>
    public ICommand MoveCommand => moveCommand;

    /// <summary>Discards a failed command; the parameter is its <see cref="PendingActionRow"/>. Its rows come back.</summary>
    public ICommand DiscardFailedActionCommand => discardFailedActionCommand;

    /// <summary>Raised on the caller's context before something that cannot be undone; without a subscriber nothing happens.</summary>
    public event EventHandler<ConfirmationRequest>? ConfirmationRequested;

    /// <summary>
    /// Subscribes to the status of the service, builds the navigation and loads the default view (group Inbox).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when navigation and message list are loaded.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (isDisposed)
        {
            return;
        }

        if (!isSubscribed)
        {
            controller.StatusChanged += OnStatusChanged;
            controller.ClassificationPassCompleted += OnClassificationPassCompleted;
            isSubscribed = true;
        }

        // First, so that the first list already leaves out what is pending from the last session.
        await pendingMail.RefreshAsync(ct);
        await Navigation.RefreshAsync(ct);
        await pendingSelectionLoad;
    }

    /// <summary>Stores the size and position of the main window; values out of range are limited to the range.</summary>
    /// <param name="bounds">The current bounds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bounds"/> is null.</exception>
    public void SaveWindowBounds(WindowBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        WindowBounds limited = new(
            LimitWindowSize(bounds.Width),
            LimitWindowSize(bounds.Height),
            LimitWindowPosition(bounds.X),
            LimitWindowPosition(bounds.Y),
            bounds.IsMaximized);
        if (limited != windowBounds)
        {
            windowBounds = limited;
            SaveSettings();
        }
    }

    /// <summary>
    /// Stops listening to the service, cancels running loads and ends the reload loop: after the read that is running now
    /// there is no further read and no log line. Queued status changes are ignored from now on, an open question counts as
    /// no, and no action stores anything from now on.
    /// </summary>
    public void Dispose()
    {
        isDisposed = true;
        if (isSubscribed)
        {
            controller.StatusChanged -= OnStatusChanged;
            controller.ClassificationPassCompleted -= OnClassificationPassCompleted;
            isSubscribed = false;
        }

        CancelReadOnSelectWait();
        openConfirmation?.TrySetResult(false);
        pendingMail.Close();
        Search.Close();
        Navigation.Close();
        Messages.Close();
        Reading.Close();
    }

    private static double? LimitWindowSize(double? size)
    {
        return size is double value ? Math.Clamp(value, UiSettings.MinimumWindowSize, UiSettings.MaximumWindowSize) : null;
    }

    private static int? LimitWindowPosition(int? position)
    {
        return position is int value ? Math.Clamp(value, -UiSettings.MaximumWindowPosition, UiSettings.MaximumWindowPosition) : null;
    }

    private static List<MailMessage> MessagesOf(IEnumerable<MessageRowViewModel> rows)
    {
        return rows.Select(row => row.Message).ToList();
    }

    private static bool IsMoveTarget(MailFolder folder)
    {
        return folder.Role is not (FolderRole.All or FolderRole.Outbox);
    }

    private UiSettings LoadSettings()
    {
        try
        {
            return settings.Load();
        }
        catch (UiSettingsException)
        {
            banner.Set(ShellBanner.BannerSource.SettingsLoad, UiText.BannerSettingsLoadFailed);
            return UiSettings.Default;
        }
    }

    private void ApplySettings(UiSettings loaded)
    {
        layout = loaded.Layout;
        theme = loaded.Theme;
        navigationWidth = loaded.NavigationWidth;
        listWidth = loaded.ListWidth;
        listHeight = loaded.ListHeight;
        markReadOnSelect = loaded.MarkReadOnSelect;
        windowBounds = new WindowBounds(loaded.WindowWidth, loaded.WindowHeight, loaded.WindowX, loaded.WindowY, loaded.WindowMaximized);
    }

    // A successful save also replaced a file that could not be loaded before.
    private void SaveSettings()
    {
        try
        {
            settings.Save(new UiSettings(
                layout,
                theme,
                navigationWidth,
                listWidth,
                listHeight,
                markReadOnSelect,
                windowBounds.Width,
                windowBounds.Height,
                windowBounds.X,
                windowBounds.Y,
                windowBounds.IsMaximized));
            banner.Clear(ShellBanner.BannerSource.SettingsSave);
            banner.Clear(ShellBanner.BannerSource.SettingsLoad);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            banner.Set(ShellBanner.BannerSource.SettingsSave, UiText.BannerSettingsSaveFailed);
            log.Write(CriticalLogLevel.Critical, "ShellViewModel.SaveSettings", $"Saving the settings failed: {exception.GetType().Name}");
        }
    }

    private void OnBannerChanged(object? sender, EventArgs eventArgs) => OnPropertyChanged(nameof(Banner));

    private void ToggleLayout()
    {
        Layout = Layout == ReadingPaneLayout.SideBySide ? ReadingPaneLayout.ListAbove : ReadingPaneLayout.SideBySide;
    }

    private void Refresh()
    {
        Dictionary<Guid, AccountSyncStatus> statuses = Navigation.ReadStatusByAccount();
        foreach (Guid accountId in Navigation.AccountIds)
        {
            AccountSyncState? state = statuses.GetValueOrDefault(accountId)?.State;
            if (state is AccountSyncState.NeedsSignIn or AccountSyncState.WaitingAfterError)
            {
                controller.RetryAccount(accountId);
            }
            else
            {
                controller.TriggerAccount(accountId);
            }
        }
    }

    // A selection change loads the list, unless a refreshed tree only replaced the item object of the same view.
    private void OnNavigationPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(NavigationViewModel.SelectedItem)
            && Navigation.SelectedItem?.View is MailView view
            && view != Messages.CurrentView)
        {
            Search.Reset();
            pendingSelectionLoad = RunObservedAsync(() => Messages.LoadAsync(view), "ShellViewModel.OnNavigationPropertyChanged");
            _ = RunObservedAsync(UpdateEmptyTargetAsync, "ShellViewModel.UpdateEmptyTargetAsync");
        }
    }

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(SearchViewModel.IsActive))
        {
            _ = RunObservedAsync(UpdateEmptyTargetAsync, "ShellViewModel.UpdateEmptyTargetAsync");
        }
    }

    // A selected row shows its message. The same message after a reload stays as it is: the document and the image
    // approval must survive, unless the mail has been downloaded in the meantime.
    private void OnMessagesPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MessageListViewModel.SelectedRows))
        {
            OnSelectionChanged();
            return;
        }

        if (eventArgs.PropertyName != nameof(MessageListViewModel.SelectedRow))
        {
            return;
        }

        UpdateReadOnSelectWait();
        MessageRowViewModel? row = Messages.SelectedRow;
        bool isAlreadyShown = row is not null
            && Reading.CurrentMessage?.Id == row.Message.Id
            && Reading.CurrentMessage.EmlPath == row.Message.EmlPath;
        if (!isAlreadyShown)
        {
            _ = RunObservedAsync(() => Reading.ShowAsync(row?.Message), "ShellViewModel.OnMessagesPropertyChanged");
        }
    }

    // Raised by the service on a thread-pool thread; an exception here would be swallowed by the service, so it is logged.
    private void OnStatusChanged(object? sender, AccountSyncStatus status)
    {
        try
        {
            dispatcher.Post(() => ApplyStatus(status));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteHandlerFailure(exception);
        }
    }

    // On the UI thread. The service raises Idle without progress only when a cycle ended successfully, so new data can
    // be there. A change to an error state also reloads, because an interrupted pass has stored results as well; the same
    // error state reported again does not. Progress updates never reload.
    private void ApplyStatus(AccountSyncStatus status)
    {
        try
        {
            if (!isSubscribed || !Navigation.ApplyStatus(status))
            {
                return;
            }

            bool isStateChange = !lastStates.TryGetValue(status.AccountId, out AccountSyncState lastState)
                || lastState != status.State;
            lastStates[status.AccountId] = status.State;
            bool isFailureChange = isStateChange
                && status.State is AccountSyncState.WaitingAfterError or AccountSyncState.NeedsSignIn;
            if (status.Progress is null && (status.State == AccountSyncState.Idle || isFailureChange))
            {
                RequestReload();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteHandlerFailure(exception);
        }
    }

    private void WriteHandlerFailure(Exception exception)
    {
        log.Write(CriticalLogLevel.Critical, "ShellViewModel.OnStatusChanged", $"Handling a status change failed: {exception.GetType().Name}");
    }

    // Raised on the thread of the account's cycle; same rules as OnStatusChanged.
    private void OnClassificationPassCompleted(object? sender, ClassificationPassSummary summary)
    {
        try
        {
            dispatcher.Post(() => ApplyClassificationSummary(summary));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteSummaryHandlerFailure(exception);
        }
    }

    // On the UI thread. An account the navigation does not show is ignored, like its status.
    private void ApplyClassificationSummary(ClassificationPassSummary summary)
    {
        try
        {
            if (!isSubscribed || !Navigation.AccountIds.Contains(summary.AccountId))
            {
                return;
            }

            ClassificationTally tally = classificationTallies.GetValueOrDefault(summary.AccountId);
            classificationTallies[summary.AccountId] = new ClassificationTally(
                tally.EvaluationFailed + summary.EvaluationFailed,
                tally.BrakeHeld + summary.BrakeHeld,
                summary.ActSuspendedNoContacts);
            UpdateClassificationBanner();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteSummaryHandlerFailure(exception);
        }
    }

    // A clean pass clears nothing: these mails stay unsorted, so the sums must not disappear unseen. They end with the app;
    // acknowledging them comes with AP-08. An account that has left the navigation no longer counts.
    private void UpdateClassificationBanner()
    {
        List<ClassificationTally> tallies =
        [
            .. classificationTallies
                .Where(accountTally => Navigation.AccountIds.Contains(accountTally.Key))
                .Select(accountTally => accountTally.Value),
        ];
        int evaluationFailed = tallies.Sum(tally => tally.EvaluationFailed);
        int brakeHeld = tallies.Sum(tally => tally.BrakeHeld);

        if (evaluationFailed > 0 || brakeHeld > 0)
        {
            banner.Set(ShellBanner.BannerSource.Classification, UiText.BannerClassificationProblems(evaluationFailed, brakeHeld));
        }
        else
        {
            banner.Clear(ShellBanner.BannerSource.Classification);
        }

        // Only true in Act: while the mode is RecordOnly this never shows.
        if (tallies.Any(tally => tally.IsWaitingForContacts))
        {
            banner.Set(ShellBanner.BannerSource.ClassificationWaiting, UiText.BannerClassificationWaiting);
        }
        else
        {
            banner.Clear(ShellBanner.BannerSource.ClassificationWaiting);
        }
    }

    private void WriteSummaryHandlerFailure(Exception exception)
    {
        log.Write(CriticalLogLevel.Critical, "ShellViewModel.OnClassificationPassCompleted", $"Handling a classification summary failed: {exception.GetType().Name}");
    }

    private void RequestReload()
    {
        lock (reloadGate)
        {
            if (isReloadRunning)
            {
                isReloadPending = true;
                return;
            }

            isReloadRunning = true;
        }

        _ = RunObservedAsync(RunReloadLoopAsync, "ShellViewModel.RunReloadLoopAsync");
    }

    private async Task RunReloadLoopAsync()
    {
        try
        {
            while (true)
            {
                lock (reloadGate)
                {
                    isReloadPending = false;
                }

                await Navigation.RefreshAsync();
                await Messages.ReloadAsync();
                await RunObservedAsync(UpdateEmptyTargetAsync, "ShellViewModel.UpdateEmptyTargetAsync");
                lock (reloadGate)
                {
                    if (!isReloadPending)
                    {
                        isReloadRunning = false;
                        return;
                    }
                }
            }
        }
        catch
        {
            lock (reloadGate)
            {
                isReloadRunning = false;
            }

            throw;
        }
    }

    private bool HasSelection() => Messages.SelectedRows.Count > 0;

    private Guid? SingleAccountOfSelection()
    {
        IReadOnlyList<MessageRowViewModel> rows = Messages.SelectedRows;
        if (rows.Count == 0)
        {
            return null;
        }

        Guid accountId = rows[0].Message.AccountId;
        return rows.All(row => row.Message.AccountId == accountId) ? accountId : null;
    }

    private void OnSelectionChanged()
    {
        markReadCommand.RaiseCanExecuteChanged();
        markUnreadCommand.RaiseCanExecuteChanged();
        toggleFlagCommand.RaiseCanExecuteChanged();
        deleteCommand.RaiseCanExecuteChanged();
        deletePermanentlyCommand.RaiseCanExecuteChanged();
        moveCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanMove));
        _ = RunObservedAsync(UpdateMoveTargetsAsync, "ShellViewModel.UpdateMoveTargetsAsync");
    }

    // The menu reads this list when it opens, so it may arrive a moment after the selection.
    private async Task UpdateMoveTargetsAsync()
    {
        int generation = ++moveTargetGeneration;
        List<NavigationItem> targets = [];
        if (SingleAccountOfSelection() is Guid accountId)
        {
            List<long> sourceFolderIds = Messages.SelectedRows.Select(row => row.Message.FolderId).Distinct().ToList();
            IReadOnlyList<MailFolder> folders = await readModel.ListFoldersAsync(accountId);
            if (generation != moveTargetGeneration || isDisposed)
            {
                return;
            }

            targets = folders
                .Where(IsMoveTarget)
                .Where(folder => sourceFolderIds.Count != 1 || folder.Id != sourceFolderIds[0])
                .Select(folder => new NavigationItem(
                    SafeDisplayText.Shorten(folder.FullName, MaxMoveTargetCharacters),
                    MailView.Folder(accountId, folder.Id),
                    accountId,
                    []))
                .ToList();
        }

        moveTargets = targets;
        OnPropertyChanged(nameof(MoveTargets));
    }

    // Exactly one folder with role Trash or Junk behind the view, or none: this is what the button offers and the command empties.
    private async Task<MailFolder?> FindEmptyTargetAsync()
    {
        if (Messages.IsShowingSearch || Messages.CurrentView is not MailView view)
        {
            return null;
        }

        if (view.FolderId is null && view.Role is not (FolderRole.Trash or FolderRole.Junk))
        {
            return null;
        }

        IEnumerable<Guid> accountIds = view.AccountId is Guid accountId
            ? [accountId]
            : (await readModel.ListAccountsAsync()).Select(account => account.Id);
        var candidates = new List<MailFolder>();
        foreach (Guid candidateAccountId in accountIds.ToList())
        {
            IReadOnlyList<MailFolder> folders = await readModel.ListFoldersAsync(candidateAccountId);
            candidates.AddRange(folders.Where(folder => view.FolderId is long folderId ? folder.Id == folderId : folder.Role == view.Role));
        }

        return candidates is [{ Role: FolderRole.Trash or FolderRole.Junk } target] ? target : null;
    }

    private async Task UpdateEmptyTargetAsync()
    {
        int generation = ++emptyTargetGeneration;
        MailFolder? target = await FindEmptyTargetAsync();
        if (generation != emptyTargetGeneration || isDisposed || target == emptyTarget)
        {
            return;
        }

        emptyTarget = target;
        OnPropertyChanged(nameof(CanEmptyFolder));
        emptyFolderCommand.RaiseCanExecuteChanged();
    }

    // On the context of the refresh. Failed commands bring their rows back with the next list read; the banner names their count.
    private void OnPendingMailRefreshed(object? sender, EventArgs eventArgs)
    {
        IReadOnlyList<PendingAction> listedActions = pendingMail.LatestActions;
        List<PendingActionRow> failedRows = listedActions
            .Where(action => action.Status == MailCommandStatus.Failed)
            .Select(action => new PendingActionRow(action.CommandId, UiText.FailedAction(action.Kind)))
            .ToList();
        if (!failedRows.SequenceEqual(failedActions))
        {
            failedActions = failedRows;
            OnPropertyChanged(nameof(FailedActions));
            OnPropertyChanged(nameof(HasFailedActions));
        }

        if (failedRows.Count > 0)
        {
            banner.Set(ShellBanner.BannerSource.Actions, UiText.BannerFailedActions(failedRows.Count));
        }
        else
        {
            banner.Clear(ShellBanner.BannerSource.Actions);
        }

        if (permanentDeleteWatcher.IsWatching)
        {
            _ = RunObservedAsync(() => permanentDeleteWatcher.CheckAsync(listedActions), "ShellViewModel.OnPendingMailRefreshed");
        }
    }

    // An action the user started replaces what the banner said about the last one.
    private void StartUserAction(Func<Task> work)
    {
        banner.Clear(ShellBanner.BannerSource.ActionRejected);
        banner.Clear(ShellBanner.BannerSource.DeleteReachedTrash);
        StartAction(work);
    }

    // One after the other: a second Del during the first works on the row the first has selected. Up to the first real wait
    // this runs synchronously, so a row changes at once.
    private void StartAction(Func<Task> work)
    {
        if (!isDisposed)
        {
            actionTail = RunActionAsync(actionTail, work);
        }
    }

    // Nobody can await a command, so nothing may escape from here. After Dispose a failure is the expected end of the work.
    private async Task RunActionAsync(Task previousAction, Func<Task> work)
    {
        await previousAction;
        if (isDisposed)
        {
            return;
        }

        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // Only the answer of a question can be cancelled, and that means no.
        }
        catch (MailActionException exception)
        {
            if (!isDisposed)
            {
                banner.Set(ShellBanner.BannerSource.ActionRejected, exception.Message);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!isDisposed)
            {
                log.Write(CriticalLogLevel.Critical, "ShellViewModel.RunActionAsync", $"An action failed: {exception.GetType().Name}");
                banner.Set(ShellBanner.BannerSource.ActionRejected, UiText.BannerActionFailed);
            }
        }
    }

    // No subscriber, no answer or no means nothing is enqueued.
    private async Task<bool> ConfirmAsync(string text)
    {
        EventHandler<ConfirmationRequest>? handlers = ConfirmationRequested;
        if (handlers is null || isDisposed)
        {
            return false;
        }

        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        openConfirmation = answer;
        try
        {
            handlers(this, new ConfirmationRequest(text, answer));
            bool isConfirmed = await answer.Task;
            return isConfirmed && !isDisposed;
        }
        finally
        {
            openConfirmation = null;
        }
    }

    private Task ChangeSeenAsync(IReadOnlyList<MessageRowViewModel> rows, bool seen)
    {
        return ChangeFlagAsync(rows, MessageFlags.Seen, seen, messages => actions.SetSeenAsync(messages, seen));
    }

    private Task ToggleFlagAsync()
    {
        IReadOnlyList<MessageRowViewModel> rows = Messages.SelectedRows;
        bool flagged = rows.Any(row => !row.IsFlagged);
        return ChangeFlagAsync(rows, MessageFlags.Flagged, flagged, messages => actions.SetFlaggedAsync(messages, flagged));
    }

    // The rows show the wanted state at once. The stored state counts again when the command has run or nothing could be stored.
    private async Task ChangeFlagAsync(
        IReadOnlyList<MessageRowViewModel> rows,
        MessageFlags flag,
        bool isSet,
        Func<IReadOnlyCollection<MailMessage>, Task> enqueue)
    {
        if (rows.Count == 0)
        {
            return;
        }

        List<MailMessage> messages = MessagesOf(rows);
        pendingMail.BeginFlagChange(messages.Select(message => message.Id), flag, isSet);
        foreach (MessageRowViewModel row in rows)
        {
            row.ShowFlags(pendingMail.ShownFlags(row.Message));
        }

        await EnqueueAsync(() => enqueue(messages));
    }

    // Rows go at once and the next row takes over; nothing below this reports the action as done before it is stored.
    private async Task RemoveRowsAsync(IReadOnlyCollection<MessageRowViewModel> rows, Func<Task> enqueue)
    {
        HashSet<long> messageIds = rows.Select(row => row.Message.Id).ToHashSet();
        pendingMail.BeginRemoval(messageIds);
        Messages.RemoveRows(messageIds);
        await EnqueueAsync(enqueue);
    }

    // After a refusal the queue decides what stays hidden: what was stored before it stays, everything else comes back.
    private async Task EnqueueAsync(Func<Task> enqueue)
    {
        try
        {
            await enqueue();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            pendingMail.EndAction(isStored: false);
            await Messages.ReloadAsync();
            throw;
        }

        pendingMail.EndAction(isStored: true);
    }

    private async Task<Dictionary<long, FolderRole?>> ReadFolderRolesAsync(IEnumerable<MessageRowViewModel> rows)
    {
        var roles = new Dictionary<long, FolderRole?>();
        foreach (long folderId in rows.Select(row => row.Message.FolderId).Distinct().ToList())
        {
            roles[folderId] = await readModel.GetFolderRoleAsync(folderId);
        }

        return roles;
    }

    private async Task DeleteAsync()
    {
        List<MessageRowViewModel> rows = [.. Messages.SelectedRows];
        if (rows.Count == 0)
        {
            return;
        }

        Dictionary<long, FolderRole?> roles = await ReadFolderRolesAsync(rows);
        List<MessageRowViewModel> trashRows = rows.Where(row => roles[row.Message.FolderId] == FolderRole.Trash).ToList();
        List<MessageRowViewModel> otherRows = rows.Except(trashRows).ToList();
        if (trashRows.Count > 0 && !await ConfirmAsync(UiText.ConfirmDeleteFromTrash(trashRows.Count, otherRows.Count)))
        {
            return;
        }

        await RemoveRowsAsync(
            rows,
            async () =>
            {
                if (otherRows.Count > 0)
                {
                    await actions.MoveToTrashAsync(MessagesOf(otherRows));
                }

                if (trashRows.Count > 0)
                {
                    await actions.DeletePermanentlyAsync(MessagesOf(trashRows));
                }
            });
    }

    private async Task DeletePermanentlyAsync()
    {
        List<MessageRowViewModel> rows = [.. Messages.SelectedRows];
        if (rows.Count == 0 || !await ConfirmAsync(UiText.ConfirmDeletePermanently(rows.Count)))
        {
            return;
        }

        Dictionary<long, FolderRole?> roles = await ReadFolderRolesAsync(rows);
        List<MailMessage> messages = MessagesOf(rows);
        await RemoveRowsAsync(rows, () => actions.DeletePermanentlyAsync(messages));

        // Only outside Trash and Junk does the server need to name the new UIDs (Q1 of AP-03c).
        permanentDeleteWatcher.Add(messages.Where(message => roles[message.FolderId] is not (FolderRole.Trash or FolderRole.Junk)));
    }

    // The count of the question must stay true: when new mail arrived during the question, the user is asked again. The queue
    // takes its snapshot in the next step, so only mail arriving in that moment can still be part of it.
    private async Task EmptyFolderAsync()
    {
        if (await FindEmptyTargetAsync() is not MailFolder folder)
        {
            return;
        }

        int count = await actions.CountForEmptyAsync(folder.Id);
        bool isConfirmedCount = false;
        for (int question = 0; question < MaxEmptyConfirmations && !isConfirmedCount; question++)
        {
            if (count == 0 || !await ConfirmAsync(UiText.ConfirmEmptyFolder(folder.Role, count)))
            {
                return;
            }

            int countNow = await actions.CountForEmptyAsync(folder.Id);
            isConfirmedCount = countNow <= count;
            count = countNow;
        }

        if (!isConfirmedCount)
        {
            return;
        }

        List<MessageRowViewModel> rows = Messages.Items.Where(row => row.Message.FolderId == folder.Id).ToList();
        await RemoveRowsAsync(rows, () => actions.EmptyFolderAsync(folder.Id));

        // Rows beyond the loaded pages only stay out after a reload.
        if (Messages.HasMore)
        {
            await Messages.ReloadAsync();
        }
    }

    private void ExecuteMove(object? parameter)
    {
        if (parameter is long targetFolderId)
        {
            StartUserAction(() => MoveAsync(targetFolderId));
        }
    }

    // Refused here already when the target is no folder of this account; the queue would refuse it as well.
    private async Task MoveAsync(long targetFolderId)
    {
        if (SingleAccountOfSelection() is not Guid accountId)
        {
            return;
        }

        List<MessageRowViewModel> rows = [.. Messages.SelectedRows];
        IReadOnlyList<MailFolder> folders = await readModel.ListFoldersAsync(accountId);
        if (!folders.Any(folder => folder.Id == targetFolderId && IsMoveTarget(folder)))
        {
            throw new MailActionException(UiText.BannerMoveTargetRefused);
        }

        List<MessageRowViewModel> rowsToMove = rows.Where(row => row.Message.FolderId != targetFolderId).ToList();
        if (rowsToMove.Count > 0)
        {
            List<MailMessage> messages = MessagesOf(rowsToMove);
            await RemoveRowsAsync(rowsToMove, () => actions.MoveAsync(messages, targetFolderId));
        }
    }

    private void ExecuteDiscardFailedAction(object? parameter)
    {
        if (parameter is PendingActionRow row)
        {
            StartUserAction(() => DiscardFailedActionAsync(row));
        }
    }

    // Only what is listed as failed here: a pending command cannot be discarded through this list.
    private async Task DiscardFailedActionAsync(PendingActionRow row)
    {
        if (!failedActions.Any(failedRow => failedRow.CommandId == row.CommandId))
        {
            return;
        }

        PendingAction? action = pendingMail.LatestActions.FirstOrDefault(listedAction => listedAction.CommandId == row.CommandId);
        await actions.DiscardAsync(row.CommandId);
        if (action is not null)
        {
            permanentDeleteWatcher.Forget(action.MessageIds);
        }

        await pendingMail.RefreshAsync();
        await Messages.ReloadAsync();
    }

    // On the context of the selection. The same message again, after a reload for instance, is left alone: a mail the user has
    // just marked as unread must not become read by itself.
    private void UpdateReadOnSelectWait()
    {
        long? messageId = Messages.SelectedRow?.Message.Id;
        if (messageId == readOnSelectMessageId && readOnSelectMessageId is not null)
        {
            return;
        }

        CancelReadOnSelectWait();
        readOnSelectMessageId = messageId;
        if (markReadOnSelect && !isDisposed && Messages.SelectedRow is { IsUnread: true } row)
        {
            var cancellation = new CancellationTokenSource();
            readOnSelectCancellation = cancellation;
            _ = MarkReadAfterWaitAsync(row.Message.Id, cancellation);
        }
    }

    private void CancelReadOnSelectWait()
    {
        readOnSelectCancellation?.Cancel();
        readOnSelectCancellation?.Dispose();
        readOnSelectCancellation = null;
    }

    private async Task MarkReadAfterWaitAsync(long messageId, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(ReadOnSelectDelay, timeProvider, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (markReadOnSelect && Messages.SelectedRow is { IsUnread: true } row && row.Message.Id == messageId)
        {
            StartAction(() => ChangeSeenAsync([row], true));
        }
    }

    // For work that nobody can await: a failure is logged with its type name, it never ends as an unobserved exception.
    private async Task RunObservedAsync(Func<Task> work, string source)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // Nothing to report: only a caller's token cancels, and these calls have none.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // After Dispose a failure is the expected end of the work: the connections are closing.
            if (!isDisposed)
            {
                log.Write(CriticalLogLevel.Critical, source, $"Background work of the shell failed: {exception.GetType().Name}");
            }
        }
    }

    // Counts added since the app started; the waiting flag follows the account's last summary.
    private readonly record struct ClassificationTally(int EvaluationFailed, int BrakeHeld, bool IsWaitingForContacts);
}
