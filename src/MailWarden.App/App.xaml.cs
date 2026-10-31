using MailWarden.Infrastructure.Storage;
using Microsoft.UI.Xaml;

namespace MailWarden.App;

/// <summary>
/// Application entry point. On launch it resolves the data directory, makes sure it exists and opens the main window.
/// No database access from the UI in AP-01.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Environment variable that overrides the data directory (fully qualified path). Unset, empty or whitespace-only
    /// counts as unset and selects <c>&lt;exe dir&gt;\data</c>.
    /// </summary>
    public const string DataDirectoryVariable = "MAILWARDEN_DATA_DIR";

    private const string DefaultDataDirectoryName = "data";

    private Window? mainWindow;

    /// <summary>Creates the application and loads App.xaml.</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>Prepares the data directory and shows the main window.</summary>
    /// <param name="args">Launch details (unused).</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Data directory: MAILWARDEN_DATA_DIR or <exe dir>\data; invalid values fail fast.
        DataPaths dataPaths = ResolveDataPaths();
        Directory.CreateDirectory(dataPaths.Root);

        mainWindow = new MainWindow();
        mainWindow.Activate();
    }

    private static DataPaths ResolveDataPaths()
    {
        string? overrideDirectory = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        string rootDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(AppContext.BaseDirectory, DefaultDataDirectoryName)
            : overrideDirectory;
        return new DataPaths(rootDirectory);
    }
}
