using MailWarden.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MailWarden.App;

/// <summary>
/// The parts of a theme that only the window itself can do: the palette dictionary of Nightshade, the requested element theme,
/// the backdrop, the window background and the title bar. Light, Dark and System use Mica and the default title bar. In Windows
/// high contrast the palette stays off whatever the theme is, because system text colors on our background could be unreadable.
/// </summary>
internal sealed class WindowThemeChrome
{
    private const string PaletteUri = "ms-appx:///Themes/NightshadeTheme.xaml";

    // Keys of NightshadeTheme.xaml the title bar and the window background read, so the colors live in one place.
    private const string PaletteThemeKey = "Dark";
    private const string WindowBackgroundKey = "NightshadeWindowBackgroundBrush";
    private const string TextKey = "TextFillColorPrimaryBrush";
    private const string MutedTextKey = "TextFillColorSecondaryBrush";
    private const string HoverKey = "SubtleFillColorSecondaryBrush";
    private const string PressedKey = "SubtleFillColorTertiaryBrush";

    private readonly Window window;

    // Kept in a field: the event of an object nobody holds would stop firing.
    private readonly AccessibilitySettings accessibilitySettings = new();

    private ResourceDictionary? mergedPalette;
    private AppTheme appliedTheme;
    private Panel? appliedRoot;

    /// <summary>Creates the chrome; changes nothing yet.</summary>
    /// <param name="window">The window it belongs to.</param>
    public WindowThemeChrome(Window window)
    {
        this.window = window;
        accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
    }

    /// <summary>
    /// Merges the palette for a start with this theme. Called before the XAML of the window is parsed, so that what resolves on
    /// load already sees the palette and no other theme shows first.
    /// </summary>
    /// <param name="theme">The theme the window starts with.</param>
    public void PrepareResources(AppTheme theme)
    {
        SetPaletteMerged(UsesPalette(theme));
    }

    /// <summary>
    /// Applies a theme to the window. Only called on the UI thread; everything happens before the next frame.
    /// </summary>
    /// <param name="theme">The theme of the shell.</param>
    /// <param name="root">The root element of the window.</param>
    public void Apply(AppTheme theme, Panel root)
    {
        appliedTheme = theme;
        appliedRoot = root;

        bool usesPalette = UsesPalette(theme);
        bool hasPaletteChanged = SetPaletteMerged(usesPalette);

        // A changed dictionary alone resolves nothing again; it takes a change of the theme. No frame is drawn in between.
        if (hasPaletteChanged)
        {
            root.RequestedTheme = ElementTheme.Light;
        }

        root.RequestedTheme = ToElementTheme(theme);
        ApplyWindowSurface(root, usesPalette);
        ApplyTitleBar(usesPalette);
    }

    private static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark or AppTheme.Nightshade => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private bool UsesPalette(AppTheme theme)
    {
        return theme == AppTheme.Nightshade && !accessibilitySettings.HighContrast;
    }

    // Returns whether the application resources changed. A new dictionary each time, as the spike of the requirement proved it.
    private bool SetPaletteMerged(bool isWanted)
    {
        if (isWanted == (mergedPalette is not null))
        {
            return false;
        }

        IList<ResourceDictionary> mergedDictionaries = Application.Current.Resources.MergedDictionaries;
        if (mergedPalette is not null)
        {
            mergedDictionaries.Remove(mergedPalette);
            mergedPalette = null;
            return true;
        }

        mergedPalette = new ResourceDictionary { Source = new Uri(PaletteUri) };

        // Last in the list, so it comes before the resources of WinUI in every lookup.
        mergedDictionaries.Add(mergedPalette);
        return true;
    }

    // Solid before the backdrop leaves and Mica before the solid leaves, so that the window never shows nothing behind the controls.
    private void ApplyWindowSurface(Panel root, bool usesPalette)
    {
        if (usesPalette)
        {
            root.Background = new SolidColorBrush(PaletteColor(WindowBackgroundKey));
            window.SystemBackdrop = null;
        }
        else
        {
            window.SystemBackdrop ??= new MicaBackdrop();
            root.ClearValue(Panel.BackgroundProperty);
        }
    }

    // Null gives the default of Windows back.
    private void ApplyTitleBar(bool usesPalette)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        Color? background = usesPalette ? PaletteColor(WindowBackgroundKey) : null;
        Color? text = usesPalette ? PaletteColor(TextKey) : null;
        Color? mutedText = usesPalette ? PaletteColor(MutedTextKey) : null;
        AppWindowTitleBar titleBar = window.AppWindow.TitleBar;
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = text;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = mutedText;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = text;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = mutedText;
        titleBar.ButtonHoverBackgroundColor = usesPalette ? PaletteColor(HoverKey) : null;
        titleBar.ButtonHoverForegroundColor = text;
        titleBar.ButtonPressedBackgroundColor = usesPalette ? PaletteColor(PressedKey) : null;
        titleBar.ButtonPressedForegroundColor = text;
    }

    // From the merged dictionary, not from the application resources: those answer with the theme of the application.
    private Color PaletteColor(string key)
    {
        ResourceDictionary palette = mergedPalette ?? throw new InvalidOperationException("The palette is not merged.");
        var themeDictionary = (ResourceDictionary)palette.ThemeDictionaries[PaletteThemeKey];
        return ((SolidColorBrush)themeDictionary[key]).Color;
    }

    // Windows reports this on a thread of its own.
    private void OnHighContrastChanged(AccessibilitySettings sender, object args)
    {
        window.DispatcherQueue.TryEnqueue(ApplyAgain);
    }

    private void ApplyAgain()
    {
        if (appliedRoot is Panel root)
        {
            Apply(appliedTheme, root);
        }
    }
}
