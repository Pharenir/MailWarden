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
/// Builds the document shown in the reading pane: a page in the style of the theme with a restrictive
/// Content-Security-Policy around the sanitized HTML or the encoded plain text. A mail must always be readable, so only a mail
/// without colors of its own turns dark in the dark theme; a mail with its own colors stays a light sheet in a dark window,
/// its colors are never rewritten (PLAN, risks). The styles are fixed: of the mail, only the yes/no of
/// <see cref="SanitizedHtml.HasOwnColors"/> reaches their selection.
/// </summary>
public static class ReadingDocumentBuilder
{
    private const string LightPageBackground = "#ffffff";
    private const string DarkPageBackground = "#202020";

    private const string CommonStyle =
        "body{margin:12px;font-family:'Segoe UI',sans-serif;font-size:14px;overflow-wrap:anywhere;}"
        + "img{max-width:100%;height:auto;}";

    private const string LightStyle =
        "html,body{background:" + LightPageBackground + ";color:#1a1a1a;}"
        + CommonStyle;

    // The color scheme makes the scrollbars and what else the browser draws itself dark.
    private const string DarkStyle =
        "html{color-scheme:dark;}"
        + "html,body{background:" + DarkPageBackground + ";color:#f0f0f0;}"
        + CommonStyle
        + "a:link,a:visited{color:#8ab4f8;}";

    /// <summary>
    /// Builds the document. With <see cref="MailContent.Html"/> non-null it contains the sanitized HTML, otherwise
    /// <see cref="MailContent.PlainText"/> encoded in a <c>white-space: pre-wrap</c> block.
    /// </summary>
    /// <param name="content">Message content.</param>
    /// <param name="allowRemoteImages">True when the user approved remote images for this message.</param>
    /// <param name="allowEmbeddedImages">False for a mail in Junk or Quarantine that the user has not approved.</param>
    /// <param name="theme">Theme the application shows; it changes nothing but the style of the page.</param>
    /// <returns>The document and the number of blocked resources.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    public static ReadingDocument Build(
        MailContent content,
        bool allowRemoteImages,
        bool allowEmbeddedImages = true,
        ReadingTheme theme = ReadingTheme.Light)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Html is null)
        {
            return BuildFromPlainText(content, theme);
        }

        SanitizedHtml sanitized = MailHtmlSanitizer.Sanitize(content.Html, content.InlineImages, allowRemoteImages, allowEmbeddedImages);
        string pageStyle = theme == ReadingTheme.Dark && !sanitized.HasOwnColors ? DarkStyle : LightStyle;
        return new ReadingDocument(
            Wrap(sanitized.Html, pageStyle, allowRemoteImages, allowEmbeddedImages),
            sanitized.BlockedRemoteResourceCount,
            sanitized.WasReduced);
    }

    /// <summary>
    /// Builds the document from <see cref="MailContent.PlainText"/> only, whatever else the message has. Its cost grows
    /// linearly with the text, so this always works when building from the HTML takes too long.
    /// </summary>
    /// <param name="content">Message content.</param>
    /// <param name="theme">Theme the application shows; plain text has no colors of its own, so it follows the theme.</param>
    /// <returns>The document; without remote resources.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    public static ReadingDocument BuildFromPlainText(MailContent content, ReadingTheme theme = ReadingTheme.Light)
    {
        ArgumentNullException.ThrowIfNull(content);

        string bodyContent = $"<div style=\"white-space: pre-wrap\">{WebUtility.HtmlEncode(content.PlainText)}</div>";
        string pageStyle = theme == ReadingTheme.Dark ? DarkStyle : LightStyle;
        return new ReadingDocument(Wrap(bodyContent, pageStyle, allowRemoteImages: false, allowEmbeddedImages: true), 0);
    }

    /// <summary>
    /// The page background of a theme, for the default background of the browser: while a mail loads it must not differ from
    /// the page. The light sheet of a mail with colors of its own may still differ in the dark theme; it is the mail.
    /// </summary>
    /// <param name="theme">Theme the application shows.</param>
    /// <returns>The channels of the color.</returns>
    public static (byte Red, byte Green, byte Blue) PageBackground(ReadingTheme theme)
    {
        string hexColor = theme == ReadingTheme.Dark ? DarkPageBackground : LightPageBackground;
        byte[] channels = Convert.FromHexString(hexColor.AsSpan(1));
        return (channels[0], channels[1], channels[2]);
    }

    private static string Wrap(string bodyContent, string pageStyle, bool allowRemoteImages, bool allowEmbeddedImages)
    {
        var document = new StringBuilder(bodyContent.Length + 512);
        document.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        document.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"");
        document.Append(BuildContentSecurityPolicy(allowRemoteImages, allowEmbeddedImages));
        document.Append("\"><meta name=\"referrer\" content=\"no-referrer\">");
        document.Append("<style>").Append(pageStyle).Append("</style></head><body>");
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
