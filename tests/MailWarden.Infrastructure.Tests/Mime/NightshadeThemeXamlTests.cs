using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using MailWarden.Infrastructure.Mime;
using MimeKit;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>
/// AP-04e cases 19-21 (E5, E11): reads <c>src\MailWarden.App\Themes\NightshadeTheme.xaml</c> as XML (the file is found by
/// walking up from the test output folder to <c>MailWarden.sln</c>) and checks its structure, its palette and the match
/// with the reading pane style. The window itself runs in no test.
/// </summary>
[Trait("Category", "feature:ui")]
public sealed partial class NightshadeThemeXamlTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] ThemeDictionaryOnlyKey = ["Dark"];

    // Required keys of the requirement (table below E16), key -> colour.
    public static TheoryData<string, string> RequiredColours => new()
    {
        { "TextFillColorPrimaryBrush", "#ECE8F7" },
        { "DefaultTextForegroundThemeBrush", "#ECE8F7" },
        { "TextFillColorSecondaryBrush", "#A99FC6" },
        { "ButtonBackground", "#2E2744" },
        { "ComboBoxBackground", "#2E2744" },
        { "TextControlBackground", "#17131F" },
        { "MenuFlyoutPresenterBackground", "#231E33" },
        { "FlyoutPresenterBackground", "#231E33" },
        { "SystemFillColorCautionBrush", "#FFD166" },
        { "ListViewItemBackgroundSelected", "#4A3B7A" },
        { "TreeViewItemBackgroundSelected", "#4A3B7A" },
        { "ListViewItemForegroundSelected", "#FFFFFF" },
        { "AccentFillColorDefaultBrush", "#FF8A3D" },
        { "TextOnAccentFillColorPrimaryBrush", "#1A1626" },
        { "ToggleSwitchFillOn", "#FF8A3D" },
        { "ContentDialogBackground", "#231E33" },
        { "SolidBackgroundFillColorBaseBrush", "#231E33" },
        { "SystemFillColorCriticalBrush", "#FF8A8A" },
    };

    [GeneratedRegex(@"^\{\s*(?:StaticResource|ThemeResource)\s+(?:ResourceKey\s*=\s*)?([^\s}]+)\s*\}$")]
    private static partial Regex ResourceMarkup();

    [GeneratedRegex(@"(?:StaticResource|ThemeResource)\s+(?:ResourceKey\s*=\s*)?([^\s}]+)\s*\}")]
    private static partial Regex ResourceMarkupAnywhere();

    // ---- Loading -------------------------------------------------------------------------------------------

    private static string FindXamlPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MailWarden.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "MailWarden.sln not found above the test output folder");
        string path = Path.Combine(directory!.FullName, "src", "MailWarden.App", "Themes", "NightshadeTheme.xaml");
        Assert.True(File.Exists(path), "NightshadeTheme.xaml does not exist at the expected place");
        return path;
    }

    private static XDocument LoadXaml() => XDocument.Load(FindXamlPath());

    // The entries of the one theme dictionary, in document order.
    private static IReadOnlyList<XElement> DarkEntries()
    {
        XElement root = LoadXaml().Root!;
        XElement themeDictionaries = Assert.Single(root.Elements());
        Assert.EndsWith("ThemeDictionaries", themeDictionaries.Name.LocalName, StringComparison.Ordinal);
        XElement dark = Assert.Single(themeDictionaries.Elements());
        Assert.Equal("ResourceDictionary", dark.Name.LocalName);
        Assert.Equal("Dark", (string?)dark.Attribute(XamlNamespace + "Key"));
        return [.. dark.Elements()];
    }

    private static string? KeyOf(XElement entry) => (string?)entry.Attribute(XamlNamespace + "Key");

    private static string? NormaliseColour(string? text)
    {
        if (text is null)
        {
            return null;
        }

        string colour = text.Trim().ToUpperInvariant();
        return colour.Length == 9 && colour.StartsWith("#FF", StringComparison.Ordinal) ? "#" + colour[3..] : colour;
    }

    // Colour of an entry; a StaticResource alias (element or markup extension) is followed to its target in the same dictionary.
    private static string? ColourOf(string key, IReadOnlyList<XElement> entries, int depth = 0)
    {
        Assert.True(depth < 20, $"alias chain too long or circular at '{key}'");
        XElement? entry = entries.FirstOrDefault(candidate => KeyOf(candidate) == key);
        if (entry is null)
        {
            return null;
        }

        string localName = entry.Name.LocalName;
        if (localName == "StaticResource" || localName == "ThemeResource")
        {
            string? target = (string?)entry.Attribute("ResourceKey");
            return target is null ? null : ColourOf(target, entries, depth + 1);
        }

        string? text = localName == "Color" ? entry.Value : (string?)entry.Attribute("Color");
        if (text is null)
        {
            return null;
        }

        Match markup = ResourceMarkup().Match(text.Trim());
        return markup.Success ? ColourOf(markup.Groups[1].Value, entries, depth + 1) : NormaliseColour(text);
    }

    private static string RequiredColour(string key)
    {
        string? colour = ColourOf(key, DarkEntries());
        Assert.True(colour is not null, $"key '{key}' is missing or has no colour");
        return colour!;
    }

    // ---- Case 20: structure --------------------------------------------------------------------------------

    // Case 20 / E11: only the theme dictionary "Dark".
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_file_holds_only_the_theme_dictionary_with_the_key_Dark()
    {
        await Task.Yield();
        XElement root = LoadXaml().Root!;

        Assert.Equal("ResourceDictionary", root.Name.LocalName);
        XElement themeDictionaries = Assert.Single(root.Elements());
        Assert.EndsWith("ThemeDictionaries", themeDictionaries.Name.LocalName, StringComparison.Ordinal);
        Assert.Equal(ThemeDictionaryOnlyKey, themeDictionaries.Elements().Select(child => (string?)child.Attribute(XamlNamespace + "Key")).ToArray());
        Assert.NotEmpty(DarkEntries());
    }

    // Case 20 / E11: no key twice.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task No_key_is_defined_twice_and_every_entry_has_a_key()
    {
        await Task.Yield();
        IReadOnlyList<XElement> entries = DarkEntries();

        Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(KeyOf(entry)), $"entry '{entry.Name.LocalName}' has no x:Key"));
        string[] duplicates = [.. entries.Select(KeyOf).GroupBy(key => key, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key!)];
        Assert.True(duplicates.Length == 0, "duplicate keys: " + string.Join(", ", duplicates));
    }

    // Case 20 / E11: every ResourceKey of a StaticResource is defined earlier in the same dictionary (otherwise the app fails at load).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Every_static_resource_points_to_a_key_defined_earlier_in_the_same_dictionary()
    {
        await Task.Yield();
        IReadOnlyList<XElement> entries = DarkEntries();
        var problems = new List<string>();

        for (int index = 0; index < entries.Count; index++)
        {
            XElement entry = entries[index];
            var references = new List<string>();
            if (entry.Name.LocalName == "StaticResource")
            {
                references.Add((string?)entry.Attribute("ResourceKey") ?? string.Empty);
            }

            foreach (XAttribute attribute in entry.Attributes().Where(attribute => attribute.Name != XamlNamespace + "Key"))
            {
                references.AddRange(ResourceMarkupAnywhere().Matches(attribute.Value).Select(match => match.Groups[1].Value));
            }

            foreach (XElement nested in entry.Descendants().Where(child => child.Name.LocalName == "StaticResource"))
            {
                references.Add((string?)nested.Attribute("ResourceKey") ?? string.Empty);
            }

            foreach (string reference in references)
            {
                bool definedEarlier = reference.Length > 0 && entries.Take(index).Any(earlier => KeyOf(earlier) == reference);
                if (!definedEarlier)
                {
                    problems.Add($"'{KeyOf(entry)}' (entry {index}) -> '{reference}'");
                }
            }
        }

        Assert.True(problems.Count == 0, "StaticResource not defined earlier: " + string.Join("; ", problems));
    }

    // Case 20 / E11: the keys and colours of the table.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(RequiredColours))]
    public async Task Required_key_has_its_palette_colour(string key, string expectedColour)
    {
        await Task.Yield();

        Assert.Equal(NormaliseColour(expectedColour), RequiredColour(key));
    }

    // Case 20: the aliases of the controls in use are in the file, not only the base brushes (the spike found that base brushes alone do nothing).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_file_carries_aliases_and_not_only_base_brushes()
    {
        await Task.Yield();
        IReadOnlyList<XElement> entries = DarkEntries();

        int aliases = entries.Count(entry => entry.Name.LocalName == "StaticResource");

        Assert.True(aliases >= 50, $"expected the generated aliases of the controls in use, found {aliases}");
    }

    // ---- Case 19: match with the reading pane --------------------------------------------------------------

    private static MailContent ContentWith(string? html, string plainText = "plain_text")
    {
        MimeMessage message = ReadingEml.NewMessage("subject_a");
        message.Body = new TextPart("plain") { Text = "reader text" };
        using MemoryStream stream = ReadingEml.ToStream(message);
        MailContent read = MailContentReader.ReadAsync(stream).GetAwaiter().GetResult();
        return read with { Html = html, PlainText = plainText };
    }

    private static string NightshadePageStyle(string? html) =>
        string.Concat(new HtmlParser()
            .ParseDocument(ReadingDocumentBuilder.Build(ContentWith(html), false, true, ReadingTheme.Nightshade).Html)
            .QuerySelectorAll("style").Select(style => style.TextContent));

    private static string DeclarationsOfRuleWith(string style, params string[] selectorFragments)
    {
        foreach (string part in style.Split('}', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('{', 2);
            if (pair.Length == 2 && selectorFragments.All(fragment => pair[0].Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                return pair[1];
            }
        }

        return string.Empty;
    }

    private static bool Has(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private const string HtmlWithLink = "<p>body_marker <a href=\"https://example.test/\">link</a></p>";

    // Case 19: page, text, link and selection colours of the reading pane equal the keys of the XAML.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reading_pane_colours_equal_the_xaml_keys()
    {
        await Task.Yield();
        string surface = RequiredColour("SolidBackgroundFillColorBaseBrush");
        string text = RequiredColour("TextFillColorPrimaryBrush");
        string accent = RequiredColour("AccentFillColorDefaultBrush");
        string selection = RequiredColour("ListViewItemBackgroundSelected");
        string selectionText = RequiredColour("ListViewItemForegroundSelected");
        string htmlStyle = NightshadePageStyle(HtmlWithLink);
        string plainStyle = NightshadePageStyle(null);

        foreach (string style in new[] { htmlStyle, plainStyle })
        {
            string pageRule = DeclarationsOfRuleWith(style, "body");
            Assert.True(Has(pageRule, "background:" + surface), $"page rule '{pageRule}' vs surface {surface}");
            Assert.True(Has(pageRule, "color:" + text), $"page rule '{pageRule}' vs text {text}");
        }

        string linkRule = DeclarationsOfRuleWith(htmlStyle, "a:link", "a:visited");
        Assert.True(Has(linkRule, accent), $"link rule '{linkRule}' vs accent {accent}");
        string selectionRule = DeclarationsOfRuleWith(htmlStyle, "::selection");
        Assert.True(Has(selectionRule, selection), $"selection rule '{selectionRule}' vs {selection}");
        Assert.True(Has(selectionRule, selectionText), $"selection rule '{selectionRule}' vs {selectionText}");
    }

    // Case 19 / E7: the colour that is shown while a mail loads is the page colour of the XAML.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Page_background_equals_the_xaml_surface()
    {
        await Task.Yield();
        string surface = RequiredColour("SolidBackgroundFillColorBaseBrush");
        (byte red, byte green, byte blue) = ReadingDocumentBuilder.PageBackground(ReadingTheme.Nightshade);

        Assert.Equal(surface, $"#{red:X2}{green:X2}{blue:X2}");
    }

    // ---- Case 21: contrast ---------------------------------------------------------------------------------

    private static double Luminance(string colour)
    {
        double Channel(int offset)
        {
            double value = int.Parse(colour.AsSpan(1 + offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }

    private static double Contrast(string first, string second)
    {
        double lighter = Math.Max(Luminance(first), Luminance(second));
        double darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static string ColourIn(string declarations, string property)
    {
        Match match = Regex.Match(declarations, @"(?<![-\w])" + property + @"\s*:\s*(#[0-9a-fA-F]{6})", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(match.Success, $"no {property} colour in '{declarations}'");
        return match.Groups[1].Value.ToUpperInvariant();
    }

    // Case 21: colours taken from the style of the reading pane (not from the constants of the test).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reading_pane_style_is_readable()
    {
        await Task.Yield();
        string style = NightshadePageStyle(HtmlWithLink);
        string pageRule = DeclarationsOfRuleWith(style, "body");
        string surface = ColourIn(pageRule, "background");
        string text = ColourIn(pageRule, "color");
        string link = ColourIn(DeclarationsOfRuleWith(style, "a:link", "a:visited"), "color");
        string selectionRule = DeclarationsOfRuleWith(style, "::selection");

        Assert.True(Contrast(text, surface) >= 7d, $"text on page {Contrast(text, surface):F1}:1");
        Assert.True(Contrast(link, surface) >= 4.5d, $"link on page {Contrast(link, surface):F1}:1");
        Assert.True(
            Contrast(ColourIn(selectionRule, "color"), ColourIn(selectionRule, "background")) >= 4.5d,
            "selection text on selection");
    }

    // Case 21: colours taken from the XAML.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Xaml_palette_is_readable()
    {
        await Task.Yield();
        string surface = RequiredColour("SolidBackgroundFillColorBaseBrush");
        string window = RequiredColour("TextOnAccentFillColorPrimaryBrush"); // accent text is the window colour of the palette
        string text = RequiredColour("TextFillColorPrimaryBrush");
        string muted = RequiredColour("TextFillColorSecondaryBrush");
        string accent = RequiredColour("AccentFillColorDefaultBrush");
        string selection = RequiredColour("ListViewItemBackgroundSelected");
        string selectionText = RequiredColour("ListViewItemForegroundSelected");

        Assert.True(Contrast(text, surface) >= 7d, $"text on surface {Contrast(text, surface):F1}:1");
        Assert.True(Contrast(accent, surface) >= 4.5d, $"link (accent) on surface {Contrast(accent, surface):F1}:1");
        Assert.True(Contrast(muted, window) >= 4.5d, $"textMuted on window {Contrast(muted, window):F1}:1");
        Assert.True(Contrast(muted, surface) >= 4.5d, $"textMuted on surface {Contrast(muted, surface):F1}:1");
        Assert.True(Contrast(selectionText, selection) >= 4.5d, $"selection text on selection {Contrast(selectionText, selection):F1}:1");
        Assert.True(Contrast(window, accent) >= 4.5d, $"accent text on accent {Contrast(window, accent):F1}:1");
    }

    // Blends an #AARRGGBB (or #RRGGBB) colour over an opaque #RRGGBB background.
    private static string BlendOver(string colour, string background)
    {
        if (colour.Length == 7)
        {
            return colour;
        }

        double alpha = int.Parse(colour.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        string Mix(int offset)
        {
            int top = int.Parse(colour.AsSpan(3 + offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int below = int.Parse(background.AsSpan(1 + offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return ((int)Math.Round((top * alpha) + (below * (1 - alpha)))).ToString("X2", CultureInfo.InvariantCulture);
        }

        return "#" + Mix(0) + Mix(2) + Mix(4);
    }

    // Button hover must be visible against the resting button; disabled text must stay legible on the disabled fill.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Button_hover_and_disabled_text_are_distinguishable()
    {
        await Task.Yield();
        string hover = RequiredColour("ControlFillColorSecondaryBrush");
        string resting = RequiredColour("ButtonBackground");
        string disabledFill = RequiredColour("ControlFillColorDisabledBrush");
        string disabledText = BlendOver(RequiredColour("TextFillColorDisabledBrush"), disabledFill);

        Assert.True(Contrast(hover, resting) >= 1.2d, $"hover on resting button {Contrast(hover, resting):F2}:1");
        Assert.True(Contrast(disabledText, disabledFill) >= 3d, $"disabled text on disabled fill {Contrast(disabledText, disabledFill):F2}:1");
    }
}
