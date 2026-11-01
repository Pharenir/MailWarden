using System.Text;
using System.Text.RegularExpressions;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>
/// AP-04a case 91 (N1): inline styles with thousands of parentheses recurse in the CSS parser. A stack overflow ends the
/// whole test run - that is the failure signal of this class, so it is kept on its own and can be repeated alone
/// (<c>--filter "FullyQualifiedName~MailHtmlSanitizerCssRecursionTests"</c>).
/// </summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class MailHtmlSanitizerCssRecursionTests
{
    private static readonly Regex ManyParentheses = new(@"(?:\(|&#40){257}", RegexOptions.CultureInvariant);

    private static string Repeat(string text, int count)
    {
        var builder = new StringBuilder(text.Length * count);
        for (int index = 0; index < count; index++)
        {
            builder.Append(text);
        }

        return builder.ToString();
    }

    private static Task<SanitizedHtml> SanitizeAsync(string html) =>
        Task.Run(() => MailHtmlSanitizer.Sanitize(html, new Dictionary<string, string>(), allowRemoteImages: false));

    private static string Div(string styleValue) => $"<p>before_marker</p><div style=\"{styleValue}\">text_marker</div>";

    // Case 91
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(3_500)]
    [InlineData(12_000)]
    public async Task Open_parentheses_in_calc_do_not_end_the_process(int count)
    {
        SanitizedHtml result = await SanitizeAsync(Div("width:calc(" + Repeat("(", count)));

        Assert.True(result.WasReduced);
        Assert.Contains("text_marker", result.Html, StringComparison.Ordinal);
        Assert.Contains("before_marker", result.Html, StringComparison.Ordinal);
        Assert.DoesNotMatch(ManyParentheses, result.Html);
    }

    // Case 91
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(3_500)]
    [InlineData(12_000)]
    public async Task Closed_parentheses_in_calc_do_not_end_the_process(int count)
    {
        SanitizedHtml result = await SanitizeAsync(Div("width:calc(" + Repeat("(", count) + "1px" + Repeat(")", count) + ")"));

        Assert.True(result.WasReduced);
        Assert.Contains("text_marker", result.Html, StringComparison.Ordinal);
        Assert.DoesNotMatch(ManyParentheses, result.Html);
    }

    // Case 91
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(3_500)]
    [InlineData(12_000)]
    public async Task Nested_calc_functions_do_not_end_the_process(int count)
    {
        SanitizedHtml result = await SanitizeAsync(Div("width:" + Repeat("calc(", count) + "1px"));

        Assert.True(result.WasReduced);
        Assert.Contains("text_marker", result.Html, StringComparison.Ordinal);
    }

    // Case 91
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(3_500)]
    [InlineData(12_000)]
    public async Task Comments_between_the_parentheses_do_not_hide_them(int count)
    {
        SanitizedHtml result = await SanitizeAsync(Div("width:calc(" + Repeat("(/**/", count)));

        Assert.True(result.WasReduced);
        Assert.Contains("text_marker", result.Html, StringComparison.Ordinal);
    }

    // Case 91: "&#40" without a semicolon becomes "(" in the parser; the pre-scan must count the ampersands.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(5_000)]
    [InlineData(12_000)]
    public async Task Character_references_for_the_open_parenthesis_do_not_end_the_process(int count)
    {
        SanitizedHtml result = await SanitizeAsync(Div("width:calc(" + Repeat("&#40", count)));

        Assert.True(result.WasReduced);
        Assert.Contains("text_marker", result.Html, StringComparison.Ordinal);
        Assert.DoesNotMatch(ManyParentheses, result.Html);
    }

    // Case 91: the same inputs in a nested position (inside a table cell, behind other styled elements).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_style_is_found_wherever_it_stands()
    {
        string html =
            "<table><tr><td style=\"color: red\"><span style=\"color: red\">" +
            Div("width:calc(" + Repeat("(", 5_000)) + "</span></td></tr></table><p style=\"color: red\">after_marker</p>";

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.True(result.WasReduced);
        Assert.Contains("text_marker", result.Html, StringComparison.Ordinal);
        Assert.Contains("after_marker", result.Html, StringComparison.Ordinal);
    }

    // Case 91 positive control: only the dangerous style is dropped; the harmless styles of the same document survive.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Other_styles_of_the_same_document_survive()
    {
        string html = "<p style=\"color: red\">red_marker</p>" + Div("width:calc(" + Repeat("(", 3_500));

        SanitizedHtml result = await SanitizeAsync(html);

        Assert.True(result.WasReduced);
        Assert.Contains("red_marker", result.Html, StringComparison.Ordinal);
        Assert.True(
            result.Html.Contains("red", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(result.Html, @"style=""[^""]*(red|rgba?\(\s*255\s*,\s*0\s*,\s*0)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "the harmless colour of the other element was lost");
    }
}
