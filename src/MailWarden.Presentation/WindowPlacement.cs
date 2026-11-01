namespace MailWarden.Presentation;

/// <summary>
/// Stored size and position of the main window. The size is device-independent, so that a monitor with another scale keeps
/// the size; the position stays in screen pixels, because that is the coordinate system of the monitors.
/// </summary>
/// <param name="Width">Width in device-independent pixels; null when not stored.</param>
/// <param name="Height">Height in device-independent pixels; null when not stored.</param>
/// <param name="X">Left edge in screen pixels; null when not stored.</param>
/// <param name="Y">Top edge in screen pixels; null when not stored.</param>
/// <param name="IsMaximized">True when the window was maximised; the other values then describe its normal size.</param>
public sealed record WindowBounds(double? Width, double? Height, int? X, int? Y, bool IsMaximized);

/// <summary>
/// The pure rules of placing the main window on start.
/// </summary>
public static class WindowPlacement
{
    private const double DevicePixelsPerInchAtFullScale = 96;

    /// <summary>Tells whether a rectangle has a visible part on at least one monitor.</summary>
    /// <param name="x">Left edge of the rectangle.</param>
    /// <param name="y">Top edge of the rectangle.</param>
    /// <param name="width">Width of the rectangle.</param>
    /// <param name="height">Height of the rectangle.</param>
    /// <param name="monitors">Areas of the monitors, in the same coordinates.</param>
    /// <returns>True when the rectangle overlaps a monitor; touching an edge is no overlap.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="monitors"/> is null.</exception>
    public static bool IsOnAnyMonitor(
        double x,
        double y,
        double width,
        double height,
        IReadOnlyList<(double X, double Y, double Width, double Height)> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        return monitors.Any(monitor =>
            x < monitor.X + monitor.Width
            && monitor.X < x + width
            && y < monitor.Y + monitor.Height
            && monitor.Y < y + height);
    }

    /// <summary>Screen pixels of a device-independent size on a monitor.</summary>
    /// <param name="size">Device-independent size.</param>
    /// <param name="dotsPerInch">Scale of the monitor, 96 at 100 %.</param>
    /// <returns>The size in screen pixels, rounded.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dotsPerInch"/> is 0.</exception>
    public static int ToScreenPixels(double size, uint dotsPerInch)
    {
        ArgumentOutOfRangeException.ThrowIfZero(dotsPerInch);
        return (int)Math.Round(size * dotsPerInch / DevicePixelsPerInchAtFullScale);
    }

    /// <summary>Device-independent size of screen pixels on a monitor.</summary>
    /// <param name="pixels">Size in screen pixels.</param>
    /// <param name="dotsPerInch">Scale of the monitor, 96 at 100 %.</param>
    /// <returns>The device-independent size.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dotsPerInch"/> is 0.</exception>
    public static double ToDeviceIndependent(int pixels, uint dotsPerInch)
    {
        ArgumentOutOfRangeException.ThrowIfZero(dotsPerInch);
        return pixels * DevicePixelsPerInchAtFullScale / dotsPerInch;
    }
}
