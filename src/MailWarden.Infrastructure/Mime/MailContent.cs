namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// One attachment as listed in the reading pane; its content is never kept or interpreted.
/// </summary>
/// <param name="FileName">
/// File name cleaned for display, not cut; empty when the part has no name. It is foreign data: never a path, and a file on
/// disk only gets a name made from it by <c>AttachmentFileName.ToSafe</c>.
/// </param>
/// <param name="ContentType">MIME type, e.g. <c>application/pdf</c>.</param>
/// <param name="Size">Decoded size in bytes, or null when the part has no decodable content.</param>
public sealed record MailAttachmentInfo(string FileName, string ContentType, long? Size)
{
    /// <summary>
    /// True for an attached message. A listed <c>message/rfc822</c> always is one: the parser leaves that type unlisted when it could
    /// not make a message of it (encoded, or at the depth limit). The other types of a message have no decoded size, unlike parts such
    /// as a delivery report, which are measured.
    /// </summary>
    public bool IsAttachedMessage =>
        ContentType.Equals("message/rfc822", StringComparison.OrdinalIgnoreCase)
        || (Size is null
            && (ContentType.StartsWith("message/", StringComparison.OrdinalIgnoreCase)
                || ContentType.Equals("text/rfc822-headers", StringComparison.OrdinalIgnoreCase)));

    /// <summary>True when the part can be copied: it has decodable content, or it is an attached message.</summary>
    public bool HasContent => Size is not null || IsAttachedMessage;
}

/// <summary>
/// One mailbox of the From header.
/// </summary>
/// <param name="Name">Display name cleaned for display; empty when absent.</param>
/// <param name="Address">Address cleaned for display.</param>
public sealed record MailSender(string Name, string Address);

/// <summary>
/// Content of a parsed message for the reading pane, completely in memory.
/// </summary>
/// <param name="Subject">Subject cleaned for display; empty when absent.</param>
/// <param name="From">Senders as "Name &lt;address&gt;" or only the address, cleaned for display.</param>
/// <param name="To">To recipients, each like <paramref name="From"/>.</param>
/// <param name="Cc">Cc recipients, each like <paramref name="From"/>.</param>
/// <param name="Date">Date header, or null when absent.</param>
/// <param name="Html">The raw HTML body, null when there is none. Only for the sanitizer, never displayed directly.</param>
/// <param name="PlainText">Text body; when only HTML exists, the text extracted from the HTML.</param>
/// <param name="InlineImages">Content-ID without angle brackets → <c>data:</c> URI.</param>
/// <param name="Attachments">Non-inline parts and inline parts that are not in <paramref name="InlineImages"/>.</param>
/// <param name="Senders">
/// The parsed mailboxes of the From header, so nothing has to search in the joined text of <paramref name="From"/>, which a
/// sender controls. Null when the content was not made from a parsed message.
/// </param>
/// <param name="HasOpaqueParts">
/// True when a part of type <c>multipart/*</c> or <c>message/*</c> could not be parsed further because of the depth limit;
/// what is below it cannot be shown.
/// </param>
public sealed record MailContent(
    string Subject,
    string From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    DateTimeOffset? Date,
    string? Html,
    string PlainText,
    IReadOnlyDictionary<string, string> InlineImages,
    IReadOnlyList<MailAttachmentInfo> Attachments,
    IReadOnlyList<MailSender>? Senders = null,
    bool HasOpaqueParts = false);
