using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>
/// AP-04a cases 51 and 52 (K5, K6): size and run-time limits of <see cref="MailHtmlSanitizer"/>.
/// Own class on purpose: heavy inputs can be repeated on their own.
/// </summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class MailHtmlSanitizerLimitsTests
{
    private const long InlineBudgetCharacters = 34_000_000;

    private static IHtmlDocument Parse(SanitizedHtml result) => new HtmlParser().ParseDocument(result.Html);

    private static SanitizedHtml Sanitize(string html, IReadOnlyDictionary<string, string>? inlineImages = null) =>
        MailHtmlSanitizer.Sanitize(html, inlineImages ?? new Dictionary<string, string>(), allowRemoteImages: false);

    private static string DataUriOfSize(int byteCount) =>
        "data:image/png;base64," + Convert.ToBase64String(new byte[byteCount]);

    private static string RepeatedImages(string cid, int count) =>
        string.Concat(Enumerable.Repeat($"<img src=\"cid:{cid}\">", count));

    // Case 51
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task One_megabyte_image_referenced_300_times_stays_within_the_data_uri_budget()
    {
        await Task.Yield();
        string dataUri = DataUriOfSize(1_048_576);
        var images = new Dictionary<string, string> { ["big@example.test"] = dataUri };
        string html = "<p>keep_marker</p>" + RepeatedImages("big@example.test", 300);

        SanitizedHtml result = Sanitize(html, images);

        IHtmlDocument document = Parse(result);
        long totalCharacters = 0;
        int keptImages = 0;
        foreach (IElement image in document.QuerySelectorAll("img"))
        {
            string? source = image.GetAttribute("src");
            if (!string.IsNullOrEmpty(source))
            {
                totalCharacters += source.Length;
                keptImages++;
            }
        }

        Assert.True(totalCharacters <= InlineBudgetCharacters, $"{totalCharacters} data URI characters");
        Assert.InRange(keptImages, 20, 25);
        Assert.True(keptImages < 300);
        Assert.Contains("keep_marker", document.Body!.TextContent, StringComparison.Ordinal);
        Assert.Equal(dataUri, document.QuerySelector("img[src]")?.GetAttribute("src"));
    }

    // Case 51: references beyond the budget are removed, not cut; the first ones keep their image.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Images_beyond_the_budget_lose_their_source_and_the_first_ones_keep_it()
    {
        await Task.Yield();
        string dataUri = DataUriOfSize(1_048_576);
        var images = new Dictionary<string, string> { ["big@example.test"] = dataUri };

        SanitizedHtml result = Sanitize(RepeatedImages("big@example.test", 60), images);

        IElement[] all = [.. Parse(result).QuerySelectorAll("img")];
        IElement[] withSource = [.. all.Where(image => !string.IsNullOrEmpty(image.GetAttribute("src")))];
        Assert.True(withSource.Length is >= 20 and <= 25, $"{withSource.Length} images kept");
        Assert.All(withSource, image => Assert.Equal(dataUri, image.GetAttribute("src")));
        if (all.Length > withSource.Length)
        {
            Assert.DoesNotContain(all.Skip(withSource.Length), image => !string.IsNullOrEmpty(image.GetAttribute("src")));
        }
    }

    // Case 51 positive control: a small image referenced 300 times is far below the budget and stays everywhere.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Small_image_referenced_300_times_is_kept_everywhere()
    {
        await Task.Yield();
        string dataUri = DataUriOfSize(100);
        var images = new Dictionary<string, string> { ["small@example.test"] = dataUri };

        SanitizedHtml result = Sanitize(RepeatedImages("small@example.test", 300), images);

        Assert.Equal(300, Parse(result).QuerySelectorAll("img[src]").Length);
    }

    // Case 51: images with different ids share one budget per document.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Different_images_share_one_budget()
    {
        await Task.Yield();
        string dataUri = DataUriOfSize(1_048_576);
        var images = Enumerable.Range(0, 60).ToDictionary(index => $"img{index}@example.test", _ => dataUri);
        string html = string.Concat(images.Keys.Select(cid => $"<img src=\"cid:{cid}\">"));

        SanitizedHtml result = Sanitize(html, images);

        long total = Parse(result).QuerySelectorAll("img[src]").Sum(image => (long)image.GetAttribute("src")!.Length);
        Assert.True(total <= InlineBudgetCharacters, $"{total} data URI characters");
        Assert.True(total > 0);
    }

    // Case 51: the budget belongs to one document; a second call starts fresh.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_budget_is_per_document()
    {
        await Task.Yield();
        string dataUri = DataUriOfSize(1_048_576);
        var images = new Dictionary<string, string> { ["big@example.test"] = dataUri };
        string html = RepeatedImages("big@example.test", 30);

        int first = Parse(Sanitize(html, images)).QuerySelectorAll("img[src]").Length;
        int second = Parse(Sanitize(html, images)).QuerySelectorAll("img[src]").Length;

        Assert.True(first > 0);
        Assert.Equal(first, second);
    }

    // Case 52
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Start_tag_with_100000_attributes_is_handled_quickly_and_safely()
    {
        await Task.Yield();
        var attributes = new StringBuilder();
        for (int index = 0; index < 100_000; index++)
        {
            attributes.Append(" a").Append(index).Append("=\"x\"");
        }

        string html = "<p>before_marker</p><div" + attributes + " title=\"late_title\">tag_text_marker</div><p>after_marker</p>";

        var watch = Stopwatch.StartNew();
        SanitizedHtml result = Sanitize(html);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.True(result.Html.Length < 500_000, $"output has {result.Html.Length} characters");
        IHtmlDocument document = Parse(result);
        Assert.All(document.All, element => Assert.True(element.Attributes.Length <= 256, $"{element.TagName} has {element.Attributes.Length} attributes"));
        string text = document.Body!.TextContent;
        Assert.Contains("before_marker", text, StringComparison.Ordinal);
        Assert.Contains("after_marker", text, StringComparison.Ordinal);
    }

    // Case 52
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Style_with_100000_declarations_is_dropped_quickly_and_the_text_stays()
    {
        await Task.Yield();
        string html =
            "<p>before_marker</p><p style=\"" + string.Concat(Enumerable.Repeat("color:red;", 100_000)) + "\">style_text_marker</p>" +
            "<p style=\"color: red\">control_marker</p>";

        var watch = Stopwatch.StartNew();
        SanitizedHtml result = Sanitize(html);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        IHtmlDocument document = Parse(result);
        Assert.All(document.All, element => Assert.True((element.GetAttribute("style") ?? string.Empty).Length <= 65_536));
        string text = document.Body!.TextContent;
        Assert.Contains("style_text_marker", text, StringComparison.Ordinal);
        Assert.Contains("control_marker", text, StringComparison.Ordinal);
        Assert.True(result.Html.Length < 500_000, $"output has {result.Html.Length} characters");
    }

    // Case 52: many tags with many declarations each (the total counts, not only one tag).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Many_moderately_large_tags_finish_quickly()
    {
        await Task.Yield();
        string style = string.Concat(Enumerable.Repeat("color:red;", 200));
        string html = string.Concat(Enumerable.Repeat($"<span style=\"{style}\">s</span>", 2_000));

        var watch = Stopwatch.StartNew();
        SanitizedHtml result = Sanitize(html);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.NotNull(result.Html);
    }

    // Case 52 positive control: a tag with 10 attributes keeps its allowed attributes.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Tag_with_ten_attributes_keeps_its_allowed_attributes()
    {
        await Task.Yield();
        const string html =
            "<p title=\"keep_title\" style=\"color: red\" data-a=\"1\" data-b=\"2\" data-c=\"3\" data-d=\"4\" data-e=\"5\" data-f=\"6\" data-g=\"7\" data-h=\"8\">control_marker</p>";

        SanitizedHtml result = Sanitize(html);

        IElement? paragraph = Parse(result).QuerySelector("p");
        Assert.NotNull(paragraph);
        Assert.Equal("keep_title", paragraph.GetAttribute("title"));
        string style = paragraph.GetAttribute("style") ?? string.Empty;
        Assert.True(
            style.Contains("red", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(style, @"rgba?\(\s*255\s*,\s*0\s*,\s*0", RegexOptions.CultureInvariant),
            style);
        Assert.Contains("control_marker", paragraph.TextContent, StringComparison.Ordinal);
    }

    // Case 52 positive control: exactly 256 attributes are still allowed, 257 are not.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Attribute_limit_is_256_per_tag()
    {
        await Task.Yield();
        static string Tag(int attributeCount) =>
            "<p title=\"keep_title\"" + string.Concat(Enumerable.Range(1, attributeCount - 1).Select(index => $" data-a{index}=\"x\"")) + ">text_marker</p>";

        SanitizedHtml atLimit = Sanitize(Tag(256));
        SanitizedHtml overLimit = Sanitize(Tag(257));

        Assert.Equal("keep_title", Parse(atLimit).QuerySelector("p")?.GetAttribute("title"));
        IElement? over = Parse(overLimit).QuerySelector("p");
        Assert.True(over is null || over.Attributes.Length <= 256);
        Assert.True(over is null || over.GetAttribute("title") is null, "tag with 257 attributes must be dropped");
    }

    // Case 52: a large data: source in src is not a "large style" and stays.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Large_data_uri_in_src_is_not_dropped_by_the_style_size_limit()
    {
        await Task.Yield();
        string dataUri = DataUriOfSize(100_000);

        SanitizedHtml result = Sanitize($"<img src=\"{dataUri}\" alt=\"keep_alt\">");

        Assert.Equal(dataUri, Parse(result).QuerySelector("img")?.GetAttribute("src"));
    }
}
