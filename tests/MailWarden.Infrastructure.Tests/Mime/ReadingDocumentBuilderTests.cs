using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using MailWarden.Infrastructure.Mime;
using MimeKit;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>AP-04a cases 16-17 (D3): <see cref="ReadingDocumentBuilder"/>.</summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
[Trait("Category", "smoke")]
public sealed class ReadingDocumentBuilderTests
{
    private const string CspBlocked =
        "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:; form-action 'none'; base-uri 'none'";

    private const string CspAllowed =
        "default-src 'none'; img-src data: http: https:; style-src 'unsafe-inline'; font-src data:; form-action 'none'; base-uri 'none'";

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

    private static IReadOnlyList<string?> CspValues(IHtmlDocument document) =>
        [.. document.QuerySelectorAll("meta")
            .Where(meta => string.Equals(meta.GetAttribute("http-equiv"), "Content-Security-Policy", StringComparison.OrdinalIgnoreCase))
            .Select(meta => meta.GetAttribute("content"))];

    // Case 16
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_with_images_blocked_gets_the_exact_csp_without_remote_sources()
    {
        await Task.Yield();

        ReadingDocument document = ReadingDocumentBuilder.Build(ContentWith("<p>body_marker</p>"), allowRemoteImages: false);
        IHtmlDocument parsed = Parse(document);

        Assert.Equal(CspBlocked, Assert.Single(CspValues(parsed)));
        Assert.Contains("body_marker", parsed.Body!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(parsed.Body.QuerySelector("p"));
    }

    // Case 16
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_with_images_allowed_gets_the_exact_csp_with_http_and_https_for_images()
    {
        await Task.Yield();

        ReadingDocument document = ReadingDocumentBuilder.Build(ContentWith("<p>body_marker</p>"), allowRemoteImages: true);
        IHtmlDocument parsed = Parse(document);

        Assert.Equal(CspAllowed, Assert.Single(CspValues(parsed)));
        Assert.Contains("body_marker", parsed.Body!.TextContent, StringComparison.Ordinal);
    }

    // Case 16: a CSP or charset meta inside the mail must not replace or add to the document's own.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Meta_tags_of_the_mail_cannot_override_the_csp()
    {
        await Task.Yield();
        const string html =
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src *\"><meta http-equiv=\"refresh\" content=\"0;url=https://evil.test/\"><p>body_marker</p>";

        ReadingDocument document = ReadingDocumentBuilder.Build(ContentWith(html), allowRemoteImages: false);
        IHtmlDocument parsed = Parse(document);

        Assert.Equal(CspBlocked, Assert.Single(CspValues(parsed)));
        Assert.DoesNotContain(
            parsed.QuerySelectorAll("meta"),
            meta => string.Equals(meta.GetAttribute("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase));
    }

    // Case 16
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Document_is_complete_html5_with_utf8_charset_and_a_fixed_light_colour_scheme()
    {
        await Task.Yield();

        ReadingDocument document = ReadingDocumentBuilder.Build(ContentWith("<p>body_marker</p>"), allowRemoteImages: false);
        IHtmlDocument parsed = Parse(document);

        Assert.StartsWith("<!doctype html", document.Html.TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(parsed.Head);
        Assert.NotNull(parsed.Body);
        IElement? charset = parsed.QuerySelectorAll("meta").FirstOrDefault(meta => meta.HasAttribute("charset"));
        Assert.NotNull(charset);
        Assert.Equal("utf-8", charset.GetAttribute("charset"), ignoreCase: true);
        Assert.Contains("background", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color", document.Html, StringComparison.OrdinalIgnoreCase);
    }

    // Case 16
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Dangerous_content_is_sanitised_inside_the_document_and_the_count_comes_from_the_sanitizer()
    {
        await Task.Yield();
        const string html =
            "<p>body_marker</p><script>alert('script_marker')</script><img src=\"https://tracker.test/a.gif\"><img src=\"https://tracker.test/b.gif\">";

        ReadingDocument blocked = ReadingDocumentBuilder.Build(ContentWith(html), allowRemoteImages: false);
        ReadingDocument allowed = ReadingDocumentBuilder.Build(ContentWith(html), allowRemoteImages: true);

        Assert.Equal(2, blocked.BlockedRemoteResourceCount);
        Assert.DoesNotContain("tracker.test", blocked.Html, StringComparison.Ordinal);
        Assert.Equal(0, allowed.BlockedRemoteResourceCount);
        Assert.Equal(2, Parse(allowed).QuerySelectorAll("img[src*='tracker.test']").Length);
        Assert.Empty(Parse(blocked).QuerySelectorAll("script"));
        Assert.DoesNotContain("script_marker", blocked.Html, StringComparison.Ordinal);
    }

    // Case 17
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_with_script_markup_is_encoded_and_not_an_element()
    {
        await Task.Yield();
        const string text = "line one <script>alert('x')</script>\nline two & <b>not bold</b>";

        ReadingDocument document = ReadingDocumentBuilder.Build(ContentWith(null, text), allowRemoteImages: false);
        IHtmlDocument parsed = Parse(document);

        Assert.Empty(parsed.QuerySelectorAll("script"));
        Assert.Empty(parsed.Body!.QuerySelectorAll("b"));
        Assert.Contains("<script>alert('x')</script>", parsed.Body.TextContent, StringComparison.Ordinal);
        Assert.Contains("line two & <b>not bold</b>", parsed.Body.TextContent, StringComparison.Ordinal);
        Assert.Equal(0, document.BlockedRemoteResourceCount);
        Assert.Contains("pre-wrap", document.Html, StringComparison.Ordinal);
        Assert.Equal(CspBlocked, Assert.Single(CspValues(parsed)));
    }

    // Case 60 (K13): no referrer is sent; the CSP stays exactly as before.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Document_contains_the_no_referrer_meta_and_the_unchanged_csp(bool withHtml, bool allowRemoteImages)
    {
        await Task.Yield();
        MailContent content = ContentWith(withHtml ? "<p>body_marker</p>" : null, "plain body");

        ReadingDocument document = ReadingDocumentBuilder.Build(content, allowRemoteImages);
        IHtmlDocument parsed = Parse(document);

        IElement referrer = Assert.Single(parsed.QuerySelectorAll("meta[name]"), meta => string.Equals(meta.GetAttribute("name"), "referrer", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("no-referrer", referrer.GetAttribute("content"));
        // Planner decision (D3 note): a plain-text document has no images and keeps the strict CSP even with approval.
        Assert.Equal(withHtml && allowRemoteImages ? CspAllowed : CspBlocked, Assert.Single(CspValues(parsed)));
    }

    // Case 60: a referrer meta inside the mail cannot weaken the policy.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Referrer_meta_of_the_mail_does_not_replace_the_document_one()
    {
        await Task.Yield();

        ReadingDocument document = ReadingDocumentBuilder.Build(
            ContentWith("<meta name=\"referrer\" content=\"unsafe-url\"><p>body_marker</p>"),
            allowRemoteImages: false);

        IHtmlDocument parsed = Parse(document);
        IElement referrer = Assert.Single(parsed.QuerySelectorAll("meta[name]"), meta => string.Equals(meta.GetAttribute("name"), "referrer", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("no-referrer", referrer.GetAttribute("content"));
        Assert.DoesNotContain("unsafe-url", document.Html, StringComparison.Ordinal);
    }

    // Case 17 (planner decision): the plain-text path keeps the strict CSP after "Bilder laden" - text has no images.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_document_keeps_the_strict_csp_even_with_approved_images()
    {
        await Task.Yield();

        ReadingDocument document = ReadingDocumentBuilder.Build(ContentWith(null, "plain"), allowRemoteImages: true);

        Assert.Equal(CspBlocked, Assert.Single(CspValues(Parse(document))));
    }

    // ---- Case 98 (P3) --------------------------------------------------------------------------------------

    private const string CspNoImages =
        "default-src 'none'; img-src 'none'; style-src 'unsafe-inline'; font-src data:; form-action 'none'; base-uri 'none'";

    private static string ImageMailHtml =>
        "<p>body_marker</p><img src=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==\">" +
        "<img src=\"https://tracker.test/a.gif\">";

    // Case 98
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Embedded_images_off_without_remote_images_gets_the_csp_without_any_image_source()
    {
        await Task.Yield();

        ReadingDocument document = ReadingDocumentBuilder.Build(ContentWith(ImageMailHtml), allowRemoteImages: false, allowEmbeddedImages: false);

        Assert.Equal(CspNoImages, Assert.Single(CspValues(Parse(document))));
        Assert.DoesNotContain("data:image", document.Html, StringComparison.Ordinal);
        Assert.Equal(2, document.BlockedRemoteResourceCount);
        Assert.Contains("body_marker", Parse(document).Body!.TextContent, StringComparison.Ordinal);
    }

    // Case 98
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Embedded_images_off_with_remote_images_allowed_does_not_forbid_images()
    {
        await Task.Yield();

        ReadingDocument document = ReadingDocumentBuilder.Build(ContentWith(ImageMailHtml), allowRemoteImages: true, allowEmbeddedImages: false);

        string csp = Assert.Single(CspValues(Parse(document)))!;
        Assert.DoesNotContain("img-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("http: https:", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", document.Html, StringComparison.Ordinal);
        Assert.Contains("tracker.test", document.Html, StringComparison.Ordinal);
    }

    // Case 98 positive control: both allowed gives the D3 policy with remote sources, and the images are there.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Both_allowed_keeps_the_d3_policy_and_all_images()
    {
        await Task.Yield();

        ReadingDocument document = ReadingDocumentBuilder.Build(ContentWith(ImageMailHtml), allowRemoteImages: true, allowEmbeddedImages: true);

        Assert.Equal(CspAllowed, Assert.Single(CspValues(Parse(document))));
        Assert.Contains("data:image", document.Html, StringComparison.Ordinal);
        Assert.Contains("tracker.test", document.Html, StringComparison.Ordinal);
        Assert.Equal(0, document.BlockedRemoteResourceCount);
    }

    // Case 98 positive control: the default is unchanged D3 (embedded images shown, remote blocked).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Default_keeps_the_d3_policy_for_blocked_remote_images()
    {
        await Task.Yield();

        ReadingDocument document = ReadingDocumentBuilder.Build(ContentWith(ImageMailHtml), allowRemoteImages: false);

        Assert.Equal(CspBlocked, Assert.Single(CspValues(Parse(document))));
        Assert.Contains("data:image", document.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker.test", document.Html, StringComparison.Ordinal);
        Assert.Equal(1, document.BlockedRemoteResourceCount);
    }

    // Case 98: plain-text documents are unaffected by the switch.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_document_is_unaffected_by_the_embedded_images_switch()
    {
        await Task.Yield();

        ReadingDocument off = ReadingDocumentBuilder.Build(ContentWith(null, "plain body"), allowRemoteImages: false, allowEmbeddedImages: false);
        ReadingDocument on = ReadingDocumentBuilder.Build(ContentWith(null, "plain body"), allowRemoteImages: false, allowEmbeddedImages: true);

        Assert.Equal(CspBlocked, Assert.Single(CspValues(Parse(off))));
        Assert.Equal(on.Html, off.Html);
        Assert.Equal(0, off.BlockedRemoteResourceCount);
    }}
