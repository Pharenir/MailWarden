using System.ComponentModel;
using System.Windows.Input;
using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;

namespace MailWarden.Presentation;

/// <summary>
/// Root view model of the main window: connects navigation, message list, reading pane and background service, and keeps
/// the settings. It can only schedule cycles of the service and read data; it never changes anything on a server.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IMailboxController controller;
    private readonly IUiDispatcher dispatcher;
    private readonly IUiSettingsStore settings;
    private readonly ICriticalLog log;
    private readonly ShellBanner banner = new();
    private readonly RelayCommand toggleLayoutCommand;
    private readonly RelayCommand refreshCommand;

    // Coalesces reload requests: several requests while one reload runs cause exactly one more.
    private readonly Lock reloadGate = new();
    private bool isReloadRunning;
    private bool isReloadPending;

    private ReadingPaneLayout layout;
    private AppTheme theme;
    private double navigationWidth;
    private double listSize;
    private Task pendingSelectionLoad = Task.CompletedTask;
    private bool isSubscribed;

    /// <summary>Creates the shell and loads the settings. Nothing else runs before <see cref="InitializeAsync"/>.</summary>
    /// <param name="readModel">Read access to the stored mail.</param>
    /// <param name="controller">The background service.</param>
    /// <param name="dispatcher">Runs status changes on the UI thread.</param>
    /// <param name="settings">Store of the UI settings.</param>
    /// <param name="log">Critical log.</param>
    /// <param name="timeProvider">Clock of the date texts.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public ShellViewModel(
        IMailboxReadModel readModel,
        IMailboxController controller,
        IUiDispatcher dispatcher,
        IUiSettingsStore settings,
        ICriticalLog log,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.log = log ?? throw new ArgumentNullException(nameof(log));

        Navigation = new NavigationViewModel(readModel, controller, log, banner);
        Messages = new MessageListViewModel(readModel, log, banner, timeProvider);
        Reading = new ReadingPaneViewModel(readModel, log, timeProvider);
        toggleLayoutCommand = new RelayCommand(ToggleLayout);
        refreshCommand = new RelayCommand(Refresh);

        banner.Changed += OnBannerChanged;
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

    /// <summary>Size of the message list, limited to the range of <see cref="UiSettings"/>; a change is saved.</summary>
    public double ListSize
    {
        get => listSize;
        set
        {
            if (SetProperty(ref listSize, Math.Clamp(value, UiSettings.MinimumSize, UiSettings.MaximumSize)))
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

    /// <summary>
    /// Subscribes to the status of the service, builds the navigation and loads the default view (group Inbox).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when navigation and message list are loaded.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!isSubscribed)
        {
            controller.StatusChanged += OnStatusChanged;
            isSubscribed = true;
        }

        await Navigation.RefreshAsync(ct);
        await pendingSelectionLoad;
    }

    /// <summary>Stops listening to the service. Queued status changes are ignored from now on.</summary>
    public void Dispose()
    {
        if (isSubscribed)
        {
            controller.StatusChanged -= OnStatusChanged;
            isSubscribed = false;
        }
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
        listSize = loaded.ListSize;
    }

    // A successful save also replaced a file that could not be loaded before.
    private void SaveSettings()
    {
        try
        {
            settings.Save(new UiSettings(layout, theme, navigationWidth, listSize));
            banner.Clear(ShellBanner.BannerSource.SettingsSave);
            banner.Clear(ShellBanner.BannerSource.SettingsLoad);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            banner.Set(ShellBanner.BannerSource.SettingsSave, UiText.BannerSettingsSaveFailed);
            log.Write(LogLevels.Critical, "ShellViewModel.SaveSettings", $"Saving the settings failed: {exception.GetType().Name}");
        }
    }

    private void OnBannerChanged(object? sender, EventArgs eventArgs) => OnPropertyChanged(nameof(Banner));

    private void ToggleLayout()
    {
        Layout = Layout == ReadingPaneLayout.SideBySide ? ReadingPaneLayout.ListAbove : ReadingPaneLayout.SideBySide;
    }

    private void Refresh()
    {
        Dictionary<Guid, AccountSyncStatus> statuses = controller.CurrentStatus
            .GroupBy(status => status.AccountId)
            .ToDictionary(group => group.Key, group => group.Last());
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
            pendingSelectionLoad = RunObservedAsync(() => Messages.LoadAsync(view), "ShellViewModel.OnNavigationPropertyChanged");
        }
    }

    // A selected row shows its message. The same message after a reload stays as it is: the document and the image
    // approval must survive, unless the mail has been downloaded in the meantime.
    private void OnMessagesPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(MessageListViewModel.SelectedRow))
        {
            return;
        }

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
    // be there; progress updates never reload.
    private void ApplyStatus(AccountSyncStatus status)
    {
        try
        {
            if (!isSubscribed || !Navigation.ApplyStatus(status))
            {
                return;
            }

            if (status.Progress is null && status.State == AccountSyncState.Idle)
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
        log.Write(LogLevels.Critical, "ShellViewModel.OnStatusChanged", $"Handling a status change failed: {exception.GetType().Name}");
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
            log.Write(LogLevels.Critical, source, $"Background work of the shell failed: {exception.GetType().Name}");
        }
    }
}
