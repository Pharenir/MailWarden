using System.Runtime.InteropServices;

namespace MailWarden.App;

/// <summary>
/// The Windows calls for monitor scale and mouse position; the Windows App SDK has no API for them.
/// </summary>
internal static class NativeDisplay
{
    private const uint DefaultToNearestMonitor = 2;
    private const int EffectiveDotsPerInch = 0;
    private const uint FullScaleDotsPerInch = 96;

    /// <summary>The scale of the monitor a window is on.</summary>
    /// <param name="window">Window handle.</param>
    /// <returns>Dots per inch; 96 when Windows reports none.</returns>
    public static uint DotsPerInchOfWindow(IntPtr window)
    {
        uint dotsPerInch = GetDpiForWindow(window);
        return dotsPerInch == 0 ? FullScaleDotsPerInch : dotsPerInch;
    }

    /// <summary>The scale of the monitor at or nearest to a screen position.</summary>
    /// <param name="x">Horizontal screen position.</param>
    /// <param name="y">Vertical screen position.</param>
    /// <returns>Dots per inch; 96 when Windows reports none.</returns>
    public static uint DotsPerInchAt(int x, int y)
    {
        IntPtr monitor = MonitorFromPoint(new NativePoint(x, y), DefaultToNearestMonitor);
        int result = GetDpiForMonitor(monitor, EffectiveDotsPerInch, out uint dotsPerInchX, out _);
        return result == 0 && dotsPerInchX != 0 ? dotsPerInchX : FullScaleDotsPerInch;
    }

    /// <summary>The mouse position in client pixels of a window.</summary>
    /// <param name="window">Window handle.</param>
    /// <param name="x">Horizontal position.</param>
    /// <param name="y">Vertical position.</param>
    /// <returns>False when Windows reports no position.</returns>
    public static bool TryGetCursorInClient(IntPtr window, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!GetCursorPos(out NativePoint position) || !ScreenToClient(window, ref position))
        {
            return false;
        }

        x = position.X;
        y = position.Y;
        return true;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dotsPerInchX, out uint dotsPerInchY);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    /// <summary>The POINT structure of Windows.</summary>
    /// <param name="X">Horizontal position.</param>
    /// <param name="Y">Vertical position.</param>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);
}
