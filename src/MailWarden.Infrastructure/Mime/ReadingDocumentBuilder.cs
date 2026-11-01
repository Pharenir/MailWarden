using System.Net;
using System.Text;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// The complete document for the reading pane.
/// </summary>
/// <param name="Html">A complete HTML5 document.</param>
/// <param name="BlockedRemoteResourceCount">Number of blocked remote resources; 0 for plain text.</param>
public sealed record ReadingDocument(string Html, int BlockedRemoteResourceCount);

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
    /// <returns>The document and the number of blocked remote resources.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    public static ReadingDocument Build(MailContent content, bool allowRemoteImages)
    {
        ArgumentNullException.ThrowIfNull(content);

        string bodyContent;
        int blockedCount;
        if (content.Html is null)
        {
            bodyContent = $"<div style=\"white-space: pre-wrap\">{WebUtility.HtmlEncode(content.PlainText)}</div>";
            blockedCount = 0;
        }
        else
        {
            SanitizedHtml sanitized = MailHtmlSanitizer.Sanitize(content.Html, content.InlineImages, allowRemoteImages);
            bodyContent = sanitized.Html;
            blockedCount = sanitized.BlockedRemoteResourceCount;
        }

        var document = new StringBuilder(bodyContent.Length + 512);
        document.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        document.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"");
        document.Append(BuildContentSecurityPolicy(allowRemoteImages));
        document.Append("\"><style>").Append(BaseStyle).Append("</style></head><body>");
        document.Append(bodyContent);
        document.Append("</body></html>");
        return new ReadingDocument(document.ToString(), blockedCount);
    }

    private static string BuildContentSecurityPolicy(bool allowRemoteImages)
    {
        string remoteImageSources = allowRemoteImages ? " http: https:" : string.Empty;
        return $"default-src 'none'; img-src data:{remoteImageSources}; style-src 'unsafe-inline'; font-src data:; "
            + "form-action 'none'; base-uri 'none'";
    }
}
