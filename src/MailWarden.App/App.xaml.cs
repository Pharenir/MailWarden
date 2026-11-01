using MailWarden.Core.Storage;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;
using MailWarden.Infrastructure.Storage;
using MailWarden.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MailWarden.App;

/// <summary>
/// Application entry point and composition of the user interface: resolves the data folder, opens the runtime, creates
/// the shell, starts the service and shows the main window. A failure on this way shows an error window instead of ending
/// silently. Closing the main window disposes the runtime.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Environment variable that overrides the data directory (fully qualified path). Unset, empty or whitespace-only
    /// counts as unset and selects <c>&lt;exe dir&gt;\data</c>.
    /// </summary>
    public const string DataDirectoryVariable = "MAILWARDEN_DATA_DIR";

    private const string DefaultDataDirectoryName = "data";

    // Stopping waits for running cycles; the operating system releases everything else when the process ends.
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    private Window? openWindow;
    private ICriticalLog? criticalLog;
    private MailWardenRuntime? runtime;
    private ShellViewModel? shell;
    private bool isShuttingDown;

    /// <summary>Creates the application and loads App.xaml.</summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>Starts the application.</summary>
    /// <param name="args">Launch details (unused).</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // async void is the only form of this override, so nothing may escape from here.
        try
        {
            await StartAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Closing the window during the start disposes the runtime under it; the app is ending anyway.
            if (isShuttingDown)
            {
                return;
            }

            WriteLog("App.OnLaunched", $"Start failed: {exception.GetType().Name}");

            // The error window opens first: closing the last window would end the application.
            Window? windowOfFailedStart = openWindow;
            ShowErrorWindow(DescribeStartFailure(exception));
            await ReleaseAfterFailedStartAsync(windowOfFailedStart);
        }
    }

    private static DataPaths ResolveDataPaths()
    {
        string? overrideDirectory = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        string rootDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(AppContext.BaseDirectory, DefaultDataDirectoryName)
            : overrideDirectory;
        return new DataPaths(rootDirectory);
    }

    private static string DescribeStartFailure(Exception exception) => exception switch
    {
        DataFolderInUseException => UiText.StartErrorInUse,
        InvalidDataException { Data: var details } when details[MailWardenRuntime.InvalidFileNameDataKey] is string fileName
            => UiText.StartErrorInvalidClassifierFile(fileName),
        SchemaVersionException => UiText.StartErrorSchemaTooNew,
        ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException => UiText.StartErrorInvalidDataFolder,
        _ => UiText.StartErrorOther,
    };

    private async Task StartAsync()
    {
        DataPaths paths = ResolveDataPaths();
        criticalLog = new CriticalFileLog(paths.LogRoot, TimeProvider.System);

        runtime = await MailWardenRuntime.OpenAsync(paths, log: criticalLog);
        var dispatcher = new DispatcherQueueUiDispatcher(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        shell = new ShellViewModel(
            runtime.ReadModel,
            new MailboxServiceController(runtime),
            runtime.Actions,
            dispatcher,
            new UiSettingsFile(Path.Combine(paths.Root, UiSettingsFile.FileName)),
            criticalLog,
            TimeProvider.System);

        var mainWindow = new MainWindow(shell, paths, criticalLog);
        mainWindow.Closed += OnMainWindowClosed;
        openWindow = mainWindow;
        mainWindow.ApplyStoredPlacement();
        mainWindow.Activate();

        await shell.InitializeAsync();
        if (!isShuttingDown)
        {
            await runtime.Service.StartAsync();
        }
    }

    // Keeps the message loop alive until the runtime is disposed; the window is gone for the user at once.
    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (isShuttingDown)
        {
            return;
        }

        isShuttingDown = true;
        args.Handled = true;
        try
        {
            SaveWindowPlacementAndLog();
            openWindow?.AppWindow.Hide();
            shell?.Dispose();
            CloseReadingViewAndLog();
            if (runtime is not null)
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteLog("App.OnMainWindowClosed", $"Shutdown failed: {exception.GetType().Name}");
        }

        Exit();
    }

    // A failure here must not keep the runtime from being disposed.
    private void SaveWindowPlacementAndLog()
    {
        try
        {
            (openWindow as MainWindow)?.SaveWindowPlacement();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteLog("App.SaveWindowPlacementAndLog", $"Saving the window placement failed: {exception.GetType().Name}");
        }
    }

    // A failure of the browser control must not keep the runtime from being disposed.
    private void CloseReadingViewAndLog()
    {
        try
        {
            (openWindow as MainWindow)?.CloseReadingView();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteLog("App.CloseReadingViewAndLog", $"Closing the reading view failed: {exception.GetType().Name}");
        }
    }

    private async Task ReleaseAfterFailedStartAsync(Window? windowOfFailedStart)
    {
        try
        {
            try
            {
                shell?.Dispose();
                if (runtime is not null)
                {
                    await runtime.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout);
                }
            }
            finally
            {
                // An already opened main window must not stay around without a runtime behind it.
                if (windowOfFailedStart is MainWindow mainWindow)
                {
                    isShuttingDown = true;
                    mainWindow.CloseReadingView();
                    mainWindow.Close();
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteLog("App.ReleaseAfterFailedStartAsync", $"Release failed: {exception.GetType().Name}");
        }
    }

    // Fixed text only: exception messages may hold paths or account data. The application ends with this window.
    private void ShowErrorWindow(string text)
    {
        var message = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var errorWindow = new Window { Title = UiText.StartErrorTitle, Content = message };
        errorWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 240));
        openWindow = errorWindow;
        errorWindow.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        WriteLog("App.OnUnhandledException", $"Unhandled exception: {args.Exception.GetType().Name}");
    }

    // Before the data folder is known there is no log folder; the failure then only shows in the error window.
    private void WriteLog(string source, string message)
    {
        criticalLog?.Write(CriticalLogLevel.Critical, source, message);
    }
}
