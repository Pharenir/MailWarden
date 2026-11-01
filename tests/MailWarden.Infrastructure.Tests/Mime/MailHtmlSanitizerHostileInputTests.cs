using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>
/// AP-04a cases 66-68 (L1, L2): hostile HTML for <see cref="MailHtmlSanitizer"/>. Own class on
/// purpose: heavy inputs can be repeated on their own. Heavy work runs on a pool thread so that the test timeout can fire.
/// Heavy results are checked with a regex (not parsed again) so that the test itself cannot overflow the stack.
/// </summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class MailHtmlSanitizerHostileInputTests
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

    // ---- Case 66 -------------------------------------------------------------------------------------------

    // Case 66: a comment opener inside a raw-text element must not make the pre-scan and the parser disagree.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<script><!--</script>")]
    [InlineData("<style><!--</style>")]
    [InlineData("<title><!--</title>")]
    [InlineData("<textarea><!--</textarea>")]
    [InlineData("<SCRIPT><!--</SCRIPT>")]
    public async Task Comment_opener_inside_a_raw_text_element_is_handled_quickly_and_safely(string opening)
    {
        string html = "<p>before_marker</p>" + opening + Repeat("<div>", 100_000) + "-->";

        SanitizedHtml result = await SanitizeAsync(html);

        AssertNoDangerousElement(result);
        Assert.True(result.WasReduced, "the limiter dropped content, WasReduced must say so");
        Assert.True(result.Html.Length < 10_000_000, $"output has {result.Html.Length} characters");
        Assert.Contains("before_marker", result.Html, StringComparison.Ordinal);
    }

    // ---- Case 67 -------------------------------------------------------------------------------------------

    // Case 67
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Script_with_an_open_attribute_value_followed_by_100000_attributes_is_safe()
    {
        var attributes = new StringBuilder();
        for (int index = 0; index < 100_000; index++)
        {
            attributes.Append(" a").Append(index).Append("=\"x\"");
        }

        string html = "<p>before_marker</p><script><a title=\"</script><div" + attributes + ">after_marker</div>";

        SanitizedHtml result = await SanitizeAsync(html);

        AssertNoDangerousElement(result);
        Assert.True(result.Html.Length < 1_000_000, $"output has {result.Html.Length} characters");
        Assert.Contains("before_marker", result.Html, StringComparison.Ordinal);
    }

    // Case 67
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Script_with_an_open_data_url_attribute_followed_by_100000_divs_is_safe()
    {
        string html = "<p>before_marker</p><script><img src=\"data:</script>" + Repeat("<div>", 100_000) + "\">";

        SanitizedHtml result = await SanitizeAsync(html);

        AssertNoDangerousElement(result);
        Assert.True(result.Html.Length < 10_000_000, $"output has {result.Html.Length} characters");
        Assert.Contains("before_marker", result.Html, StringComparison.Ordinal);
    }

    // Case 67
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("anchors")]
    [InlineData("angle")]
    [InlineData("comments")]
    public async Task Style_with_an_open_attribute_value_followed_by_heavy_filler_is_safe(string filler)
    {
        string fillerText = filler switch
        {
            "anchors" => Repeat("x</a>", 380_000),
            "angle" => Repeat("<1", 900_000),
            _ => Repeat("<!---->", 280_000),
        };
        string html = "<p>before_marker</p><style><a x=\"</style>" + fillerText;

        SanitizedHtml result = await SanitizeAsync(html);

        AssertNoDangerousElement(result);
        Assert.True(result.Html.Length < 10_000_000, $"output has {result.Html.Length} characters");
        Assert.Contains("before_marker", result.Html, StringComparison.Ordinal);
    }

    // ---- Case 68 -------------------------------------------------------------------------------------------

    // Case 68: formatting elements are reconstructed after every paragraph; without a limit this explodes quadratically.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Formatting_reconstruction_bomb_finishes_without_a_large_memory_rise()
    {
        string html = "<p>" + Repeat("<b aI=1>", 1_000) + Repeat("</p><p>x", 18_000);

        (SanitizedHtml Result, long AllocatedBytes) outcome = await Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            SanitizedHtml result = MailHtmlSanitizer.Sanitize(html, new Dictionary<string, string>(), allowRemoteImages: false);
            return (result, GC.GetAllocatedBytesForCurrentThread() - before);
        });

        AssertNoDangerousElement(outcome.Result);
        // Generous guard: the bomb would allocate many gigabytes; a normal pass over this 400 KB input stays far below.
        Assert.True(outcome.AllocatedBytes < 1_500_000_000L, $"allocated {outcome.AllocatedBytes / 1_000_000} MB on the calling thread");
        Assert.True(outcome.Result.Html.Length < 20_000_000, $"output has {outcome.Result.Html.Length} characters");
    }

    // Case 68 / L2: formatting start tags without their end tags are capped per document.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Thousands_of_unclosed_formatting_tags_are_capped()
    {
        string html = Repeat("<b>", 20_000) + Repeat("<i>", 20_000) + "text_marker";

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.True(result.WasReduced);
        Assert.Equal(64, Regex.Matches(result.Html, @"<b[\s>]").Count);
        Assert.Contains("text_marker", result.Html, StringComparison.Ordinal);
    }

    // ---- Case 78 (M2) --------------------------------------------------------------------------------------

    private static string Attributes(int count)
    {
        var attributes = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            attributes.Append(" a").Append(index).Append("=\"x\"");
        }

        return attributes.ToString();
    }

    // Case 78: comment ends the parser accepts (`--!>`, `-->` right after `<!--`) must not hide a tag from the pre-scan.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<!--!><a x=\"-->")]
    [InlineData("<!---!><a x=\"-->")]
    [InlineData("<!--!><a src=\"data:-->")]
    [InlineData("<!---!><a src=\"data:-->")]
    public async Task Comment_end_variants_followed_by_an_open_attribute_value_and_100000_attributes_are_safe(string opening)
    {
        string html = "<p>before_marker</p>" + opening + "<div" + Attributes(100_000) + "\">" + "<script>evil_marker()</script><iframe src=\"x\"></iframe>";

        (SanitizedHtml Result, long AllocatedBytes) outcome = await Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            SanitizedHtml sanitized = MailHtmlSanitizer.Sanitize(html, new Dictionary<string, string>(), allowRemoteImages: false);
            return (sanitized, GC.GetAllocatedBytesForCurrentThread() - before);
        });

        AssertNoDangerousElement(outcome.Result);
        Assert.True(outcome.AllocatedBytes < 1_500_000_000L, $"allocated {outcome.AllocatedBytes / 1_000_000} MB on the calling thread");
        Assert.True(outcome.Result.Html.Length < 2_000_000, $"output has {outcome.Result.Html.Length} characters");
        Assert.Contains("before_marker", outcome.Result.Html, StringComparison.Ordinal);
        Assert.True(outcome.Result.WasReduced, "the pre-scan limit or the parser budget must be reported");
    }

    // ---- Case 79 (M1) --------------------------------------------------------------------------------------

    // Case 79: reconstructed formatting elements (fmtObject, fmtTable) must be counted by the parser budget.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("<object>", "</object>")]
    [InlineData("<table><tr><td>", "</td></tr></table>")]
    public async Task Formatting_reconstruction_around_object_and_table_is_bounded(string open, string close)
    {
        string html = "<div>" + Repeat($"<span><b aI=1>{open}</b>{close}</span>", 1_000) + "</div>" + Repeat("<p>x</p>", 16_000);

        (SanitizedHtml Result, long AllocatedBytes) outcome = await Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            SanitizedHtml result = MailHtmlSanitizer.Sanitize(html, new Dictionary<string, string>(), allowRemoteImages: false);
            return (result, GC.GetAllocatedBytesForCurrentThread() - before);
        });

        AssertNoDangerousElement(outcome.Result);
        Assert.True(outcome.AllocatedBytes < 1_500_000_000L, $"allocated {outcome.AllocatedBytes / 1_000_000} MB on the calling thread");
        Assert.True(outcome.Result.Html.Length < 20_000_000, $"output has {outcome.Result.Html.Length} characters");
        Assert.True(outcome.Result.WasReduced, "the parser budget must have been hit and reported");
    }

    // ---- Case 80 (M1) --------------------------------------------------------------------------------------

    // Case 80 (revised M1): over the pre-scan limits the text stays and the result is marked as reduced.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Input_over_the_pre_scan_limits_keeps_its_text_and_is_marked_as_reduced()
    {
        string html =
            "<p>before_marker</p>" + Repeat("<b>", 200) +
            string.Concat(Enumerable.Range(0, 20_000).Select(index => $"word{index} ")) +
            "<script>evil_marker()</script><iframe src=\"x\"></iframe>";

        SanitizedHtml result = await SanitizeAsync(html);

        AssertNoDangerousElement(result);
        Assert.True(result.WasReduced);
        Assert.Contains("before_marker", result.Html, StringComparison.Ordinal);
        Assert.Contains("word0 ", result.Html, StringComparison.Ordinal);
        Assert.Contains("word19999", result.Html, StringComparison.Ordinal);
    }
}
