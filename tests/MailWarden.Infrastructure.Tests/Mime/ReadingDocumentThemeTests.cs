using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using MailWarden.Infrastructure.Mime;
using MimeKit;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>
/// AP-04d cases 1-12 (D1-D8): the reading document follows <see cref="ReadingTheme"/>, and the sanitizer reports
/// <see cref="SanitizedHtml.HasOwnColors"/>. The dark style is recognised by <c>color-scheme:dark</c> and <c>background:#202020</c>,
/// the light one by <c>html,body{background:#ffffff;color:#1a1a1a;}</c>.
/// </summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class ReadingDocumentThemeTests
{
    private const string LightMarker = "html,body{background:#ffffff;color:#1a1a1a;}";
    private const string DarkSchemeMarker = "color-scheme:dark";
    private const string DarkBackgroundMarker = "background:#202020";

    private const string EmbeddedPng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly IReadOnlyDictionary<string, string> NoInlineImages = new Dictionary<string, string>();

    // Built by the real reader so that every member of MailContent is consistent; html and plain text are then set directly.
    private static MailContent ContentWith(string? html, string plainText = "plain_text")
    {
        MimeMessage message = ReadingEml.NewMessage("subject_a");
        message.Body = new TextPart("plain") { Text = "reader text" };
        using MemoryStream stream = ReadingEml.ToStream(message);
        MailContent read = MailContentReader.ReadAsync(stream).GetAwaiter().GetResult();
        return read with { Html = html, PlainText = plainText };
    }

    private static bool IsDark(ReadingDocument document) =>
        document.Html.Contains(DarkSchemeMarker, StringComparison.Ordinal)
        && document.Html.Contains(DarkBackgroundMarker, StringComparison.Ordinal)
        && !document.Html.Contains(LightMarker, StringComparison.Ordinal);

    private static bool IsLight(ReadingDocument document) =>
        document.Html.Contains(LightMarker, StringComparison.Ordinal)
        && !document.Html.Contains(DarkSchemeMarker, StringComparison.Ordinal)
        && !document.Html.Contains(DarkBackgroundMarker, StringComparison.Ordinal);

    private static ReadingDocument BuildDark(string html, bool allowRemoteImages = false) =>
        ReadingDocumentBuilder.Build(ContentWith(html), allowRemoteImages, allowEmbeddedImages: true, ReadingTheme.Dark);

    private static IHtmlDocument Parse(ReadingDocument document) => new HtmlParser().ParseDocument(document.Html);

    private static IReadOnlyList<string?> CspValues(ReadingDocument document) =>
        [.. Parse(document).QuerySelectorAll("meta")
            .Where(meta => string.Equals(meta.GetAttribute("http-equiv"), "Content-Security-Policy", StringComparison.OrdinalIgnoreCase))
            .Select(meta => meta.GetAttribute("content"))];

    private static SanitizedHtml Sanitize(string html, bool allowRemoteImages = false) =>
        MailHtmlSanitizer.Sanitize(html, NoInlineImages, allowRemoteImages);

    // ---- Page background (D6) ------------------------------------------------------------------------------

    // D6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Page_background_is_white_for_light_and_the_dark_grey_of_the_dark_style_for_dark()
    {
        await Task.Yield();

        Assert.Equal(((byte)255, (byte)255, (byte)255), ReadingDocumentBuilder.PageBackground(ReadingTheme.Light));
        Assert.Equal(((byte)0x20, (byte)0x20, (byte)0x20), ReadingDocumentBuilder.PageBackground(ReadingTheme.Dark));
    }

    // ---- Cases 1 and 2: plain text -------------------------------------------------------------------------

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_in_the_dark_theme_gets_the_dark_style_and_the_text_stays_encoded()
    {
        await Task.Yield();
        const string text = "line one <script>alert('x')</script>\nline two & <b>not bold</b>";
        MailContent content = ContentWith(null, text);

        ReadingDocument viaBuild = ReadingDocumentBuilder.Build(content, allowRemoteImages: false, allowEmbeddedImages: true, ReadingTheme.Dark);
        ReadingDocument direct = ReadingDocumentBuilder.BuildFromPlainText(content, ReadingTheme.Dark);

        foreach (ReadingDocument document in new[] { viaBuild, direct })
        {
            Assert.True(IsDark(document));
            IHtmlDocument parsed = Parse(document);
            Assert.Empty(parsed.QuerySelectorAll("script"));
            Assert.Empty(parsed.Body!.QuerySelectorAll("b"));
            Assert.Contains("<script>alert('x')</script>", parsed.Body.TextContent, StringComparison.Ordinal);
            Assert.Contains("line two & <b>not bold</b>", parsed.Body.TextContent, StringComparison.Ordinal);
            Assert.Contains("pre-wrap", document.Html, StringComparison.Ordinal);
            Assert.Equal(0, document.BlockedRemoteResourceCount);
        }
    }

    // Case 2 / D5: byte-identical to the call without a theme.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_in_the_light_theme_is_byte_identical_to_the_call_without_a_theme()
    {
        await Task.Yield();
        MailContent content = ContentWith(null, "plain line <i>one</i>\nline two");

        Assert.Equal(
            ReadingDocumentBuilder.Build(content, allowRemoteImages: false).Html,
            ReadingDocumentBuilder.Build(content, allowRemoteImages: false, allowEmbeddedImages: true, ReadingTheme.Light).Html);
        Assert.Equal(
            ReadingDocumentBuilder.BuildFromPlainText(content).Html,
            ReadingDocumentBuilder.BuildFromPlainText(content, ReadingTheme.Light).Html);
        Assert.True(IsLight(ReadingDocumentBuilder.Build(content, allowRemoteImages: false)));
    }

    // Positive control: dark and light plain text really differ.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_differs_between_the_themes()
    {
        await Task.Yield();
        MailContent content = ContentWith(null, "text");

        Assert.NotEqual(
            ReadingDocumentBuilder.BuildFromPlainText(content, ReadingTheme.Light).Html,
            ReadingDocumentBuilder.BuildFromPlainText(content, ReadingTheme.Dark).Html);
    }

    // ---- Cases 3, 4, 8: HTML ------------------------------------------------------------------------------

    // Case 3
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_without_colours_in_the_dark_theme_gets_the_dark_style()
    {
        await Task.Yield();

        ReadingDocument document = BuildDark("<h1>head_marker</h1><p>body_marker <a href=\"https://example.test/\">link</a></p>");

        Assert.True(IsDark(document));
        Assert.Contains("body_marker", Parse(document).Body!.TextContent, StringComparison.Ordinal);
        Assert.Contains("head_marker", Parse(document).Body!.TextContent, StringComparison.Ordinal);
    }

    // Case 3: layout-only declarations are no colours.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Layout_only_style_declarations_do_not_count_as_own_colours()
    {
        await Task.Yield();
        const string html = "<p style=\"margin:0; font-size:12px; text-align:center\">body_marker</p>";

        Assert.False(Sanitize(html).HasOwnColors);
        Assert.True(IsDark(BuildDark(html)));
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_with_a_colour_declaration_stays_on_the_light_sheet_in_the_dark_theme()
    {
        await Task.Yield();

        ReadingDocument document = BuildDark("<p style=\"color:#000\">body_marker</p>");

        Assert.True(IsLight(document));
        Assert.Contains("body_marker", document.Html, StringComparison.Ordinal);
        Assert.Matches("color:\\s*(#000|rgb)", document.Html);
    }

    // Case 8 / D5 / D3: with own colours both themes give the same document.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_with_own_colours_gives_the_same_document_in_both_themes()
    {
        await Task.Yield();
        MailContent content = ContentWith("<p style=\"color:#000\">body_marker</p><img src=\"" + EmbeddedPng + "\">");

        string withoutTheme = ReadingDocumentBuilder.Build(content, allowRemoteImages: false).Html;
        string light = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Light).Html;
        string dark = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Dark).Html;

        Assert.Equal(withoutTheme, light);
        Assert.Equal(light, dark);
        Assert.Contains(LightMarker, light, StringComparison.Ordinal);
    }

    // Case 8 / D5: HTML without colours in the light theme is the call without a theme.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_without_colours_in_the_light_theme_is_byte_identical_to_the_call_without_a_theme()
    {
        await Task.Yield();
        MailContent content = ContentWith("<h1>head</h1><p>body_marker</p>");

        string withoutTheme = ReadingDocumentBuilder.Build(content, allowRemoteImages: false).Html;
        string light = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Light).Html;

        Assert.Equal(withoutTheme, light);
        Assert.Contains(LightMarker, light, StringComparison.Ordinal);
        Assert.NotEqual(light, ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Dark).Html);
    }

    // ---- Case 5: every D4 trigger alone --------------------------------------------------------------------

    public static TheoryData<string, string> D4Triggers => new()
    {
        { "color", "<p style=\"color:red\">x</p>" },
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
    };

    // Case 5
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(D4Triggers))]
    public async Task Each_trigger_alone_keeps_the_light_sheet_in_the_dark_theme(string trigger, string html)
    {
        await Task.Yield();

        Assert.True(Sanitize(html).HasOwnColors, $"sanitizer verdict for '{trigger}'");
        Assert.True(IsLight(BuildDark(html)), $"dark build for '{trigger}'");
    }

    // Case 5 control: without the trigger the same shapes are dark.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<p>x</p>")]
    [InlineData("<table><tr><td>x</td></tr></table>")]
    [InlineData("<font>x</font>")]
    [InlineData("<p style=\"margin:0\">x</p>")]
    public async Task Same_shapes_without_the_trigger_get_the_dark_style(string html)
    {
        await Task.Yield();

        Assert.False(Sanitize(html).HasOwnColors);
        Assert.True(IsDark(BuildDark(html)));
    }

    // Case 5 / decision: a blocked background-image and a removed <img> still count, before and after the approval.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<p style=\"background-image:url(https://tracker.test/bg.png)\">x</p>", false)]
    [InlineData("<p style=\"background-image:url(https://tracker.test/bg.png)\">x</p>", true)]
    [InlineData("<p>x</p><img src=\"https://tracker.test/a.gif\">", false)]
    [InlineData("<p>x</p><img src=\"https://tracker.test/a.gif\">", true)]
    [InlineData("<p>x</p><img src=\"cid:unknown@example.test\">", false)]
    [InlineData("<p>x</p><img src=\"cid:unknown@example.test\">", true)]
    public async Task Blocked_or_removed_resources_count_as_own_colours_with_and_without_approval(string html, bool allowRemoteImages)
    {
        await Task.Yield();

        Assert.True(Sanitize(html, allowRemoteImages).HasOwnColors);
        Assert.True(IsLight(BuildDark(html, allowRemoteImages)));
    }

    // Case 5: the verdict, and with it the page, does not jump on "Bilder laden".
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Approving_remote_images_does_not_change_the_page_style()
    {
        await Task.Yield();
        const string html = "<p>x</p><img src=\"https://tracker.test/a.gif\">";

        ReadingDocument before = BuildDark(html, allowRemoteImages: false);
        ReadingDocument after = BuildDark(html, allowRemoteImages: true);

        Assert.True(IsLight(before));
        Assert.True(IsLight(after));
        Assert.Contains("tracker.test", after.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker.test", before.Html, StringComparison.Ordinal);
    }

    // ---- Case 5a --------------------------------------------------------------------------------------------

    // Case 5a: the sanitizer removes <style>, so its rule is gone and our colours apply.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_style_element_is_no_trigger_because_the_sanitizer_removes_it()
    {
        await Task.Yield();
        const string html = "<style>p{color:red}</style><p>body_marker</p>";

        SanitizedHtml sanitized = Sanitize(html);
        ReadingDocument document = BuildDark(html);

        Assert.False(sanitized.HasOwnColors);
        Assert.DoesNotContain("color:red", sanitized.Html, StringComparison.Ordinal);
        Assert.True(IsDark(document));
        Assert.Contains("body_marker", document.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("color:red", document.Html, StringComparison.Ordinal);
    }

    // ---- Case 6 ---------------------------------------------------------------------------------------------

    // Case 6
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<p>color: red</p>")]
    [InlineData("<p>background-color: blue; background-image: url(x)</p>")]
    [InlineData("<p>use style=\"color:red\" in your own mail</p>")]
    [InlineData("<pre>p { color: red; background: #fff }</pre>")]
    public async Task Colour_words_as_visible_text_are_no_trigger(string html)
    {
        await Task.Yield();

        Assert.False(Sanitize(html).HasOwnColors);
        Assert.True(IsDark(BuildDark(html)));
    }

    // Case 6: only real declarations count, not attributes that merely hold the text.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Title_attribute_and_html_comment_holding_a_colour_are_no_trigger()
    {
        await Task.Yield();

        const string titleOnly = "<p title=\"color:red\">x</p>";
        const string commentOnly = "<p>x</p><!-- color:red; background-color:#fff -->";
        const string altOnly = "<p>x</p><a href=\"https://example.test/\" title=\"background-color:red\">y</a>";

        foreach (string html in new[] { titleOnly, commentOnly, altOnly })
        {
            Assert.False(Sanitize(html).HasOwnColors, html);
            Assert.True(IsDark(BuildDark(html)), html);
        }
    }

    // ---- Case 7 ---------------------------------------------------------------------------------------------

    // Case 7
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<p STYLE=\"COLOR : red\">x</p>")]
    [InlineData("<p style=\"Color:Red\">x</p>")]
    [InlineData("<p style=\"  color\t:\tred  \">x</p>")]
    [InlineData("<p style=\"BACKGROUND-COLOR : #FFF\">x</p>")]
    [InlineData("<p style=\"margin:0;color:red\">x</p>")]
    [InlineData("<FONT COLOR=\"red\">x</FONT>")]
    [InlineData("<TABLE BGCOLOR=\"#fff\"><TR><TD>x</TD></TR></TABLE>")]
    public async Task Upper_case_and_spaces_do_not_hide_a_colour(string html)
    {
        await Task.Yield();

        Assert.True(Sanitize(html).HasOwnColors, html);
        Assert.True(IsLight(BuildDark(html)), html);
    }

    // Case 7: a CSS escape in the property name is the same property.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<p style=\"c\\6flor:red\">x</p>")]
    [InlineData("<p style=\"c\\6f lor:red\">x</p>")]
    [InlineData("<p style=\"\\63olor:red\">x</p>")]
    [InlineData("<p style=\"background-c\\6flor:red\">x</p>")]
    public async Task A_css_escape_in_the_property_name_counts_as_a_colour_declaration(string html)
    {
        await Task.Yield();

        Assert.True(Sanitize(html).HasOwnColors, html);
        Assert.True(IsLight(BuildDark(html)), html);
    }

    // ---- Verdict without parse result ----------------------------------------------------------------------

    // D4: unclear counts as own colours. A document the sanitizer reduces to text only has no verdict, so it is light.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Mail_ending_in_the_text_only_path_of_the_sanitizer_has_own_colours()
    {
        await Task.Yield();
        string html = string.Concat(Enumerable.Repeat("<p style=\"margin:0\">t</p>", 20_000)); // no colour at all, but so many style attributes that only text is left

        SanitizedHtml sanitized = Sanitize(html);
        ReadingDocument dark = BuildDark(html);

        Assert.True(sanitized.WasReduced);
        Assert.DoesNotContain("<p", sanitized.Html, StringComparison.Ordinal); // text-only result
        Assert.True(sanitized.HasOwnColors);
        Assert.True(IsLight(dark));
    }

    // ---- Case 9: CSP ---------------------------------------------------------------------------------------

    public static TheoryData<string?, string> CspInputs => new()
    {
        { null, "plain only" },
        { "<p>body_marker</p>", "html without colours" },
        { "<p style=\"color:#000\">body_marker</p>", "html with colours" },
        { "<p>body_marker</p><img src=\"https://tracker.test/a.gif\">", "html with a remote image" },
    };

    // Case 9
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(CspInputs))]
    public async Task The_csp_and_the_image_rule_are_the_same_in_both_themes(string? html, string description)
    {
        await Task.Yield();
        MailContent content = ContentWith(html);

        foreach (bool allowRemote in new[] { false, true })
        {
            foreach (bool allowEmbedded in new[] { false, true })
            {
                ReadingDocument light = ReadingDocumentBuilder.Build(content, allowRemote, allowEmbedded, ReadingTheme.Light);
                ReadingDocument dark = ReadingDocumentBuilder.Build(content, allowRemote, allowEmbedded, ReadingTheme.Dark);

                string lightCsp = Assert.Single(CspValues(light))!;
                string darkCsp = Assert.Single(CspValues(dark))!;
                Assert.True(lightCsp == darkCsp, $"{description}: remote={allowRemote} embedded={allowEmbedded}");
                Assert.Equal(light.BlockedRemoteResourceCount, dark.BlockedRemoteResourceCount);
                Assert.Equal(light.WasReduced, dark.WasReduced);
            }
        }
    }

    // Case 9: remote images still need the approval in the dark theme.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Remote_images_stay_blocked_without_approval_in_the_dark_theme()
    {
        await Task.Yield();
        const string html = "<p>body_marker</p><img src=\"https://tracker.test/a.gif\"><img src=\"https://tracker.test/b.gif\">";

        ReadingDocument blocked = BuildDark(html, allowRemoteImages: false);
        ReadingDocument allowed = BuildDark(html, allowRemoteImages: true);

        Assert.Equal(2, blocked.BlockedRemoteResourceCount);
        Assert.DoesNotContain("tracker.test", blocked.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("http:", Assert.Single(CspValues(blocked))!, StringComparison.Ordinal);
        Assert.DoesNotContain("https:", Assert.Single(CspValues(blocked))!, StringComparison.Ordinal);
        Assert.Equal(0, allowed.BlockedRemoteResourceCount);
        Assert.Contains("tracker.test", allowed.Html, StringComparison.Ordinal);
    }

    // ---- Case 12: hostile mails ----------------------------------------------------------------------------

    // Case 12
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_style_element_trying_to_reach_the_page_style_changes_nothing_and_our_style_comes_first()
    {
        await Task.Yield();
        const string html =
            "<style>html,body{background:#ff0000 !important;color:#00ff00 !important}</style><p>body_marker</p>";

        SanitizedHtml sanitized = Sanitize(html);
        ReadingDocument dark = BuildDark(html);
        ReadingDocument light = ReadingDocumentBuilder.Build(ContentWith(html), allowRemoteImages: false);

        Assert.DoesNotContain("<style", sanitized.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#ff0000", dark.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#00ff00", dark.Html, StringComparison.OrdinalIgnoreCase);
        Assert.True(IsDark(dark));
        Assert.True(IsLight(light));
        Assert.True(dark.Html.IndexOf(DarkSchemeMarker, StringComparison.Ordinal) < dark.Html.IndexOf("body_marker", StringComparison.Ordinal));
        Assert.True(light.Html.IndexOf(LightMarker, StringComparison.Ordinal) < light.Html.IndexOf("body_marker", StringComparison.Ordinal));
        Assert.Single(Parse(dark).QuerySelectorAll("style"));
    }

    // Case 12: a style attribute with colours leaves the light sheet exactly as it is without a theme, and the base style stays first.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_hostile_style_attribute_keeps_the_light_document_unchanged_and_the_base_style_first()
    {
        await Task.Yield();
        const string html =
            "<body style=\"background:#ff0000\"><p style=\"color:#00ff00;position:fixed;top:0;left:0;width:100%;height:100%;background:url(https://tracker.test/x.png)\">body_marker</p></body>";
        MailContent content = ContentWith(html);

        ReadingDocument dark = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Dark);
        ReadingDocument light = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Light);
        ReadingDocument legacy = ReadingDocumentBuilder.Build(content, allowRemoteImages: false);

        Assert.Equal(legacy.Html, light.Html);
        Assert.Equal(light.Html, dark.Html);
        Assert.True(dark.Html.IndexOf(LightMarker, StringComparison.Ordinal) < dark.Html.IndexOf("body_marker", StringComparison.Ordinal));
        Assert.Single(Parse(dark).QuerySelectorAll("style"));
        Assert.DoesNotContain("tracker.test", dark.Html, StringComparison.Ordinal);
    }

    // Case 12: the theme choice does not touch the sanitizer output; only our own constants differ.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_sanitized_body_is_the_same_in_both_themes_and_only_the_page_style_differs()
    {
        await Task.Yield();
        const string html = "<h1>head_marker</h1><p>body_marker <a href=\"https://example.test/p?a=1&amp;b=2\">link</a></p><ul><li>item</li></ul>";
        MailContent content = ContentWith(html);

        IHtmlDocument light = Parse(ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Light));
        IHtmlDocument dark = Parse(ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Dark));

        Assert.Equal(light.Body!.InnerHtml, dark.Body!.InnerHtml);
        Assert.Contains("link", dark.Body.InnerHtml, StringComparison.Ordinal);
        IElement[] metaLight = [.. light.QuerySelectorAll("meta")];
        IElement[] metaDark = [.. dark.QuerySelectorAll("meta")];
        Assert.Equal(metaLight.Select(meta => meta.OuterHtml), metaDark.Select(meta => meta.OuterHtml));
    }

    // ---- Sanitizer verdict API -----------------------------------------------------------------------------

    // D4: the sanitizer verdict is false for plain markup and the record default (a hand-made result) is "has own colours".
    [Fact]
    public void Record_default_of_the_verdict_errs_towards_light()
    {
        var made = new SanitizedHtml("<p>x</p>", 0);

        Assert.True(made.HasOwnColors);
        Assert.False(made.WasReduced);
    }

    // ---- Cases 14, 14a, 15 (fix.1, F1) ---------------------------------------------------------------------

    public static TheoryData<string> RepaintingDeclarations => new()
    {
        "all:initial",
        "filter:brightness(0)",
        "FILTER : invert(1)",
        "mix-blend-mode:multiply",
        "box-shadow:0 0 1px 9999px #fff",
        "border-image:linear-gradient(#fff,#fff) 1 fill",
        "border-image-source:linear-gradient(#fff,#fff)",
        "border-image-slice:1 fill",
        "list-style-image:url(https://images.example.test/a.png)",
        "content:url(https://images.example.test/a.png)",
    };

    // Case 14
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(RepaintingDeclarations))]
    public async Task A_declaration_that_repaints_text_alone_keeps_the_light_sheet(string declaration)
    {
        await Task.Yield();
        string html = $"<p style=\"{declaration}\">body_marker</p>";

        Assert.True(Sanitize(html).HasOwnColors, declaration);
        Assert.True(IsLight(BuildDark(html)), declaration);
    }

    // Case 14a: the allow-list removes it, so it never reaches the output; the page stays dark.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Backdrop_filter_is_dark_and_does_not_reach_the_output()
    {
        await Task.Yield();
        const string html = "<p style=\"backdrop-filter:blur(1px)\">body_marker</p>";

        ReadingDocument document = BuildDark(html);

        Assert.True(IsDark(document));
        Assert.DoesNotContain("backdrop-filter", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("body_marker", document.Html, StringComparison.Ordinal);
    }

    // Case 15
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("border:1px solid #ccc")]
    [InlineData("outline:1px solid #ccc")]
    [InlineData("text-shadow:1px 1px #ccc")]
    [InlineData("border-color:#ccc")]
    [InlineData("border-radius:4px")]
    public async Task Border_outline_and_text_shadow_do_not_make_a_mail_light(string declaration)
    {
        await Task.Yield();
        string html = $"<p style=\"{declaration}\">body_marker</p>";

        Assert.False(Sanitize(html).HasOwnColors, declaration);
        Assert.True(IsDark(BuildDark(html)), declaration);
    }

    // F5: links in <mark> get a readable colour, in the dark style only.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mark_link_rule_is_in_the_dark_style_only()
    {
        await Task.Yield();
        MailContent content = ContentWith("<p><mark>x</mark></p>");

        string dark = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Dark).Html;
        string light = ReadingDocumentBuilder.Build(content, false, true, ReadingTheme.Light).Html;

        Assert.Contains("mark a", dark, StringComparison.Ordinal);
        Assert.DoesNotContain("mark a", light, StringComparison.Ordinal);
    }

    // Case 15 (verify.3): values that paint nothing do not make a mail light.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("list-style:none")]
    [InlineData("list-style:disc")]
    [InlineData("list-style-image:none")]
    [InlineData("content:normal")]
    public async Task Declarations_with_values_that_paint_nothing_are_dark(string declaration)
    {
        await Task.Yield();
        string html = $"<p style=\"{declaration}\">body_marker</p>";

        Assert.False(Sanitize(html).HasOwnColors, declaration);
        Assert.True(IsDark(BuildDark(html)), declaration);
    }

    // Case 14 (verify.3): a url() value still counts; box-shadow:none is an accepted no-op (stays light).
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("list-style-image:url(https://images.example.test/a.png)")]
    [InlineData("list-style:square url(https://images.example.test/a.png)")]
    [InlineData("content:url(https://images.example.test/a.png)")]
    [InlineData("box-shadow:none")]
    public async Task Url_values_and_box_shadow_none_keep_the_light_sheet(string declaration)
    {
        await Task.Yield();
        string html = $"<p style=\"{declaration}\">body_marker</p>";

        Assert.True(Sanitize(html).HasOwnColors, declaration);
        Assert.True(IsLight(BuildDark(html)), declaration);
    }
}
