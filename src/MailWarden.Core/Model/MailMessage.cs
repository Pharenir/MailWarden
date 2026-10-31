namespace MailWarden.Core.Model;

/// <summary>
/// Header data of a message as shown in list views. <paramref name="Id"/> == 0 means "not stored yet".
/// </summary>
/// <param name="Id">Database id, 0 before the first store.</param>
/// <param name="AccountId">Owning account.</param>
/// <param name="FolderId">Database id of the folder the message is in.</param>
/// <param name="Uid">IMAP UID, unique only within the folder.</param>
/// <param name="MessageId">Message-ID header, null when absent.</param>
/// <param name="Subject">Decoded subject (empty when absent).</param>
/// <param name="FromAddress">Sender address.</param>
/// <param name="FromName">Sender display name (empty when absent).</param>
/// <param name="Date">Date header including its original offset.</param>
/// <param name="Flags">IMAP system flags.</param>
/// <param name="HasAttachments">True when the message has at least one attachment.</param>
/// <param name="Size">Size of the raw message in bytes.</param>
/// <param name="EmlPath">
/// Path of the EML file relative to <c>DataPaths.MailRoot</c> (forward slashes); null = body not downloaded.
/// </param>
/// <param name="TextPreview">Plain-text start of the body, at most 200 characters (caller's responsibility).</param>
public sealed record MailMessage(
    long Id,
    Guid AccountId,
    long FolderId,
    uint Uid,
    string? MessageId,
    string Subject,
    string FromAddress,
    string FromName,
    DateTimeOffset Date,
    MessageFlags Flags,
    bool HasAttachments,
    long Size,
    string? EmlPath,
    string TextPreview);
