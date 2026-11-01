using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>
/// AP-04a cases 9-15 (D3): <see cref="MailHtmlSanitizer"/>. The result is checked structurally by re-parsing it, never
/// by comparing exact output strings. Every block test has a positive control so that "everything removed" is red.
/// </summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class MailHtmlSanitizerTests
{
    private const string KnownCid = "known_cid@example.test";
    private const string KnownDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly IReadOnlyDictionary<string, string> InlineImages =
        new Dictionary<string, string> { [KnownCid] = KnownDataUri };

    public static TheoryData<string> RemovedElements =>
    [
        "script", "style", "iframe", "frame", "object", "embed", "applet", "input", "button", "select",
        "textarea", "link", "meta", "base", "svg", "math", "video", "audio", "source", "template",
    ];

    private static SanitizedHtml Sanitize(string html, bool allowRemoteImages = false) =>
        MailHtmlSanitizer.Sanitize(html, InlineImages, allowRemoteImages);

    private static IHtmlDocument Parse(SanitizedHtml result) => new HtmlParser().ParseDocument(result.Html);

    private static bool ContainsTag(SanitizedHtml result, string tag) =>
        Regex.IsMatch(result.Html, $@"<\s*{Regex.Escape(tag)}[\s>/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string MixedCase(string text) =>
        string.Concat(text.Select((character, index) => index % 2 == 0 ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character)));

    private static IEnumerable<IElement> AllElements(IHtmlDocument document) => document.All;

    // A harmless colour may be normalised by the sanitizer (red -> rgba(255, 0, 0, 1)); all spellings count.
    private static bool IsRed(string styles) =>
        styles.Contains("red", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(styles, @"rgba?\(\s*255\s*,\s*0\s*,\s*0", RegexOptions.CultureInvariant) ||
        Regex.IsMatch(styles, "#f00|#ff0000", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IReadOnlyList<string> StyleValues(IHtmlDocument document) =>
        [.. AllElements(document).Select(element => element.GetAttribute("style")).Where(value => value is not null).Select(value => value!)];

    // Case 9
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(RemovedElements))]
    public async Task Listed_element_is_removed_and_surrounding_content_stays(string tag)
    {
        await Task.Yield();
        string html = $"<p id=\"keep_p\">keep_marker</p><{tag} id=\"gone\" src=\"x\">inner_marker</{tag}>";

        SanitizedHtml result = Sanitize(html);

        Assert.False(ContainsTag(result, tag), $"<{tag}> survived: {result.Html}");
        Assert.Empty(Parse(result).QuerySelectorAll(tag));
        Assert.Contains("keep_marker", Parse(result).Body!.TextContent, StringComparison.Ordinal);
    }

    // Case 9: mixed-case spelling must not slip through.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(RemovedElements))]
    public async Task Listed_element_is_removed_in_mixed_case(string tag)
    {
        await Task.Yield();
        string spelled = MixedCase(tag);
        string html = $"<P>keep_marker</P><{spelled}>inner_marker</{spelled}>";

        SanitizedHtml result = Sanitize(html);

        Assert.False(ContainsTag(result, tag), $"<{spelled}> survived: {result.Html}");
        Assert.Contains("keep_marker", Parse(result).Body!.TextContent, StringComparison.Ordinal);
    }

    // Case 9: nested deep inside harmless containers and inside a table.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(RemovedElements))]
    public async Task Listed_element_is_removed_when_nested(string tag)
    {
        await Task.Yield();
        string html =
            $"<div><section><p><span>keep_marker</span><b><{tag}>inner_marker</{tag}></b></p></section></div>" +
            $"<table><tr><td><{tag}>cell_marker</{tag}></td></tr></table>";

        SanitizedHtml result = Sanitize(html);

        Assert.False(ContainsTag(result, tag), $"nested <{tag}> survived: {result.Html}");
        Assert.Contains("keep_marker", Parse(result).Body!.TextContent, StringComparison.Ordinal);
    }

    // Case 9: the code inside script and style never ends up as visible text either.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Script_and_style_bodies_do_not_survive_as_text()
    {
        await Task.Yield();

        SanitizedHtml result = Sanitize(
            "<p>keep_marker</p><script>alert('script_marker')</script><style>.a{color:red}/* style_marker */</style>");

        Assert.DoesNotContain("script_marker", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("style_marker", result.Html, StringComparison.Ordinal);
        Assert.Contains("keep_marker", result.Html, StringComparison.Ordinal);
    }

    // Case 56 (K10): the form tag goes, its content stays; input and button inside go.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Form_loses_only_the_tag_and_its_text_stays()
    {
        await Task.Yield();
        const string html =
            "<form action=\"https://evil.test/\" method=\"post\"><p>form_text_marker</p><input name=\"x\" value=\"v\"><button>button_label</button>" +
            "<select><option>o</option></select><textarea>area_text</textarea><b>bold_in_form</b></form>";

        SanitizedHtml result = Sanitize(html);
        IHtmlDocument document = Parse(result);

        Assert.False(ContainsTag(result, "form"), result.Html);
        foreach (string tag in new[] { "input", "button", "select", "textarea" })
        {
            Assert.Empty(document.QuerySelectorAll(tag));
        }

        Assert.DoesNotContain("evil.test", result.Html, StringComparison.Ordinal);
        string text = document.Body!.TextContent;
        Assert.Contains("form_text_marker", text, StringComparison.Ordinal);
        Assert.NotNull(document.QuerySelector("b"));
        Assert.Contains("bold_in_form", text, StringComparison.Ordinal);
    }

    // Case 56: mixed case and nesting; a mail wrapped completely in a form is not empty.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Form_in_mixed_case_and_nested_forms_keep_their_text()
    {
        await Task.Yield();

        SanitizedHtml result = Sanitize("<FoRm><div><FORM><p>wrapped_marker</p></form></div></fORM><p>outside_marker</p>");

        Assert.False(ContainsTag(result, "form"), result.Html);
        string text = Parse(result).Body!.TextContent;
        Assert.Contains("wrapped_marker", text, StringComparison.Ordinal);
        Assert.Contains("outside_marker", text, StringComparison.Ordinal);
    }
    // Case 9 positive control: harmless structure and text formatting stay.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Harmless_elements_and_text_stay()
    {
        await Task.Yield();

        SanitizedHtml result = Sanitize("<h1>head_marker</h1><p>para <b>bold_marker</b> <i>italic_marker</i></p><ul><li>item_marker</li></ul><table><tr><td>cell_marker</td></tr></table>");
        IHtmlDocument document = Parse(result);

        Assert.NotNull(document.QuerySelector("h1"));
        Assert.NotNull(document.QuerySelector("b"));
        Assert.NotNull(document.QuerySelector("li"));
        Assert.NotNull(document.QuerySelector("td"));
        string text = document.Body!.TextContent;
        foreach (string marker in new[] { "head_marker", "bold_marker", "italic_marker", "item_marker", "cell_marker" })
        {
            Assert.Contains(marker, text, StringComparison.Ordinal);
        }
    }

    // Case 10
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Event_handler_and_blocked_attributes_are_removed_on_every_element()
    {
        await Task.Yield();
        const string html =
            "<div onclick=\"x()\" ONMOUSEOVER=\"y()\" OnLoAd=\"z()\" target=\"_blank\" background=\"a.png\" class=\"keep_class\">div_marker</div>" +
            "<img src=\"data:image/png;base64,AAAA\" onerror=\"x()\" srcset=\"a.png 1x, b.png 2x\" alt=\"keep_alt\">" +
            "<a href=\"https://example.test/\" target=\"_blank\" ping=\"https://tracker.test/p\" onclick=\"x()\">link_marker</a>" +
            "<table background=\"a.png\"><tr><td background=\"b.png\" onfocus=\"x()\">cell_marker</td></tr></table>" +
            "<p formaction=\"https://evil.test/\" srcdoc=\"<b>x</b>\" onmouseenter=\"x()\">para_marker</p>";

        SanitizedHtml result = Sanitize(html);
        IHtmlDocument document = Parse(result);

        string[] forbidden = ["target", "srcset", "srcdoc", "background", "ping", "formaction"];
        foreach (IElement element in AllElements(document))
        {
            foreach (IAttr attribute in element.Attributes)
            {
                Assert.False(attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase), $"{element.TagName}@{attribute.Name} survived");
                Assert.DoesNotContain(attribute.Name, forbidden, StringComparer.OrdinalIgnoreCase);
            }
        }

        // Positive controls: harmless attributes and the elements themselves stay.
        Assert.Equal("keep_class", document.QuerySelector("div")?.GetAttribute("class"));
        Assert.Equal("https://example.test/", document.QuerySelector("a")?.GetAttribute("href"));
        Assert.Equal("keep_alt", document.QuerySelector("img")?.GetAttribute("alt"));
        Assert.Contains("link_marker", document.Body!.TextContent, StringComparison.Ordinal);
        Assert.Contains("para_marker", document.Body.TextContent, StringComparison.Ordinal);
    }

    // Case 11
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("http://example.test/a")]
    [InlineData("https://example.test/a?x=1")]
    [InlineData("mailto:recipient_b@example.test")]
    [InlineData("HtTpS://example.test/upper")]
    public async Task Allowed_link_schemes_keep_the_href(string href)
    {
        await Task.Yield();

        SanitizedHtml result = Sanitize($"<a href=\"{href}\">link_marker</a>");
        IElement? anchor = Parse(result).QuerySelector("a");

        Assert.NotNull(anchor);
        string? kept = anchor.GetAttribute("href");
        Assert.NotNull(kept);
        Assert.Equal(href, kept, ignoreCase: true);
        Assert.Contains("link_marker", anchor.TextContent, StringComparison.Ordinal);
    }

    // Case 11
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("  JaVaScRiPt:alert(1)")]
    [InlineData("java&#9;script:alert(1)")]
    [InlineData("java&#10;script:alert(1)")]
    [InlineData("&#9;javascript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("data:text/html;base64,PGI+eDwvYj4=")]
    [InlineData("file:///c:/secret.txt")]
    [InlineData("/relative/path")]
    [InlineData("relative.html")]
    [InlineData("//evil.test/protocol-relative")]
    [InlineData("#fragment")]
    [InlineData("ftp://example.test/file")]
    public async Task Other_link_schemes_lose_the_href_and_keep_the_text(string href)
    {
        await Task.Yield();

        SanitizedHtml result = Sanitize($"<a href=\"{href}\">link_marker</a>");
        IHtmlDocument document = Parse(result);

        Assert.Empty(document.QuerySelectorAll("[href]"));
        Assert.Contains("link_marker", document.Body!.TextContent, StringComparison.Ordinal);
    }

    // Case 12
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Image_with_known_cid_gets_the_data_uri_and_unknown_cid_is_removed()
    {
        await Task.Yield();

        SanitizedHtml known = Sanitize($"<p>keep_marker</p><img src=\"cid:{KnownCid}\">");
        SanitizedHtml unknown = Sanitize("<p>keep_marker</p><img src=\"cid:unknown_cid@example.test\">");

        Assert.Equal(KnownDataUri, Parse(known).QuerySelector("img")?.GetAttribute("src"));
        Assert.Equal(0, known.BlockedRemoteResourceCount);
        Assert.DoesNotContain("cid:", unknown.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Parse(unknown).QuerySelectorAll("img"));
        Assert.Equal(0, unknown.BlockedRemoteResourceCount);
    }

    // Case 12
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public async Task Data_uri_of_an_allowed_image_type_stays(string mediaType)
    {
        await Task.Yield();
        string source = $"data:{mediaType};base64,AAAA";

        SanitizedHtml result = Sanitize($"<img src=\"{source}\">");

        Assert.Equal(source, Parse(result).QuerySelector("img")?.GetAttribute("src"));
        Assert.Equal(0, result.BlockedRemoteResourceCount);
    }

    // Case 12
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("data:image/svg+xml;base64,PHN2Zy8+")]
    [InlineData("data:image/svg+xml,<svg/>")]
    [InlineData("data:text/html;base64,PGI+eDwvYj4=")]
    [InlineData("data:application/octet-stream;base64,AAAA")]
    [InlineData("file:///c:/secret.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.test/a.png")]
    [InlineData("/relative/a.png")]
    public async Task Disallowed_image_sources_are_removed_and_not_counted(string source)
    {
        await Task.Yield();

        SanitizedHtml result = Sanitize($"<p>keep_marker</p><img src=\"{source}\" alt=\"alt_marker\">");
        IHtmlDocument document = Parse(result);

        Assert.DoesNotContain(document.QuerySelectorAll("img"), image => !string.IsNullOrEmpty(image.GetAttribute("src")));
        Assert.Equal(0, result.BlockedRemoteResourceCount);
        Assert.Contains("keep_marker", document.Body!.TextContent, StringComparison.Ordinal);
    }

    // Case 12 + 13
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("https://tracker.test/pixel.gif")]
    [InlineData("http://tracker.test/pixel.gif")]
    public async Task Remote_image_is_removed_and_counted_when_blocked_and_kept_when_allowed(string source)
    {
        await Task.Yield();
        string html = $"<p>keep_marker</p><img src=\"{source}\">";

        SanitizedHtml blocked = Sanitize(html, allowRemoteImages: false);
        SanitizedHtml allowed = Sanitize(html, allowRemoteImages: true);

        Assert.DoesNotContain(Parse(blocked).QuerySelectorAll("img"), image => image.HasAttribute("src"));
        Assert.DoesNotContain("tracker.test", blocked.Html, StringComparison.Ordinal);
        Assert.Equal(1, blocked.BlockedRemoteResourceCount);
        Assert.Equal(source, Parse(allowed).QuerySelector("img")?.GetAttribute("src"));
        Assert.Equal(0, allowed.BlockedRemoteResourceCount);
    }

    // Case 13
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Three_remote_images_are_counted_when_blocked_and_kept_when_allowed()
    {
        await Task.Yield();
        const string html =
            "<img src=\"https://tracker.test/a.gif\"><img src=\"https://tracker.test/b.gif\"><img src=\"http://tracker.test/c.gif\">" +
            $"<img src=\"cid:{KnownCid}\">";

        SanitizedHtml blocked = Sanitize(html, allowRemoteImages: false);
        SanitizedHtml allowed = Sanitize(html, allowRemoteImages: true);

        Assert.Equal(3, blocked.BlockedRemoteResourceCount);
        Assert.DoesNotContain("tracker.test", blocked.Html, StringComparison.Ordinal);
        Assert.Equal(KnownDataUri, Assert.Single(Parse(blocked).QuerySelectorAll("img[src]")).GetAttribute("src"));
        Assert.Equal(0, allowed.BlockedRemoteResourceCount);
        Assert.Equal(4, Parse(allowed).QuerySelectorAll("img[src]").Length);
        Assert.Equal(3, Parse(allowed).QuerySelectorAll("img[src*='tracker.test']").Length);
    }

    // Case 14
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Remote_css_url_follows_the_remote_image_rule_and_count()
    {
        await Task.Yield();
        const string html = "<div style=\"background-image: url(https://tracker.test/bg.gif); color: red\">style_marker</div>";

        SanitizedHtml blocked = Sanitize(html, allowRemoteImages: false);
        SanitizedHtml allowed = Sanitize(html, allowRemoteImages: true);

        Assert.DoesNotContain("tracker.test", blocked.Html, StringComparison.Ordinal);
        Assert.Equal(1, blocked.BlockedRemoteResourceCount);
        Assert.True(IsRed(string.Concat(StyleValues(Parse(blocked)))), "colour lost");
        Assert.Contains("style_marker", Parse(blocked).Body!.TextContent, StringComparison.Ordinal);
        Assert.Contains("tracker.test", string.Concat(StyleValues(Parse(allowed))), StringComparison.Ordinal);
        Assert.Equal(0, allowed.BlockedRemoteResourceCount);
    }

    // Case 14
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("width: expression(alert(1)); color: red", "expression")]
    [InlineData("WIDTH: ExPrEsSiOn(alert(1)); COLOR: red", "expression")]
    [InlineData("behavior: url(x.htc); color: red", "behavior")]
    [InlineData("BeHaViOr: url(x.htc); color: red", "behavior")]
    [InlineData("-moz-binding: url(x.xml#y); color: red", "binding")]
    [InlineData("position: fixed; top: 0; color: red", "fixed")]
    [InlineData("POSITION : FIXED; color: red", "fixed")]
    [InlineData("background-image: url(file:///c:/secret.png); color: red", "file:")]
    [InlineData("background-image: url(javascript:alert(1)); color: red", "javascript")]
    [InlineData("background-image: url(data:text/html;base64,AAAA); color: red", "text/html")]
    [InlineData("background-image: url(cid:known_cid@example.test); color: red", "cid:")]
    public async Task Dangerous_inline_style_parts_are_removed_and_the_harmless_colour_stays(string style, string forbiddenText)
    {
        await Task.Yield();

        SanitizedHtml result = Sanitize($"<p style=\"{style}\">style_marker</p>");
        IHtmlDocument document = Parse(result);
        string styles = string.Concat(StyleValues(document));

        Assert.DoesNotContain(forbiddenText, styles, StringComparison.OrdinalIgnoreCase);
        Assert.True(IsRed(styles), styles);
        Assert.Contains("style_marker", document.Body!.TextContent, StringComparison.Ordinal);
        Assert.Equal(0, result.BlockedRemoteResourceCount);
    }

    // Case 14 positive control: absolute positioning is not "fixed" and plain colour/size declarations stay.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Harmless_inline_style_stays()
    {
        await Task.Yield();

        SanitizedHtml result = Sanitize("<p style=\"color: red; font-size: 14px\">style_marker</p>");

        string styles = string.Concat(StyleValues(Parse(result)));
        Assert.True(IsRed(styles), styles);
        Assert.Contains("14px", styles, StringComparison.OrdinalIgnoreCase);
    }

    // Case 15
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<")]
    [InlineData("<<>>")]
    [InlineData("<p")]
    [InlineData("<p class=\"x")]
    [InlineData("<a href=\"https://example.test/")]
    [InlineData("<img src=")]
    [InlineData("<div><span><b>unclosed")]
    [InlineData("</div></p></span>")]
    [InlineData("<!-- unterminated comment")]
    [InlineData("<![CDATA[ x ]]>")]
    [InlineData("<script>alert(1)")]
    [InlineData("<style>a{")]
    [InlineData("<table><td><tr><div></table>")]
    [InlineData("&#xFFFFFFFF; &#0; &bogus; &amp")]
    [InlineData("<p style=\"background: url(\">x</p>")]
    [InlineData("<p style=\"((((((((((\">x</p>")]
    public async Task Malformed_input_never_throws(string html)
    {
        await Task.Yield();

        SanitizedHtml result = Sanitize(html);

        Assert.NotNull(result.Html);
        Assert.True(result.BlockedRemoteResourceCount >= 0);
    }

    // Case 15
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Control_characters_and_a_null_character_never_throw()
    {
        await Task.Yield();

        SanitizedHtml result = Sanitize("<p\0 class=\"a\u0001b\">text\0</p>");

        Assert.NotNull(result.Html);
    }

    // Case 15
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Very_deep_nesting_of_five_thousand_levels_never_throws(bool closed)
    {
        await Task.Yield();
        const int depth = 5000;
        string html = string.Concat(Enumerable.Repeat("<div>", depth)) + "deep_marker" + (closed ? string.Concat(Enumerable.Repeat("</div>", depth)) : string.Empty);

        SanitizedHtml result = Sanitize(html);

        Assert.NotNull(result.Html);
    }

    // Case 15 (D3): null is the only thing that throws.
    [Fact]
    public void Null_html_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => MailHtmlSanitizer.Sanitize(null!, InlineImages, allowRemoteImages: false));
    }

    // Case 15: an empty inline image map is a normal call.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_inline_image_map_makes_every_cid_unknown()
    {
        await Task.Yield();

        SanitizedHtml result = MailHtmlSanitizer.Sanitize(
            $"<p>keep_marker</p><img src=\"cid:{KnownCid}\">",
            new Dictionary<string, string>(),
            allowRemoteImages: true);

        Assert.Empty(Parse(result).QuerySelectorAll("img"));
        Assert.Contains("keep_marker", result.Html, StringComparison.Ordinal);
    }

    // ---- AP-04b Y-a: a mail must not place its own text into the link hint, so title is gone ---------------

    // Y-a: title on the link is removed, href stays.
    [Fact]
    public void Y_a_title_on_a_link_is_removed_and_the_href_stays()
    {
        SanitizedHtml result = Sanitize("<p><a href=\"https://evil.example.test/\" title=\"https://www.bank.example.test/login\">Text_marker</a></p>");

        IElement link = Parse(result).QuerySelector("a")!;
        Assert.False(link.HasAttribute("title"));
        Assert.Equal("https://evil.example.test/", link.GetAttribute("href"));
        Assert.Equal("Text_marker", link.TextContent);
        Assert.DoesNotContain("bank.example.test", result.Html, StringComparison.Ordinal);
    }

    // Y-a: title on an element inside the link is removed too.
    [Fact]
    public void Y_a_title_on_a_span_inside_a_link_is_removed()
    {
        SanitizedHtml result = Sanitize("<a href=\"https://evil.example.test/\"><span title=\"https://www.bank.example.test/login\">Text_marker</span></a>");

        IHtmlDocument document = Parse(result);
        Assert.Equal("https://evil.example.test/", document.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("Text_marker", document.QuerySelector("span")!.TextContent);
        Assert.Empty(document.QuerySelectorAll("[title]"));
        Assert.DoesNotContain("bank.example.test", result.Html, StringComparison.Ordinal);
    }

    // Y-a: title on any element and in any case is gone; the text stays.
    [Theory]
    [InlineData("<p title=\"t_marker\" class=\"keep_class\">body_marker</p>")]
    [InlineData("<div TITLE=\"t_marker\" class=\"keep_class\">body_marker</div>")]
    [InlineData("<table><tr><td title=\"t_marker\" class=\"keep_class\">body_marker</td></tr></table>")]
    public void Y_a_title_is_removed_on_every_element(string html)
    {
        SanitizedHtml result = Sanitize(html);

        IHtmlDocument document = Parse(result);
        Assert.Empty(document.QuerySelectorAll("[title]"));
        Assert.DoesNotContain("t_marker", result.Html, StringComparison.Ordinal);
        Assert.Contains("body_marker", result.Html, StringComparison.Ordinal);
    }
}
