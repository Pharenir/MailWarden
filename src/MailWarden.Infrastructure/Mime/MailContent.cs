namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// One attachment as listed in the reading pane; its content is never kept or interpreted.
/// </summary>
/// <param name="FileName">File name cleaned for display, never used as a path; empty when the part has no name.</param>
/// <param name="ContentType">MIME type, e.g. <c>application/pdf</c>.</param>
/// <param name="Size">Decoded size in bytes, or null when the part has no decodable content.</param>
public sealed record MailAttachmentInfo(string FileName, string ContentType, long? Size);

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
