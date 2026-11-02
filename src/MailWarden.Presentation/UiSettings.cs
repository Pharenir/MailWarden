namespace MailWarden.Presentation;

/// <summary>
/// Arrangement of message list and reading pane; the navigation is always on the left.
/// </summary>
public enum ReadingPaneLayout
{
    /// <summary>List and reading pane next to each other.</summary>
    SideBySide = 1,

    /// <summary>List above the reading pane.</summary>
    ListAbove = 2,
}

/// <summary>
/// Theme of the application shell. The reading pane follows what it resolves to; a mail with colors of its own stays on a
/// light sheet.
/// </summary>
public enum AppTheme
{
    /// <summary>Follows the Windows setting.</summary>
    System = 0,

    /// <summary>Light.</summary>
    Light = 1,

    /// <summary>Dark.</summary>
    Dark = 2,

    /// <summary>The palette of the Nightshade project: dark to the controls, with colors of its own; the default.</summary>
    Nightshade = 3,
}

/// <summary>
/// Settings of the user interface that survive a restart.
/// </summary>
/// <param name="Layout">Arrangement of list and reading pane.</param>
/// <param name="Theme">Theme of the shell.</param>
/// <param name="NavigationWidth">Width of the navigation.</param>
/// <param name="ListWidth">Width of the message list in the layout side by side.</param>
/// <param name="ListHeight">Height of the message list in the layout list above.</param>
/// <param name="MarkReadOnSelect">True when a mail shown long enough is marked as read.</param>
/// <param name="WindowWidth">Width of the window in device-independent pixels; null when not stored.</param>
/// <param name="WindowHeight">Height of the window in device-independent pixels; null when not stored.</param>
/// <param name="WindowX">Left edge of the window in screen pixels; null when not stored.</param>
/// <param name="WindowY">Top edge of the window in screen pixels; null when not stored.</param>
/// <param name="WindowMaximized">True when the window was maximised; the other values then describe its normal size.</param>
public sealed record UiSettings(
    ReadingPaneLayout Layout,
    AppTheme Theme,
    double NavigationWidth,
    double ListWidth,
    double ListHeight,
    bool MarkReadOnSelect = false,
    double? WindowWidth = null,
    double? WindowHeight = null,
    int? WindowX = null,
    int? WindowY = null,
    bool WindowMaximized = false)
{
    /// <summary>Smallest allowed value of the sizes.</summary>
    public const double MinimumSize = 150;

    /// <summary>Largest allowed value of the sizes.</summary>
    public const double MaximumSize = 2000;

    /// <summary>Smallest allowed width and height of the window.</summary>
    public const double MinimumWindowSize = 400;

    /// <summary>Largest allowed width and height of the window.</summary>
    public const double MaximumWindowSize = 20000;

    /// <summary>Largest allowed distance of the window position from 0, in both directions.</summary>
    public const int MaximumWindowPosition = 20000;

    /// <summary>SideBySide, Nightshade, 280, 420, 320.</summary>
    public static UiSettings Default { get; } = new(ReadingPaneLayout.SideBySide, AppTheme.Nightshade, 280, 420, 320);
}

/// <summary>
/// Stores the <see cref="UiSettings"/>.
/// </summary>
public interface IUiSettingsStore
{
    /// <summary>Loads the settings.</summary>
    /// <returns>The stored settings; defaults for what is not stored.</returns>
    /// <exception cref="UiSettingsException">The stored settings are invalid.</exception>
    UiSettings Load();

    /// <summary>Stores the settings.</summary>
    /// <param name="settings">Settings to store.</param>
    void Save(UiSettings settings);
}

/// <summary>
/// The settings file is invalid or unreadable. The message names the file and the key, never a value.
/// </summary>
public sealed class UiSettingsException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Description with file name and key, without values.</param>
    public UiSettingsException(string message)
        : base(message)
    {
    }
}
