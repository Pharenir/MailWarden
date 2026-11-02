using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation;

/// <summary>
/// What the user interface needs to know about the values of <see cref="AppTheme"/>: their order in the theme choice, their
/// names, and what the reading pane shows for them.
/// </summary>
public static class AppThemes
{
    /// <summary>Gets the entries of the theme choice, in their order; every value of <see cref="AppTheme"/> once.</summary>
    public static IReadOnlyList<AppTheme> ChoiceOrder { get; } = [AppTheme.Nightshade, AppTheme.System, AppTheme.Light, AppTheme.Dark];

    /// <summary>Gets the name of a theme in the theme choice.</summary>
    /// <param name="theme">The theme.</param>
    /// <returns>The text of the entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="theme"/> is no known value.</exception>
    public static string DisplayName(AppTheme theme) => theme switch
    {
        AppTheme.System => UiText.ThemeSystem,
        AppTheme.Light => UiText.ThemeLight,
        AppTheme.Dark => UiText.ThemeDark,
        AppTheme.Nightshade => UiText.ThemeNightshade,
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown theme."),
    };

    /// <summary>
    /// Gets the theme of the reading document. Nightshade shows as dark to the window as the dark theme does, so only the
    /// stored choice tells them apart; for the others the window reports what it resolved to.
    /// </summary>
    /// <param name="theme">The theme of the shell.</param>
    /// <param name="isWindowDark">True when the window actually shows dark.</param>
    /// <returns>The theme for the reading document.</returns>
    public static ReadingTheme ToReadingTheme(AppTheme theme, bool isWindowDark)
    {
        if (theme == AppTheme.Nightshade)
        {
            return ReadingTheme.Nightshade;
        }

        return isWindowDark ? ReadingTheme.Dark : ReadingTheme.Light;
    }
}
