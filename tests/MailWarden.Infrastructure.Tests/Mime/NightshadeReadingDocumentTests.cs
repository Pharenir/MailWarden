using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using MailWarden.Infrastructure.Mime;
using MimeKit;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>
/// AP-04e cases 9-15 (E4-E9): the reading document in <see cref="ReadingTheme.Nightshade"/>. Colours are compared without
/// regard to case. Light and Dark are recognised by the same markers as in <see cref="ReadingDocumentThemeTests"/>.
/// </summary>
[Trait("Category", "feature:ui")]
public sealed class NightshadeReadingDocumentTests
{
    // Nightshade palette, fixed values of the requirement (E5, E7).
    private const string PageColour = "#231E33";
    private const string TextColour = "#ECE8F7";
    private const string AccentColour = "#FF8A3D";
    private const string SelectionColour = "#4A3B7A";
    private const string SelectionTextColour = "#FFFFFF";
    private static readonly string[] NightshadeColours = [PageColour, TextColour, AccentColour, SelectionColour];

    // Markers of the existing styles (E8): they must not change.
    private const string LightMarker = "html,body{background:#ffffff;color:#1a1a1a;}";
    private const string DarkSchemeMarker = "color-scheme:dark";
    private const string DarkBackgroundMarker = "background:#202020";
    private const string DarkLinkColour = "#8ab4f8";
    private const string MarkLinkColour = "#0b57d0";

    private const string EmbeddedPng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    // Built by the real reader so that every member of MailContent is consistent; html and plain text are then set directly.
    private static MailContent ContentWith(string? html, string plainText = "plain_text")
    {
        MimeMessage message = ReadingEml.NewMessage("subject_a");
        message.Body = new TextPart("plain") { Text = "reader text" };
        using MemoryStream stream = ReadingEml.ToStream(message);
        MailContent read = MailContentReader.ReadAsync(stream).GetAwaiter().GetResult();
        return read with { Html = html, PlainText = plainText };
    }

    private static IHtmlDocument Parse(ReadingDocument document) => new HtmlParser().ParseDocument(document.Html);

    private static string PageStyle(ReadingDocument document) =>
        string.Concat(Parse(document).QuerySelectorAll("style").Select(style => style.TextContent));

    // The veil is a rule of our own page style; text in the body never counts.
    private static bool HasVeil(ReadingDocument document)
    {
        string style = PageStyle(document);
        return style.Contains("html::after", StringComparison.Ordinal)
            && style.Contains("rgba(0,0,0,.25)", StringComparison.Ordinal)
            && style.Contains("pointer-events:none", StringComparison.Ordinal);
    }

    private static bool Has(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    // Splits a style sheet without nesting into (selector, declarations) pairs.
    private static IReadOnlyList<(string Selector, string Declarations)> Rules(string style) =>
        [.. style.Split('}', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('{', 2))
            .Where(pair => pair.Length == 2)
            .Select(pair => (pair[0].Trim(), pair[1].Trim()))];

    // Declarations of the first rule whose selector contains every fragment; empty when there is none.
    private static string DeclarationsOfRuleWith(string style, params string[] selectorFragments)
    {
        foreach ((string selector, string declarations) in Rules(style))
        {
            if (selectorFragments.All(fragment => Has(selector, fragment)))
            {
                return declarations;
            }
        }

        return string.Empty;
    }

    // Nightshade page: dark scheme, palette page and text colour, no light or dark style marker, no veil.
    private static void AssertNightshadePage(ReadingDocument document, string description)
    {
        string style = PageStyle(document);
        Assert.True(Has(style, DarkSchemeMarker), $"{description}: color-scheme:dark");
        Assert.True(Has(style, "background:" + PageColour), $"{description}: page colour");
        Assert.True(Has(style, "color:" + TextColour), $"{description}: text colour");
        Assert.False(HasVeil(document), $"{description}: veil");
        Assert.DoesNotContain(LightMarker, document.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(DarkBackgroundMarker, document.Html, StringComparison.Ordinal);
    }

    // Light sheet under the veil, with the page colour of the palette nowhere in the document (E6, case 11).
    private static void AssertVeiledLightSheet(ReadingDocument document, string description)
    {
        Assert.Contains(LightMarker, document.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(DarkSchemeMarker, document.Html, StringComparison.Ordinal);
        Assert.True(HasVeil(document), $"{description}: veil");
        Assert.DoesNotContain(PageColour, document.Html, StringComparison.OrdinalIgnoreCase);
    }

    private static ReadingDocument Build(string? html, ReadingTheme theme, bool allowRemoteImages = false, bool allowEmbeddedImages = true) =>
        ReadingDocumentBuilder.Build(ContentWith(html), allowRemoteImages, allowEmbeddedImages, theme);

    // E6: same style, same body, same meta elements as the dark theme, and the veiled light sheet.
    private static void AssertSameAsDark(string html, string description, bool allowRemoteImages = false)
    {
        ReadingDocument nightshade = Build(html, ReadingTheme.Nightshade, allowRemoteImages);
        ReadingDocument dark = Build(html, ReadingTheme.Dark, allowRemoteImages);

        Assert.True(PageStyle(dark) == PageStyle(nightshade), $"{description}: page style differs from the dark theme");
        Assert.True(Parse(dark).Body!.InnerHtml == Parse(nightshade).Body!.InnerHtml, $"{description}: body differs");
        Assert.Equal(
            Parse(dark).QuerySelectorAll("meta").Select(meta => meta.OuterHtml),
            Parse(nightshade).QuerySelectorAll("meta").Select(meta => meta.OuterHtml));
        AssertVeiledLightSheet(nightshade, description);
    }

    // ---- Case 15 / E7 --------------------------------------------------------------------------------------

    // Case 15
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Page_background_of_nightshade_is_the_palette_surface_and_the_others_stay()
    {
        await Task.Yield();

        Assert.Equal(((byte)0x23, (byte)0x1E, (byte)0x33), ReadingDocumentBuilder.PageBackground(ReadingTheme.Nightshade));
        Assert.Equal(((byte)255, (byte)255, (byte)255), ReadingDocumentBuilder.PageBackground(ReadingTheme.Light));
        Assert.Equal(((byte)0x20, (byte)0x20, (byte)0x20), ReadingDocumentBuilder.PageBackground(ReadingTheme.Dark));
    }

    // Case 15: the enum values of the contract.
    [Fact]
    public void Reading_theme_values_follow_the_contract()
    {
        Assert.Equal(0, (int)ReadingTheme.Light);
        Assert.Equal(1, (int)ReadingTheme.Dark);
        Assert.Equal(2, (int)ReadingTheme.Nightshade);
    }

    // ---- Case 9: plain text --------------------------------------------------------------------------------

    // Case 9
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_in_nightshade_gets_the_palette_page_without_veil_and_stays_encoded()
    {
        await Task.Yield();
        const string text = "line one <script>alert('x')</script>\nline two & <b>not bold</b>";
        MailContent content = ContentWith(null, text);

        ReadingDocument viaBuild = ReadingDocumentBuilder.Build(content, allowRemoteImages: false, allowEmbeddedImages: true, ReadingTheme.Nightshade);
        ReadingDocument direct = ReadingDocumentBuilder.BuildFromPlainText(content, ReadingTheme.Nightshade);

        foreach (ReadingDocument document in new[] { viaBuild, direct })
        {
            AssertNightshadePage(document, "plain text");
            IHtmlDocument parsed = Parse(document);
            Assert.Empty(parsed.QuerySelectorAll("script"));
            Assert.Empty(parsed.Body!.QuerySelectorAll("b"));
            Assert.Contains("<script>alert('x')</script>", parsed.Body.TextContent, StringComparison.Ordinal);
            Assert.Contains("line two & <b>not bold</b>", parsed.Body.TextContent, StringComparison.Ordinal);
            Assert.Contains("pre-wrap", document.Html, StringComparison.Ordinal);
            Assert.Equal(0, document.BlockedRemoteResourceCount);
        }
    }

    // Case 9: the plain text body is the same in all three themes, only the page style differs.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_body_is_the_same_in_all_three_themes()
    {
        await Task.Yield();
        MailContent content = ContentWith(null, "plain line <i>one</i>\nline two");

        string light = Parse(ReadingDocumentBuilder.BuildFromPlainText(content, ReadingTheme.Light)).Body!.InnerHtml;
        string dark = Parse(ReadingDocumentBuilder.BuildFromPlainText(content, ReadingTheme.Dark)).Body!.InnerHtml;
        string nightshade = Parse(ReadingDocumentBuilder.BuildFromPlainText(content, ReadingTheme.Nightshade)).Body!.InnerHtml;

        Assert.Equal(light, dark);
        Assert.Equal(light, nightshade);
        Assert.NotEqual(
            ReadingDocumentBuilder.BuildFromPlainText(content, ReadingTheme.Dark).Html,
            ReadingDocumentBuilder.BuildFromPlainText(content, ReadingTheme.Nightshade).Html);
    }

    // ---- Case 10: HTML without colours ---------------------------------------------------------------------

    // Case 10
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_without_colours_in_nightshade_gets_the_page_the_link_rule_and_the_selection_rule()
    {
        await Task.Yield();

        ReadingDocument document = Build("<h1>head_marker</h1><p>body_marker <a href=\"https://example.test/\">link</a></p>", ReadingTheme.Nightshade);
        string style = PageStyle(document);

        AssertNightshadePage(document, "html without colours");
        Assert.Contains("body_marker", Parse(document).Body!.TextContent, StringComparison.Ordinal);
        Assert.Contains("head_marker", Parse(document).Body!.TextContent, StringComparison.Ordinal);

        string linkRule = DeclarationsOfRuleWith(style, "a:link", "a:visited");
        Assert.True(Has(linkRule, AccentColour), $"link rule without the accent colour: '{linkRule}'");
        Assert.False(Has(linkRule, DarkLinkColour), "link rule still holds the dark style link colour");

        string selectionRule = DeclarationsOfRuleWith(style, "::selection");
        Assert.True(Has(selectionRule, SelectionColour), $"selection background: '{selectionRule}'");
        Assert.True(Has(selectionRule, SelectionTextColour), $"selection text: '{selectionRule}'");
    }

    // Case 10 / E5: links in <mark> get the readable colour of the dark style, because mark stays yellow.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Links_in_mark_keep_the_dark_style_colour_in_nightshade()
    {
        await Task.Yield();
        const string html = "<p><mark><a href=\"https://example.test/\">x</a></mark></p>";

        string nightshadeRule = DeclarationsOfRuleWith(PageStyle(Build(html, ReadingTheme.Nightshade)), "mark a");
        string darkRule = DeclarationsOfRuleWith(PageStyle(Build(html, ReadingTheme.Dark)), "mark a");

        Assert.True(Has(nightshadeRule, MarkLinkColour), $"mark rule: '{nightshadeRule}'");
        Assert.Equal(darkRule, nightshadeRule);
    }

    // Case 10: no veil, and the page differs from the dark one while the body stays.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_without_colours_in_nightshade_has_no_veil_and_differs_from_dark()
    {
        await Task.Yield();

        ReadingDocument nightshade = Build("<p>body_marker</p>", ReadingTheme.Nightshade);
        ReadingDocument dark = Build("<p>body_marker</p>", ReadingTheme.Dark);

        Assert.False(HasVeil(nightshade));
        Assert.NotEqual(PageStyle(dark), PageStyle(nightshade));
        Assert.Equal(Parse(dark).Body!.InnerHtml, Parse(nightshade).Body!.InnerHtml);
    }

    // Case 10 control (AP-04d cases 3, 6, 15): neutral content keeps the palette page, no veil.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<p style=\"margin:0; font-size:12px; text-align:center\">x</p>")]
    [InlineData("<p>color: red</p>")]
    [InlineData("<p title=\"color:red\">x</p>")]
    [InlineData("<style>p{color:red}</style><p>x</p>")]
    [InlineData("<p style=\"border:1px solid #ccc\">x</p>")]
    [InlineData("<p style=\"backdrop-filter:blur(1px)\">x</p>")]
    public async Task Mails_without_colours_of_their_own_get_the_palette_page(string html)
    {
        await Task.Yield();

        AssertNightshadePage(Build(html, ReadingTheme.Nightshade), html);
    }

    // ---- Case 11: own colours ------------------------------------------------------------------------------

    public static TheoryData<string, string> OwnColourTriggers => new()
    {
        // AP-04d case 5
        { "color", "<p style=\"color:red\">x</p>" },
        { "color #000", "<p style=\"color:#000\">x</p>" },
        { "background", "<p style=\"background:red\">x</p>" },
        { "background-color", "<p style=\"background-color:#ffffff\">x</p>" },
        { "background-image remote", "<p style=\"background-image:url(https://tracker.test/bg.png)\">x</p>" },
        { "background-image data", "<p style=\"background-image:url(" + EmbeddedPng + ")\">x</p>" },
        { "bgcolor", "<table bgcolor=\"#ffffff\"><tr><td>x</td></tr></table>" },
        { "font color", "<font color=\"red\">x</font>" },
        { "color attribute elsewhere", "<p color=\"red\">x</p>" },
        { "img embedded", "<p>x</p><img src=\"" + EmbeddedPng + "\">" },
        { "img remote", "<p>x</p><img src=\"https://tracker.test/a.gif\">" },
        { "img unknown cid", "<p>x</p><img src=\"cid:unknown@example.test\">" },
        { "colour in a td", "<table><tr><td style=\"color:red\">x</td></tr></table>" },
        // AP-04d case 7
        { "upper case", "<p STYLE=\"COLOR : red\">x</p>" },
        { "mixed case", "<p style=\"Color:Red\">x</p>" },
        { "spaces and tab", "<p style=\"  color\t:\tred  \">x</p>" },
        { "background-color upper", "<p style=\"BACKGROUND-COLOR : #FFF\">x</p>" },
        { "after another declaration", "<p style=\"margin:0;color:red\">x</p>" },
        { "FONT COLOR", "<FONT COLOR=\"red\">x</FONT>" },
        { "TABLE BGCOLOR", "<TABLE BGCOLOR=\"#fff\"><TR><TD>x</TD></TR></TABLE>" },
        { "css escape", "<p style=\"c\\6flor:red\">x</p>" },
        { "css escape with space", "<p style=\"c\\6f lor:red\">x</p>" },
        { "css escape first letter", "<p style=\"\\63olor:red\">x</p>" },
        { "css escape in background-color", "<p style=\"background-c\\6flor:red\">x</p>" },
        // AP-04d case 14
        { "all:initial", "<p style=\"all:initial\">x</p>" },
        { "filter brightness", "<p style=\"filter:brightness(0)\">x</p>" },
        { "filter invert upper", "<p style=\"FILTER : invert(1)\">x</p>" },
        { "mix-blend-mode", "<p style=\"mix-blend-mode:multiply\">x</p>" },
        { "box-shadow", "<p style=\"box-shadow:0 0 1px 9999px #fff\">x</p>" },
        { "border-image", "<p style=\"border-image:linear-gradient(#fff,#fff) 1 fill\">x</p>" },
        { "border-image-source", "<p style=\"border-image-source:linear-gradient(#fff,#fff)\">x</p>" },
        { "border-image-slice", "<p style=\"border-image-slice:1 fill\">x</p>" },
        { "list-style-image url", "<p style=\"list-style-image:url(https://images.example.test/a.png)\">x</p>" },
        { "content url", "<p style=\"content:url(https://images.example.test/a.png)\">x</p>" },
    };

    // Case 11
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(OwnColourTriggers))]
    public async Task Each_trigger_alone_gives_the_veiled_light_sheet_identical_to_the_dark_theme(string trigger, string html)
    {
        await Task.Yield();

        AssertSameAsDark(html, trigger);
    }

    // Case 11: the verdict does not jump with the image approval.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<p>x</p><img src=\"https://tracker.test/a.gif\">")]
    [InlineData("<p style=\"background-image:url(https://tracker.test/bg.png)\">x</p>")]
    [InlineData("<p>x</p><img src=\"cid:unknown@example.test\">")]
    public async Task Approving_remote_images_does_not_change_the_page_style_in_nightshade(string html)
    {
        await Task.Yield();

        string before = PageStyle(Build(html, ReadingTheme.Nightshade, allowRemoteImages: false));
        string after = PageStyle(Build(html, ReadingTheme.Nightshade, allowRemoteImages: true));

        Assert.Equal(before, after);
        AssertSameAsDark(html, html, allowRemoteImages: true);
    }

    // Case 11: values that paint nothing stay without veil (AP-04d case 15).
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("list-style:none")]
    [InlineData("list-style:disc")]
    [InlineData("list-style-image:none")]
    [InlineData("content:normal")]
    [InlineData("outline:1px solid #ccc")]
    [InlineData("text-shadow:1px 1px #ccc")]
    public async Task Declarations_that_paint_nothing_do_not_give_the_veil_in_nightshade(string declaration)
    {
        await Task.Yield();

        AssertNightshadePage(Build($"<p style=\"{declaration}\">body_marker</p>", ReadingTheme.Nightshade), declaration);
    }

    // Case 11: a mail reduced to text by the sanitizer counts as own colours, as in the dark theme.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Mail_reduced_to_text_by_the_sanitizer_gets_the_veiled_light_sheet_in_nightshade()
    {
        await Task.Yield();
        string html = string.Concat(Enumerable.Repeat("<p style=\"margin:0\">t</p>", 20_000));

        AssertSameAsDark(html, "reduced");
    }

    // Case 11: building in another theme before does not change the nightshade result.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Building_in_another_theme_before_does_not_change_the_nightshade_result()
    {
        await Task.Yield();
        MailContent content = ContentWith("<p style=\"color:#000\">body_marker</p>");

        ReadingDocument first = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Nightshade);
        _ = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Dark);
        _ = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Light);
        ReadingDocument second = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Nightshade);

        Assert.Equal(first.Html, second.Html);
    }

    // ---- Case 12 / E8: Light and Dark stay as they are -----------------------------------------------------

    public static TheoryData<string?, string, bool> CompatibilityMails => new()
    {
        { null, "plain text", false },
        { "<h1>head_marker</h1><p>body_marker <a href=\"https://example.test/\">link</a></p>", "html without colours", false },
        { "<p style=\"color:#000\">body_marker</p>", "html with colours", true },
        { "<p>x</p><img src=\"https://tracker.test/a.gif\">", "html with a remote image", true },
        { "<p><mark>x</mark></p>", "html with mark", false },
    };

    // Case 12
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(CompatibilityMails))]
    public async Task Light_and_dark_documents_keep_their_markers_and_never_show_a_palette_colour(string? html, string description, bool hasOwnColours)
    {
        await Task.Yield();
        MailContent content = ContentWith(html);

        ReadingDocument withoutTheme = ReadingDocumentBuilder.Build(content, allowRemoteImages: false);
        ReadingDocument light = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Light);
        ReadingDocument dark = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Dark);

        Assert.Equal(withoutTheme.Html, light.Html);
        Assert.Contains(LightMarker, light.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(DarkSchemeMarker, light.Html, StringComparison.Ordinal);
        Assert.False(HasVeil(light), description);
        foreach (string colour in NightshadeColours)
        {
            Assert.DoesNotContain(colour, light.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(colour, dark.Html, StringComparison.OrdinalIgnoreCase);
        }

        if (hasOwnColours)
        {
            Assert.Contains(LightMarker, dark.Html, StringComparison.Ordinal);
            Assert.True(HasVeil(dark), description);
        }
        else
        {
            Assert.Contains(DarkSchemeMarker, dark.Html, StringComparison.Ordinal);
            Assert.Contains(DarkBackgroundMarker, dark.Html, StringComparison.Ordinal);
            Assert.DoesNotContain(LightMarker, dark.Html, StringComparison.Ordinal);
            Assert.False(HasVeil(dark), description);
        }
    }

    // Case 12: the link colour of the dark style stays; the mark rule exists in dark only.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Dark_style_keeps_its_link_colour_and_the_mark_rule()
    {
        await Task.Yield();
        MailContent content = ContentWith("<p><mark><a href=\"https://example.test/\">x</a></mark></p>");

        string dark = PageStyle(ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Dark));
        string light = PageStyle(ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Light));

        Assert.Contains(DarkLinkColour, dark, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MarkLinkColour, dark, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mark a", light, StringComparison.Ordinal);
        Assert.DoesNotContain(DarkLinkColour, light, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Case 13: CSP --------------------------------------------------------------------------------------

    public static TheoryData<string?, string> CspInputs => new()
    {
        { null, "plain only" },
        { "<p>body_marker</p>", "html without colours" },
        { "<p style=\"color:#000\">body_marker</p>", "html with colours" },
        { "<p>body_marker</p><img src=\"https://tracker.test/a.gif\">", "html with a remote image" },
        { "<p>body_marker</p><img src=\"" + EmbeddedPng + "\">", "html with an embedded image" },
    };

    private static IReadOnlyList<string?> CspValues(ReadingDocument document) =>
        [.. Parse(document).QuerySelectorAll("meta")
            .Where(meta => string.Equals(meta.GetAttribute("http-equiv"), "Content-Security-Policy", StringComparison.OrdinalIgnoreCase))
            .Select(meta => meta.GetAttribute("content"))];

    // Case 13
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(CspInputs))]
    public async Task The_csp_and_the_image_rule_are_the_same_in_all_three_themes(string? html, string description)
    {
        await Task.Yield();
        MailContent content = ContentWith(html);

        foreach (bool allowRemote in new[] { false, true })
        {
            foreach (bool allowEmbedded in new[] { false, true })
            {
                ReadingDocument light = ReadingDocumentBuilder.Build(content, allowRemote, allowEmbedded, ReadingTheme.Light);
                ReadingDocument dark = ReadingDocumentBuilder.Build(content, allowRemote, allowEmbedded, ReadingTheme.Dark);
                ReadingDocument nightshade = ReadingDocumentBuilder.Build(content, allowRemote, allowEmbedded, ReadingTheme.Nightshade);

                string nightshadeCsp = Assert.Single(CspValues(nightshade))!;
                string context = $"{description}: remote={allowRemote} embedded={allowEmbedded}";
                Assert.True(Assert.Single(CspValues(light))! == nightshadeCsp, context + " (light)");
                Assert.True(Assert.Single(CspValues(dark))! == nightshadeCsp, context + " (dark)");
                Assert.Equal(dark.BlockedRemoteResourceCount, nightshade.BlockedRemoteResourceCount);
                Assert.Equal(light.BlockedRemoteResourceCount, nightshade.BlockedRemoteResourceCount);
                Assert.Equal(dark.WasReduced, nightshade.WasReduced);
            }
        }
    }

    // Case 13: remote images still need the approval in nightshade.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Remote_images_stay_blocked_without_approval_in_nightshade()
    {
        await Task.Yield();
        const string html = "<p>body_marker</p><img src=\"https://tracker.test/a.gif\"><img src=\"https://tracker.test/b.gif\">";

        ReadingDocument blocked = Build(html, ReadingTheme.Nightshade, allowRemoteImages: false);
        ReadingDocument allowed = Build(html, ReadingTheme.Nightshade, allowRemoteImages: true);

        Assert.Equal(2, blocked.BlockedRemoteResourceCount);
        Assert.DoesNotContain("tracker.test", blocked.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("http:", Assert.Single(CspValues(blocked))!, StringComparison.Ordinal);
        Assert.DoesNotContain("https:", Assert.Single(CspValues(blocked))!, StringComparison.Ordinal);
        Assert.Equal(0, allowed.BlockedRemoteResourceCount);
        Assert.Contains("tracker.test", allowed.Html, StringComparison.Ordinal);
    }

    // ---- Case 14 / E9: hostile mails -----------------------------------------------------------------------

    public static TheoryData<string, string> HostileMails => new()
    {
        { "style element", "<style>html,body{background:#ff0000 !important;color:#00ff00 !important}</style><p>body_marker</p>" },
        {
            "style element naming our rules",
            "<style>html::after{background:rgba(255,0,0,1);pointer-events:auto} a:link{color:#ff00ff} ::selection{background:#00ffff}</style><p>body_marker</p>"
        },
        {
            "hostile attributes",
            "<body style=\"background:#ff0000\"><p style=\"color:#00ff00;position:fixed;top:0;left:0;width:100%;height:100%;background:url(https://tracker.test/x.png)\">body_marker</p></body>"
        },
        { "words in the text", "<p>html::after{background:red} color-scheme:dark #231E33 #FF8A3D</p><p>body_marker</p>" },
        { "script and handler", "<p onclick=\"alert(1)\">body_marker</p><script>document.body.style.background='#ff0000'</script>" },
    };

    // Case 14
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(HostileMails))]
    public async Task Sanitizer_output_is_identical_across_the_themes_and_only_the_page_style_differs(string description, string html)
    {
        await Task.Yield();
        MailContent content = ContentWith(html);

        ReadingDocument light = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Light);
        ReadingDocument dark = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Dark);
        ReadingDocument nightshade = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Nightshade);

        Assert.True(Parse(light).Body!.InnerHtml == Parse(nightshade).Body!.InnerHtml, description + " (light)");
        Assert.True(Parse(dark).Body!.InnerHtml == Parse(nightshade).Body!.InnerHtml, description + " (dark)");
        Assert.Equal(
            Parse(dark).QuerySelectorAll("meta").Select(meta => meta.OuterHtml),
            Parse(nightshade).QuerySelectorAll("meta").Select(meta => meta.OuterHtml));
        Assert.Single(Parse(nightshade).QuerySelectorAll("style"));
        Assert.DoesNotContain("#ff0000", nightshade.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#00ff00", nightshade.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#ff00ff", nightshade.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#00ffff", nightshade.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", nightshade.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", nightshade.Html, StringComparison.OrdinalIgnoreCase);

        // Our style comes before the mail body.
        Assert.True(
            nightshade.Html.IndexOf("<style", StringComparison.OrdinalIgnoreCase) < nightshade.Html.IndexOf("body_marker", StringComparison.Ordinal),
            description);
    }

    // Case 14 / E9: the page style is a fixed text of ours; of the mail only the yes/no of own colours reaches the selection.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_page_style_depends_only_on_the_yes_no_of_own_colours_not_on_the_colour_values()
    {
        await Task.Yield();

        string styleOwnA = PageStyle(Build("<p style=\"color:#123456\">a</p>", ReadingTheme.Nightshade));
        string styleOwnB = PageStyle(Build("<p style=\"color:#abcdef;background:#fedcba\">completely different text</p>", ReadingTheme.Nightshade));
        string styleNoneA = PageStyle(Build("<p>a</p>", ReadingTheme.Nightshade));
        string styleNoneB = PageStyle(Build("<h1>other</h1><p style=\"margin:1px\">words #123456 words</p>", ReadingTheme.Nightshade));

        Assert.Equal(styleOwnA, styleOwnB);
        Assert.Equal(styleNoneA, styleNoneB);
        Assert.NotEqual(styleOwnA, styleNoneA);
        foreach (string style in new[] { styleOwnA, styleOwnB, styleNoneA, styleNoneB })
        {
            Assert.DoesNotContain("123456", style, StringComparison.Ordinal);
            Assert.DoesNotContain("abcdef", style, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fedcba", style, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Case 14: reduction and blocked-resource count do not depend on the theme.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Blocked_remote_resource_count_and_reduction_do_not_depend_on_the_theme()
    {
        await Task.Yield();
        string attributes = string.Concat(Enumerable.Range(0, 300).Select(index => $" a{index}=\"x\""));
        MailContent content = ContentWith($"<p>keep_marker</p><div{attributes}>inside</div><img src=\"https://tracker.test/a.gif\">");

        ReadingDocument dark = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Dark);
        ReadingDocument nightshade = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Nightshade);

        Assert.Equal(dark.WasReduced, nightshade.WasReduced);
        Assert.Equal(dark.BlockedRemoteResourceCount, nightshade.BlockedRemoteResourceCount);
        Assert.Contains("keep_marker", nightshade.Html, StringComparison.Ordinal);
    }
}
