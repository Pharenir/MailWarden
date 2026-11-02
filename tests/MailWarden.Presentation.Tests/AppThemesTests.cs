using System.Reflection;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04e cases 6-8 (E3, E4): <see cref="AppThemes"/> and the new <see cref="AppTheme.Nightshade"/> value.</summary>
[Trait("Category", "feature:ui")]
public sealed class AppThemesTests
{
    // Case 6 / E1
    [Fact]
    public void App_theme_values_follow_the_contract()
    {
        Assert.Equal(0, (int)AppTheme.System);
        Assert.Equal(1, (int)AppTheme.Light);
        Assert.Equal(2, (int)AppTheme.Dark);
        Assert.Equal(3, (int)AppTheme.Nightshade);
        Assert.Equal(4, Enum.GetValues<AppTheme>().Length);
    }

    // Case 6
    [Fact]
    public void Choice_order_is_nightshade_system_light_dark()
    {
        AppTheme[] expected = [AppTheme.Nightshade, AppTheme.System, AppTheme.Light, AppTheme.Dark];

        Assert.Equal(expected, AppThemes.ChoiceOrder.ToArray());
    }

    // Case 6: every value once, none missing, none twice.
    [Fact]
    public void Choice_order_holds_every_theme_exactly_once()
    {
        AppTheme[] order = [.. AppThemes.ChoiceOrder];

        Assert.Equal(Enum.GetValues<AppTheme>().Length, order.Length);
        Assert.Equal(order.Length, order.Distinct().Count());
        Assert.Equal(Enum.GetValues<AppTheme>().OrderBy(theme => (int)theme), order.OrderBy(theme => (int)theme));
    }

    // Case 7
    [Fact]
    public void Display_name_of_nightshade_is_the_ui_text()
    {
        Assert.Equal("Nightshade", AppThemes.DisplayName(AppTheme.Nightshade));
    }

    // Case 7 / E3: UiText.ThemeNightshade holds the contract text (read by reflection, so the test does not depend on the visibility of UiText).
    [Fact]
    public void Ui_text_theme_nightshade_is_Nightshade()
    {
        Type? uiText = typeof(AppThemes).Assembly.GetType("MailWarden.Presentation.UiText");
        Assert.NotNull(uiText);
        PropertyInfo? property = uiText!.GetProperty("ThemeNightshade", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(property);

        Assert.Equal("Nightshade", property!.GetValue(null));
        Assert.Equal(property.GetValue(null), AppThemes.DisplayName(AppTheme.Nightshade));
    }

    // Case 7: every theme has a readable, distinct name.
    [Fact]
    public void Display_names_are_filled_and_distinct()
    {
        string[] names = [.. Enum.GetValues<AppTheme>().Select(AppThemes.DisplayName)];

        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    // Case 7: the name does not depend on the position in the choice.
    [Fact]
    public void Display_name_is_stable_between_calls()
    {
        foreach (AppTheme theme in AppThemes.ChoiceOrder)
        {
            Assert.Equal(AppThemes.DisplayName(theme), AppThemes.DisplayName(theme));
        }
    }

    // Case 7
    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(99)]
    public void Display_name_of_an_unknown_value_throws(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AppThemes.DisplayName((AppTheme)value));
    }

    // Case 8 / E4
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nightshade_gives_nightshade_whatever_the_window_reports(bool isWindowDark)
    {
        Assert.Equal(ReadingTheme.Nightshade, AppThemes.ToReadingTheme(AppTheme.Nightshade, isWindowDark));
    }

    // Case 8 / E4: for the other themes the window alone decides.
    [Theory]
    [InlineData(AppTheme.System, true, ReadingTheme.Dark)]
    [InlineData(AppTheme.System, false, ReadingTheme.Light)]
    [InlineData(AppTheme.Light, true, ReadingTheme.Dark)]
    [InlineData(AppTheme.Light, false, ReadingTheme.Light)]
    [InlineData(AppTheme.Dark, true, ReadingTheme.Dark)]
    [InlineData(AppTheme.Dark, false, ReadingTheme.Light)]
    public void Other_themes_follow_what_the_window_reports(AppTheme theme, bool isWindowDark, ReadingTheme expected)
    {
        Assert.Equal(expected, AppThemes.ToReadingTheme(theme, isWindowDark));
    }

    // Case 8: Dark to Nightshade changes the reading theme although the window stays dark (E10).
    [Fact]
    public void Switching_between_dark_and_nightshade_changes_the_reading_theme_with_the_window_dark_all_the_time()
    {
        ReadingTheme dark = AppThemes.ToReadingTheme(AppTheme.Dark, isWindowDark: true);
        ReadingTheme nightshade = AppThemes.ToReadingTheme(AppTheme.Nightshade, isWindowDark: true);

        Assert.NotEqual(dark, nightshade);
    }
}
