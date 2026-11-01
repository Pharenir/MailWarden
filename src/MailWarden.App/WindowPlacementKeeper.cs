using MailWarden.Presentation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace MailWarden.App;

/// <summary>
/// Applies the stored size and position of the main window on start and stores them on close and when resizing ends. The
/// Windows App SDK reports no end of a resize, so "ended" means that no change came for a moment. The size is stored
/// device-independent: on a monitor with another scale the window keeps its size.
/// </summary>
internal sealed class WindowPlacementKeeper
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    // The title bar must stay reachable with the mouse: a band of it, without the corners, must be on a monitor.
    private const int TitleBarHeight = 32;
    private const int TitleBarInset = 48;

    private readonly AppWindow appWindow;
    private readonly IntPtr windowHandle;
    private readonly ShellViewModel shell;
    private readonly DispatcherQueueTimer saveTimer;
    private WindowBounds currentBounds;
    private bool isStopped;

    /// <summary>Creates the keeper; nothing happens before <see cref="ApplyStored"/>.</summary>
    /// <param name="window">The main window.</param>
    /// <param name="shell">Holds the stored bounds.</param>
    public WindowPlacementKeeper(Window window, ShellViewModel shell)
    {
        this.shell = shell;
        appWindow = window.AppWindow;
        windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        currentBounds = shell.WindowBounds;
        saveTimer = window.DispatcherQueue.CreateTimer();
        saveTimer.Interval = SaveDelay;
        saveTimer.IsRepeating = false;
        saveTimer.Tick += OnSaveTimerTick;
    }

    /// <summary>Places the window as stored and starts following its changes; called once before the window is shown.</summary>
    public void ApplyStored()
    {
        WindowBounds stored = shell.WindowBounds;
        if (stored.Width is double width && stored.Height is double height && !TryPlaceAtStoredPosition(stored, width, height))
        {
            // Placed by the system, on the monitor it chose.
            uint dotsPerInch = NativeDisplay.DotsPerInchOfWindow(windowHandle);
            appWindow.Resize(new SizeInt32(
                WindowPlacement.ToScreenPixels(width, dotsPerInch),
                WindowPlacement.ToScreenPixels(height, dotsPerInch)));
        }

        if (stored.IsMaximized && appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        appWindow.Changed += OnAppWindowChanged;
    }

    /// <summary>Stores the bounds of the moment and stops following changes; called when the window closes.</summary>
    public void SaveAndStop()
    {
        if (isStopped)
        {
            return;
        }

        isStopped = true;
        saveTimer.Stop();
        appWindow.Changed -= OnAppWindowChanged;
        CaptureBounds();
        shell.SaveWindowBounds(currentBounds);
    }

    private static List<(double X, double Y, double Width, double Height)> ReadMonitorAreas()
    {
        // By index: enumerating this list of the Windows App SDK is known to fail in .NET.
        IReadOnlyList<DisplayArea> displayAreas = DisplayArea.FindAll();
        var monitors = new List<(double X, double Y, double Width, double Height)>(displayAreas.Count);
        for (int index = 0; index < displayAreas.Count; index++)
        {
            RectInt32 workArea = displayAreas[index].WorkArea;
            monitors.Add((workArea.X, workArea.Y, workArea.Width, workArea.Height));
        }

        return monitors;
    }

    private bool TryPlaceAtStoredPosition(WindowBounds stored, double width, double height)
    {
        if (stored.X is not int x || stored.Y is not int y)
        {
            return false;
        }

        uint dotsPerInch = NativeDisplay.DotsPerInchAt(x, y);
        int pixelWidth = WindowPlacement.ToScreenPixels(width, dotsPerInch);
        int pixelHeight = WindowPlacement.ToScreenPixels(height, dotsPerInch);
        bool isTitleBarReachable = WindowPlacement.IsOnAnyMonitor(
            x + TitleBarInset,
            y,
            pixelWidth - (2 * TitleBarInset),
            TitleBarHeight,
            ReadMonitorAreas());
        if (isTitleBarReachable)
        {
            appWindow.MoveAndResize(new RectInt32(x, y, pixelWidth, pixelHeight));
        }

        return isTitleBarReachable;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPositionChange || args.DidPresenterChange)
        {
            CaptureBounds();
            saveTimer.Stop();
            saveTimer.Start();
        }
    }

    private void OnSaveTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!isStopped)
        {
            shell.SaveWindowBounds(currentBounds);
        }
    }

    // A minimised window lies far off screen, and a maximised one has the size of its monitor: only a normal window tells its
    // normal bounds, so that "maximised" comes back with the right size behind it.
    private void CaptureBounds()
    {
        if (appWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized)
        {
            return;
        }

        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            currentBounds = currentBounds with { IsMaximized = true };
            return;
        }

        uint dotsPerInch = NativeDisplay.DotsPerInchOfWindow(windowHandle);
        currentBounds = new WindowBounds(
            WindowPlacement.ToDeviceIndependent(appWindow.Size.Width, dotsPerInch),
            WindowPlacement.ToDeviceIndependent(appWindow.Size.Height, dotsPerInch),
            appWindow.Position.X,
            appWindow.Position.Y,
            IsMaximized: false);
    }
}
