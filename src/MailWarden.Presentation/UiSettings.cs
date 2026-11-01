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
/// Theme of the application shell; mail content always stays on a light background.
/// </summary>
public enum AppTheme
{
    /// <summary>Follows the Windows setting.</summary>
    System = 0,

    /// <summary>Light.</summary>
    Light = 1,

    /// <summary>Dark.</summary>
    Dark = 2,
}

/// <summary>
/// Settings of the user interface that survive a restart.
/// </summary>
/// <param name="Layout">Arrangement of list and reading pane.</param>
/// <param name="Theme">Theme of the shell.</param>
/// <param name="NavigationWidth">Width of the navigation.</param>
/// <param name="ListSize">Width (side by side) or height (list above) of the message list.</param>
public sealed record UiSettings(ReadingPaneLayout Layout, AppTheme Theme, double NavigationWidth, double ListSize)
{
    /// <summary>Smallest allowed value of the sizes.</summary>
    public const double MinimumSize = 150;

    /// <summary>Largest allowed value of the sizes.</summary>
    public const double MaximumSize = 2000;

    /// <summary>SideBySide, System, 280, 420.</summary>
    public static UiSettings Default { get; } = new(ReadingPaneLayout.SideBySide, AppTheme.System, 280, 420);
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
