using System.Net;
using System.Text;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// The complete document for the reading pane.
/// </summary>
/// <param name="Html">A complete HTML5 document.</param>
/// <param name="BlockedRemoteResourceCount">Number of blocked remote resources; 0 for plain text.</param>
/// <param name="WasReduced">True when a limit for crafted input dropped markup or the inline image budget was hit.</param>
public sealed record ReadingDocument(string Html, int BlockedRemoteResourceCount, bool WasReduced = false);

/// <summary>
/// Builds the document shown in the reading pane: a light page with a restrictive Content-Security-Policy around the
/// sanitized HTML or the encoded plain text. Mail content is never rendered dark (PLAN, risks).
/// </summary>
public static class ReadingDocumentBuilder
{
    private const string BaseStyle =
        "html,body{background:#ffffff;color:#1a1a1a;}"
        + "body{margin:12px;font-family:'Segoe UI',sans-serif;font-size:14px;overflow-wrap:anywhere;}"
        + "img{max-width:100%;height:auto;}";

    /// <summary>
    /// Builds the document. With <see cref="MailContent.Html"/> non-null it contains the sanitized HTML, otherwise
    /// <see cref="MailContent.PlainText"/> encoded in a <c>white-space: pre-wrap</c> block.
    /// </summary>
    /// <param name="content">Message content.</param>
    /// <param name="allowRemoteImages">True when the user approved remote images for this message.</param>
    /// <param name="allowEmbeddedImages">False for a mail in Junk or Quarantine that the user has not approved.</param>
    /// <returns>The document and the number of blocked resources.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    public static ReadingDocument Build(MailContent content, bool allowRemoteImages, bool allowEmbeddedImages = true)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Html is null)
        {
            return BuildFromPlainText(content);
        }

        SanitizedHtml sanitized = MailHtmlSanitizer.Sanitize(content.Html, content.InlineImages, allowRemoteImages, allowEmbeddedImages);
        return new ReadingDocument(
            Wrap(sanitized.Html, allowRemoteImages, allowEmbeddedImages),
            sanitized.BlockedRemoteResourceCount,
            sanitized.WasReduced);
    }

    /// <summary>
    /// Builds the document from <see cref="MailContent.PlainText"/> only, whatever else the message has. Its cost grows
    /// linearly with the text, so this always works when building from the HTML takes too long.
    /// </summary>
    /// <param name="content">Message content.</param>
    /// <returns>The document; without remote resources.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    public static ReadingDocument BuildFromPlainText(MailContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string bodyContent = $"<div style=\"white-space: pre-wrap\">{WebUtility.HtmlEncode(content.PlainText)}</div>";
        return new ReadingDocument(Wrap(bodyContent, allowRemoteImages: false, allowEmbeddedImages: true), 0);
    }

    private static string Wrap(string bodyContent, bool allowRemoteImages, bool allowEmbeddedImages)
    {
        var document = new StringBuilder(bodyContent.Length + 512);
        document.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        document.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"");
        document.Append(BuildContentSecurityPolicy(allowRemoteImages, allowEmbeddedImages));
        document.Append("\"><meta name=\"referrer\" content=\"no-referrer\">");
        document.Append("<style>").Append(BaseStyle).Append("</style></head><body>");
        document.Append(bodyContent);
        document.Append("</body></html>");
        return document.ToString();
    }

    private static string BuildContentSecurityPolicy(bool allowRemoteImages, bool allowEmbeddedImages)
    {
        string imageSources = (allowEmbeddedImages, allowRemoteImages) switch
        {
            (true, true) => "data: http: https:",
            (true, false) => "data:",
            (false, true) => "http: https:",
            (false, false) => "'none'",
        };
        return $"default-src 'none'; img-src {imageSources}; style-src 'unsafe-inline'; font-src data:; "
            + "form-action 'none'; base-uri 'none'";
    }
}
