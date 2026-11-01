using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>
/// AP-04a cases 69-71 and 73 (L1, L2, L4): positive controls against false alarms, unterminated raw text and flags for <see cref="MailHtmlSanitizer"/>. Own class on
/// purpose: heavy inputs can be repeated on their own. Heavy work runs on a pool thread so that the test timeout can fire.
/// Heavy results are checked with a regex (not parsed again) so that the test itself cannot overflow the stack.
/// </summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class MailHtmlSanitizerControlTests
{
    private const string KnownDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly Regex DangerousElement = new(
        @"<\s*(script|style|iframe|object|embed|svg|math|textarea|title|link|meta|base|noscript|noembed|noframes|xmp|plaintext|listing|form|input|button|select)[\s>/]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // M5: svg and math without an end tag lose only the tag; these swallow the rest of the input.
    public static TheoryData<string> SwallowedWithoutEndTag =>
    [
        "script", "style", "title", "textarea", "iframe", "noembed", "noframes", "noscript",
    ];

    public static TheoryData<string> RawTextElements =>
    [
        "script", "style", "title", "textarea", "iframe", "noembed", "noframes", "noscript", "svg", "math",
    ];

    private static Task<SanitizedHtml> SanitizeAsync(string html, bool allowRemoteImages = false, IReadOnlyDictionary<string, string>? inlineImages = null) =>
        Task.Run(() => MailHtmlSanitizer.Sanitize(html, inlineImages ?? new Dictionary<string, string>(), allowRemoteImages));

    private static string Repeat(string text, int count)
    {
        var builder = new StringBuilder(text.Length * count);
        for (int index = 0; index < count; index++)
        {
            builder.Append(text);
        }

        return builder.ToString();
    }

    private static void AssertNoDangerousElement(SanitizedHtml result) =>
        Assert.False(DangerousElement.IsMatch(result.Html), $"dangerous element survived: {DangerousElement.Match(result.Html).Value}");

    private static IHtmlDocument Parse(SanitizedHtml result) => new HtmlParser().ParseDocument(result.Html);

    // ---- Case 69: positive controls against false alarms ---------------------------------------------------

    // Case 69
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Five_hundred_balanced_formatting_elements_stay_unchanged()
    {
        string html = Repeat("<b>x</b>", 500);

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.False(result.WasReduced);
        Assert.Equal(500, Regex.Matches(result.Html, @"<b[\s>]").Count);
        Assert.Equal(500, Parse(result).QuerySelectorAll("b").Length);
    }

    // Case 69: balanced elements of every counted kind, nested and repeated, stay.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Balanced_elements_of_all_counted_kinds_stay()
    {
        string[] tags = ["a", "b", "big", "code", "em", "font", "i", "nobr", "s", "small", "strike", "strong", "tt", "u"];
        string html = string.Concat(Enumerable.Repeat(string.Concat(tags.Select(tag => $"<{tag}>")) + "x" + string.Concat(tags.Reverse().Select(tag => $"</{tag}>")), 20));

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.False(result.WasReduced);
        Assert.Equal(20, Parse(result).QuerySelectorAll("strong").Length);
        Assert.Equal(20, Parse(result).QuerySelectorAll("u").Length);
    }

    // Case 69
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Comment_with_angle_brackets_inside_a_normal_mail_is_not_a_reduction()
    {
        const string html = "<!-- note: a < b and <i>x</i> and <3 -->" + "<p>text_marker</p>";

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.False(result.WasReduced);
        Assert.Contains("text_marker", Parse(result).Body!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(Parse(result).QuerySelector("p"));
    }

    // Case 69
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Angle_brackets_in_an_attribute_value_are_kept_as_text()
    {
        string html = $"<p>text_marker</p><img src=\"{KnownDataUri}\" alt=\"<b>Logo</b>\">";

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.False(result.WasReduced);
        IHtmlDocument document = Parse(result);
        Assert.Equal("<b>Logo</b>", document.QuerySelector("img")?.GetAttribute("alt"));
        Assert.Empty(document.QuerySelectorAll("b"));
    }

    // Case 69
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Outlook_conditional_comment_is_not_a_reduction()
    {
        const string html = "<!--[if !mso]><!-- --><p>mso_marker</p><!--<![endif]-->";

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.False(result.WasReduced);
        Assert.Contains("mso_marker", Parse(result).Body!.TextContent, StringComparison.Ordinal);
    }

    // Case 69: the typical newsletter structure (tables, links, images, inline styles) is not reduced.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Typical_newsletter_markup_is_not_a_reduction()
    {
        string row = $"<tr><td style=\"color: red\"><a href=\"https://example.test/x\"><img src=\"{KnownDataUri}\" alt=\"i\"></a><p>text <b>bold</b> <i>it</i></p></td></tr>";
        string html = "<table>" + Repeat(row, 200) + "</table>";

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.False(result.WasReduced);
        Assert.Equal(200, Parse(result).QuerySelectorAll("a").Length);
    }

    // Case 69: blocking remote images is the user's choice, not a reduction of the document.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Blocked_remote_images_alone_are_not_a_reduction()
    {
        SanitizedHtml result = await SanitizeAsync("<p>text</p><img src=\"https://tracker.test/a.gif\">");

        Assert.Equal(1, result.BlockedRemoteResourceCount);
        Assert.False(result.WasReduced);
    }

    // ---- Case 70 -------------------------------------------------------------------------------------------

    // Case 70: without an end tag the rest of the input is swallowed, as the parser would do; nothing of it is markup.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(SwallowedWithoutEndTag))]
    public async Task Raw_text_element_without_end_tag_swallows_the_rest(string tag)
    {
        string html = $"<p>before_marker</p><{tag}>alert(1) <b>after_bold</b> <i>after_italic</i> tail_text";

        SanitizedHtml result = await SanitizeAsync(html);

        AssertNoDangerousElement(result);
        Assert.DoesNotContain("<b", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<i>", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("before_marker", result.Html, StringComparison.Ordinal);
    }

    // Case 70: mixed case and an end tag that never matches.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<ScRiPt>x <b>after</b></scrip>")]
    [InlineData("<STYLE>a{} <b>after</b> </styl>")]
    public async Task Mixed_case_raw_text_element_with_a_wrong_end_tag_swallows_the_rest(string opening)
    {
        SanitizedHtml result = await SanitizeAsync("<p>before_marker</p>" + opening + " tail");

        AssertNoDangerousElement(result);
        Assert.DoesNotContain("<b", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    // Case 70 positive control: with a matching end tag the text after the element survives.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(RawTextElements))]
    public async Task Raw_text_element_with_end_tag_keeps_what_follows(string tag)
    {
        string html = $"<p>before_marker</p><{tag}>alert(1)</{tag}><p>after_marker</p>";

        SanitizedHtml result = await SanitizeAsync(html);

        AssertNoDangerousElement(result);
        string text = Parse(result).Body!.TextContent;
        Assert.Contains("before_marker", text, StringComparison.Ordinal);
        Assert.Contains("after_marker", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert(1)", result.Html, StringComparison.Ordinal);
    }

    // ---- Case 71 -------------------------------------------------------------------------------------------

    // Case 71
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("xmp")]
    [InlineData("plaintext")]
    [InlineData("listing")]
    [InlineData("XMP")]
    [InlineData("PlainText")]
    public async Task Xmp_plaintext_and_listing_lose_only_the_tag(string tag)
    {
        string html = $"<p>before_marker</p><{tag}>inner_text_marker</{tag}><p>after_marker</p>";

        SanitizedHtml result = await SanitizeAsync(html);

        AssertNoDangerousElement(result);
        IHtmlDocument document = Parse(result);
        Assert.Empty(document.QuerySelectorAll("xmp"));
        Assert.Empty(document.QuerySelectorAll("plaintext"));
        Assert.Empty(document.QuerySelectorAll("listing"));
        Assert.Contains("inner_text_marker", document.Body!.TextContent, StringComparison.Ordinal);
        Assert.Contains("before_marker", document.Body.TextContent, StringComparison.Ordinal);
    }

    // ---- Case 73 (sanitizer part, L4) ----------------------------------------------------------------------

    // Case 73
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Tag_over_the_attribute_limit_marks_the_result_as_reduced()
    {
        string attributes = string.Concat(Enumerable.Range(0, 300).Select(index => $" a{index}=\"x\""));

        SanitizedHtml result = await SanitizeAsync($"<p>before_marker</p><div{attributes}>inside</div>");

        Assert.True(result.WasReduced);
    }

    // Case 73
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Hitting_the_image_budget_marks_the_result_as_reduced()
    {
        string dataUri = "data:image/png;base64," + Convert.ToBase64String(new byte[1_048_576]);
        var images = new Dictionary<string, string> { ["big@example.test"] = dataUri };
        string html = Repeat("<img src=\"cid:big@example.test\">", 60);

        SanitizedHtml result = await SanitizeAsync(html, inlineImages: images);

        Assert.True(result.WasReduced);
    }

    // Case 73 control: an image well inside the budget does not mark the result.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Images_inside_the_budget_do_not_mark_the_result_as_reduced()
    {
        string dataUri = "data:image/png;base64," + Convert.ToBase64String(new byte[1_048_576]);
        var images = new Dictionary<string, string> { ["big@example.test"] = dataUri };

        SanitizedHtml result = await SanitizeAsync(Repeat("<img src=\"cid:big@example.test\">", 5), inlineImages: images);

        Assert.False(result.WasReduced);
        Assert.Equal(5, Parse(result).QuerySelectorAll("img[src]").Length);
    }

    // Case 73
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Normal_mail_is_not_reduced()
    {
        SanitizedHtml result = await SanitizeAsync("<h1>head</h1><p>text <a href=\"https://example.test/\">link</a></p>");

        Assert.False(result.WasReduced);
    }

    // Case 69 / 73 (planner): exactly 64 open formatting tags stay and are no reduction; the 65th is dropped.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(64, false)]
    [InlineData(65, true)]
    public async Task Sixty_four_open_formatting_tags_stay_and_the_65th_is_dropped(int count, bool expectedReduced)
    {
        SanitizedHtml result = await SanitizeAsync(Repeat("<b>", count) + "text_marker");

        Assert.Equal(expectedReduced, result.WasReduced);
        Assert.Equal(64, Regex.Matches(result.Html, @"<b[\s>]").Count);
        Assert.Contains("text_marker", result.Html, StringComparison.Ordinal);
    }

    // Case 73 positive control (planner): removing raw-text elements such as style and script is not a reduction.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Newsletter_with_style_and_script_blocks_is_not_a_reduction()
    {
        const string html =
            "<style>.a { color: red } /* note: a < b */</style><p class=\"a\">text_marker</p><script>var x = 1 < 2;</script>" +
            "<table><tr><td>cell_marker</td></tr></table>";

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.False(result.WasReduced);
        AssertNoDangerousElement(result);
        string text = Parse(result).Body!.TextContent;
        Assert.Contains("text_marker", text, StringComparison.Ordinal);
        Assert.Contains("cell_marker", text, StringComparison.Ordinal);
    }
    // Case 73 (D3 builder): the document carries the flag of the sanitizer.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reading_document_carries_the_reduction_flag()
    {
        await Task.Yield();
        MailContent normal = Content("<p>text</p>");
        string attributes = string.Concat(Enumerable.Range(0, 300).Select(index => $" a{index}=\"x\""));
        MailContent reduced = Content($"<div{attributes}>inside</div>");
        MailContent plainOnly = Content(null);

        Assert.False(ReadingDocumentBuilder.Build(normal, allowRemoteImages: false).WasReduced);
        Assert.True(ReadingDocumentBuilder.Build(reduced, allowRemoteImages: false).WasReduced);
        Assert.False(ReadingDocumentBuilder.Build(plainOnly, allowRemoteImages: false).WasReduced);
    }

    private static MailContent Content(string? html)
    {
        MimeKit.MimeMessage message = ReadingEml.NewMessage("subject_a");
        message.Body = new MimeKit.TextPart("plain") { Text = "reader text" };
        using MemoryStream stream = ReadingEml.ToStream(message);
        MailContent read = MailContentReader.ReadAsync(stream).GetAwaiter().GetResult();
        return read with { Html = html, PlainText = "plain text" };
    }

    // ---- Case 85 (M5) --------------------------------------------------------------------------------------

    // Case 85: svg and math without a usable end tag lose only the tag; what follows is scanned normally and stays.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<p>A_marker</p><svg/><p>B_marker</p>")]
    [InlineData("<p>A_marker</p><math/><p>B_marker</p>")]
    [InlineData("<p>A_marker</p><svg><p>B_marker</p>")]
    [InlineData("<p>A_marker</p><math><p>B_marker</p>")]
    [InlineData("<p>A_marker</p><SVG/><p>B_marker</p>")]
    public async Task Svg_and_math_without_end_tag_lose_only_the_tag(string html)
    {
        SanitizedHtml result = await SanitizeAsync(html);

        AssertNoDangerousElement(result);
        string text = Parse(result).Body!.TextContent;
        Assert.Contains("A_marker", text, StringComparison.Ordinal);
        Assert.Contains("B_marker", text, StringComparison.Ordinal);
        Assert.False(result.WasReduced);
    }

    // Case 85: markup after an unclosed svg is still sanitized (the content is scanned, not trusted).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Content_after_an_unclosed_svg_is_still_sanitized()
    {
        SanitizedHtml result = await SanitizeAsync("<svg><p>B_marker</p><script>evil()</script><img src=\"file:///c:/x.png\" onerror=\"x()\">");

        AssertNoDangerousElement(result);
        Assert.DoesNotContain("onerror", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("B_marker", Parse(result).Body!.TextContent, StringComparison.Ordinal);
    }

    // Case 85: with an end tag the element goes as before and no notice appears.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Svg_with_end_tag_and_inner_title_is_removed_without_a_notice()
    {
        SanitizedHtml result = await SanitizeAsync("<p>A_marker</p><svg><title>x_inner</title></svg><p>B_marker</p>");

        AssertNoDangerousElement(result);
        string text = Parse(result).Body!.TextContent;
        Assert.Contains("A_marker", text, StringComparison.Ordinal);
        Assert.Contains("B_marker", text, StringComparison.Ordinal);
        Assert.DoesNotContain("x_inner", result.Html, StringComparison.Ordinal);
        Assert.False(result.WasReduced);
    }

    // ---- Case 86 (M5) --------------------------------------------------------------------------------------

    // Case 86: these swallow the rest without an end tag, and that counts as a reduction.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("noscript")]
    [InlineData("textarea")]
    [InlineData("iframe")]
    [InlineData("noembed")]
    [InlineData("noframes")]
    public async Task Unterminated_swallowing_element_keeps_the_text_before_it_and_marks_the_reduction(string tag)
    {
        SanitizedHtml result = await SanitizeAsync($"<p>A_marker</p><{tag}>B_marker");

        AssertNoDangerousElement(result);
        Assert.Contains("A_marker", Parse(result).Body!.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("B_marker", result.Html, StringComparison.Ordinal);
        Assert.True(result.WasReduced);
    }

    // Case 86 positive control: with an end tag the text after the element stays and there is no notice.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("noscript")]
    [InlineData("textarea")]
    [InlineData("iframe")]
    [InlineData("noembed")]
    [InlineData("noframes")]
    [InlineData("svg")]
    [InlineData("math")]
    public async Task Terminated_element_removes_its_content_and_marks_no_reduction(string tag)
    {
        SanitizedHtml result = await SanitizeAsync($"<p>A_marker</p><{tag}>inner_marker</{tag}><p>B_marker</p>");

        AssertNoDangerousElement(result);
        string text = Parse(result).Body!.TextContent;
        Assert.Contains("A_marker", text, StringComparison.Ordinal);
        Assert.Contains("B_marker", text, StringComparison.Ordinal);
        Assert.DoesNotContain("inner_marker", result.Html, StringComparison.Ordinal);
        Assert.False(result.WasReduced);
    }

    // Case 86 / M5: script, style and title never cause a notice, even when they are not terminated.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("script")]
    [InlineData("style")]
    [InlineData("title")]
    public async Task Script_style_and_title_never_cause_a_notice(string tag)
    {
        SanitizedHtml terminated = await SanitizeAsync($"<p>A_marker</p><{tag}>x</{tag}><p>B_marker</p>");
        SanitizedHtml unterminated = await SanitizeAsync($"<p>A_marker</p><{tag}>B_marker");

        Assert.False(terminated.WasReduced);
        Assert.False(unterminated.WasReduced);
        Assert.Contains("A_marker", unterminated.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("B_marker", unterminated.Html, StringComparison.Ordinal);
    }

    // ---- Case 81 (M1, M2): positive controls ---------------------------------------------------------------

    // Case 81
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_and_abruptly_closed_comments_stay_comments_and_hide_nothing()
    {
        const string html =
            "<p>one_marker</p><!----><p>two_marker</p><!--><p>three_marker</p><!----!><p>four_marker</p><!-- text --><p>five_marker</p>";

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.False(result.WasReduced);
        string text = Parse(result).Body!.TextContent;
        foreach (string marker in new[] { "one_marker", "two_marker", "three_marker", "four_marker", "five_marker" })
        {
            Assert.Contains(marker, text, StringComparison.Ordinal);
        }
    }

    // Case 81
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Two_thousand_links_are_not_a_reduction()
    {
        string html = string.Concat(Enumerable.Range(0, 2_000).Select(index => $"<a href=\"https://example.test/{index}\">link_{index}</a> "));

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.False(result.WasReduced);
        Assert.Equal(2_000, Parse(result).QuerySelectorAll("a[href]").Length);
    }

    // Case 81
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Twenty_thousand_ordinary_elements_are_not_a_reduction()
    {
        SanitizedHtml result = await SanitizeAsync(Repeat("<span>x</span>", 20_000));

        Assert.False(result.WasReduced);
        Assert.Equal(20_000, Regex.Matches(result.Html, @"<span[\s>]").Count);
    }

    // Case 81
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Base64_image_over_64_kilobytes_is_not_a_reduction_and_stays()
    {
        string dataUri = "data:image/png;base64," + Convert.ToBase64String(new byte[100_000]);

        SanitizedHtml result = await SanitizeAsync($"<p>text_marker</p><img src=\"{dataUri}\" alt=\"keep_alt\">");

        Assert.False(result.WasReduced);
        Assert.Equal(dataUri, Parse(result).QuerySelector("img")?.GetAttribute("src"));
    }

    // Case 81: a long attribute value is only a problem together with a "<" inside it.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Long_attribute_value_without_angle_brackets_is_not_a_reduction()
    {
        SanitizedHtml result = await SanitizeAsync($"<p title=\"{new string('t', 70_000)}\">text_marker</p>");

        Assert.False(result.WasReduced);
        Assert.Contains("text_marker", Parse(result).Body!.TextContent, StringComparison.Ordinal);
    }

    // ---- Case 90 (M8) --------------------------------------------------------------------------------------

    // Case 90: anchors are closed by the parser itself, so they do not count for the formatting limit.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Link_after_200_unclosed_named_anchors_stays()
    {
        string html = Repeat("<a name=x>", 200) + "<a href=\"https://example.test/\">link_marker</a>";

        SanitizedHtml result = await SanitizeAsync(html);

        IElement? link = Parse(result).QuerySelectorAll("a[href]").FirstOrDefault();
        Assert.NotNull(link);
        Assert.Equal("https://example.test/", link.GetAttribute("href"));
        Assert.Contains("link_marker", link.TextContent, StringComparison.Ordinal);
    }

    // Case 90: the same for nobr.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Text_after_200_unclosed_nobr_elements_stays()
    {
        SanitizedHtml result = await SanitizeAsync(Repeat("<nobr>", 200) + "<a href=\"https://example.test/\">link_marker</a>");

        Assert.NotNull(Parse(result).QuerySelector("a[href]"));
        Assert.Contains("link_marker", Parse(result).Body!.TextContent, StringComparison.Ordinal);
    }
    // ---- Case 92 (N1): positive controls for the parenthesis limit -----------------------------------------

    private static bool IsRed(string styles) =>
        styles.Contains("red", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(styles, @"rgba?\(\s*255\s*,\s*0\s*,\s*0", RegexOptions.CultureInvariant);

    private static string StylesOf(SanitizedHtml result) =>
        string.Concat(Parse(result).All.Select(element => element.GetAttribute("style")).Where(value => value is not null));

    // Case 92
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Ordinary_calc_expression_is_kept()
    {
        SanitizedHtml result = await SanitizeAsync("<div style=\"width: calc(100% - 10px); color: red\">text_marker</div>");

        Assert.False(result.WasReduced);
        string styles = StylesOf(result);
        Assert.Contains("calc(", styles, StringComparison.Ordinal);
        Assert.True(styles.Contains("red", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(styles, @"rgba?\(\s*255\s*,\s*0\s*,\s*0", RegexOptions.CultureInvariant), styles);
    }

    // Case 92
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Style_with_twenty_rgb_functions_is_kept()
    {
        string style = string.Concat(Enumerable.Range(0, 20).Select(index => $"color: rgb({index}, 0, 0); "));

        SanitizedHtml result = await SanitizeAsync($"<p style=\"{style}\">text_marker</p>");

        Assert.False(result.WasReduced);
        Assert.False(string.IsNullOrWhiteSpace(StylesOf(result)), "the style was dropped");
    }

    // Case 92: exactly 256 opening parentheses are allowed, one more is not.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(256, false)]
    [InlineData(257, true)]
    public async Task Exactly_256_opening_parentheses_are_kept_and_257_are_not(int count, bool expectedReduced)
    {
        string style = "color: red; width: calc(" + Repeat("(", count - 1) + "1px" + Repeat(")", count);

        SanitizedHtml result = await SanitizeAsync($"<p style=\"{style}\">text_marker</p>");

        Assert.Equal(expectedReduced, result.WasReduced);
        Assert.Contains("text_marker", Parse(result).Body!.TextContent, StringComparison.Ordinal);
    }

    // ---- Case 94 (N3): positive control for the output-size estimate --------------------------------------

    // Case 94
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Newsletter_with_repeated_attributes_keeps_its_markup()
    {
        string cell = "<td align=\"center\" class=\"cell\" style=\"color: red; padding: 4px\" title=\"A rather ordinary tooltip text that repeats\"><font face=\"Arial\" color=\"red\">cell_text</font></td>";
        string html = "<table>" + Repeat($"<tr>{cell}{cell}{cell}</tr>", 200) + "</table>";

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.False(result.WasReduced);
        Assert.Equal(600, Regex.Matches(result.Html, @"<td[\s>]").Count);
        Assert.Equal(200, Regex.Matches(result.Html, @"<tr[\s>]").Count);
        Assert.Equal(600, Regex.Matches(result.Html, "cell_text").Count);
    }
    // ---- Case 97 (P3): embedded images can be switched off ---------------------------------------------------

    private const string EmbeddedCid = "logo@example.test";

    private static readonly IReadOnlyDictionary<string, string> EmbeddedImages =
        new Dictionary<string, string> { [EmbeddedCid] = KnownDataUri };

    private static string EmbeddedImageMail(string remoteImage) =>
        $"<p>text_marker</p><img src=\"cid:{EmbeddedCid}\"><img src=\"{KnownDataUri}\">" +
        $"<div style=\"background-image: url({KnownDataUri}); color: red\">styled_marker</div>" +
        $"<img src=\"{remoteImage}\">";

    private static Task<SanitizedHtml> SanitizeEmbeddedAsync(string html, bool allowRemoteImages, bool? allowEmbeddedImages) =>
        Task.Run(() => allowEmbeddedImages is { } embedded
            ? MailHtmlSanitizer.Sanitize(html, EmbeddedImages, allowRemoteImages, allowEmbeddedImages: embedded)
            : MailHtmlSanitizer.Sanitize(html, EmbeddedImages, allowRemoteImages));

    // Case 97
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Embedded_images_off_removes_cid_data_css_and_remote_images_and_counts_them()
    {
        SanitizedHtml result = await SanitizeEmbeddedAsync(EmbeddedImageMail("https://tracker.test/a.gif"), allowRemoteImages: false, allowEmbeddedImages: false);

        IHtmlDocument document = Parse(result);
        Assert.DoesNotContain(document.QuerySelectorAll("img"), image => !string.IsNullOrEmpty(image.GetAttribute("src")));
        Assert.DoesNotContain("data:image", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.test", result.Html, StringComparison.Ordinal);
        Assert.Equal(4, result.BlockedRemoteResourceCount);
        string text = document.Body!.TextContent;
        Assert.Contains("text_marker", text, StringComparison.Ordinal);
        Assert.Contains("styled_marker", text, StringComparison.Ordinal);
        Assert.True(IsRed(StylesOf(result)), "the harmless colour of the styled element was lost");
    }

    // Case 97 positive control: with the default the cid and data images stay; only the remote one is counted.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Embedded_images_stay_with_the_default()
    {
        SanitizedHtml result = await SanitizeEmbeddedAsync(EmbeddedImageMail("https://tracker.test/a.gif"), allowRemoteImages: false, allowEmbeddedImages: null);

        IElement[] images = [.. Parse(result).QuerySelectorAll("img[src]")];
        Assert.Equal(2, images.Length);
        Assert.All(images, image => Assert.Equal(KnownDataUri, image.GetAttribute("src")));
        Assert.DoesNotContain("tracker.test", result.Html, StringComparison.Ordinal);
        Assert.Equal(1, result.BlockedRemoteResourceCount);
        Assert.False(result.WasReduced);
    }

    // Case 97 positive control: the explicit true is the default.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Explicit_true_for_embedded_images_equals_the_default()
    {
        SanitizedHtml explicitTrue = await SanitizeEmbeddedAsync(EmbeddedImageMail("https://tracker.test/a.gif"), allowRemoteImages: false, allowEmbeddedImages: true);
        SanitizedHtml defaulted = await SanitizeEmbeddedAsync(EmbeddedImageMail("https://tracker.test/a.gif"), allowRemoteImages: false, allowEmbeddedImages: null);

        Assert.Equal(defaulted.Html, explicitTrue.Html);
        Assert.Equal(defaulted.BlockedRemoteResourceCount, explicitTrue.BlockedRemoteResourceCount);
    }

    // Case 97: with both approvals off the remote image is kept only when remote images are allowed; embedded ones stay removed.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Embedded_images_off_with_remote_images_allowed_keeps_only_the_remote_image()
    {
        SanitizedHtml result = await SanitizeEmbeddedAsync(EmbeddedImageMail("https://tracker.test/a.gif"), allowRemoteImages: true, allowEmbeddedImages: false);

        IElement[] images = [.. Parse(result).QuerySelectorAll("img[src]")];
        Assert.Equal("https://tracker.test/a.gif", Assert.Single(images).GetAttribute("src"));
        Assert.DoesNotContain("data:image", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, result.BlockedRemoteResourceCount);
    }
    // Guards the helper pattern itself so that the other tests cannot pass vacuously.
    [Fact]
    public void Dangerous_element_pattern_matches_what_it_should_and_nothing_else()
    {
        Assert.Matches(DangerousElement, "<SCRIPT src=x>");
        Assert.Matches(DangerousElement, "<title>");
        Assert.DoesNotMatch(DangerousElement, "<p title=\"x\">scripted</p>");
        Assert.DoesNotMatch(DangerousElement, "<b>bold</b><i>it</i>");
    }
}
