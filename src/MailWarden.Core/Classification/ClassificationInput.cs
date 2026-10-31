namespace MailWarden.Core.Classification;

/// <summary>
/// Everything the classifier needs, extracted from the message by the caller (AP-07b, with MimeKit).
/// </summary>
/// <param name="FromAddress">Address of the From header; must contain '@'.</param>
/// <param name="FromName">Display name of the From header (empty when absent).</param>
/// <param name="ReplyToAddress">Address of the Reply-To header, null when absent.</param>
/// <param name="AuthenticationResultsHeaders">Raw Authentication-Results header values, top-most first (may be
/// empty). Only index 0 is used: lower headers can be forged by the sender.</param>
/// <param name="BodyText">Plain text of the body (may be empty).</param>
/// <param name="LinkHosts">Host names of http/https links in the body (may be empty, may contain duplicates).</param>
public sealed record ClassificationInput(
    string FromAddress,
    string FromName,
    string? ReplyToAddress,
    IReadOnlyList<string> AuthenticationResultsHeaders,
    string BodyText,
    IReadOnlyList<string> LinkHosts);
