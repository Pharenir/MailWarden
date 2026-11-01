using System.Text;
using System.Text.RegularExpressions;
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
    [MemberData(nameof(RawTextElements))]
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
    [InlineData("<SVG><b>after</b> </svgx>")]
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
