using System.Collections.Frozen;
using MailWarden.Core.Text;
using MimeKit;
using MimeKit.IO;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// Parses a raw message into <see cref="MailContent"/> for the reading pane. Nothing refers to the stream afterwards, so
/// the sync can overwrite the EML file while the message is displayed.
/// </summary>
public static class MailContentReader
{
    // Limits for embedding inline images into the reading document (binary megabytes).
    private const long MaxInlineImageBytes = 5L * 1024 * 1024;
    private const long MaxInlineImagesTotalBytes = 25L * 1024 * 1024;

    private static readonly FrozenSet<string> InlineImageTypes =
        new[] { "image/png", "image/jpeg", "image/gif", "image/webp" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a complete EML from the stream into memory; the stream is not disposed. Afterwards nothing refers to it.
    /// </summary>
    /// <param name="eml">Raw message, read from its current position.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The content of the message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="eml"/> is null.</exception>
    /// <exception cref="MailContentException">The stream cannot be parsed as a message, e.g. it is empty.</exception>
    /// <exception cref="IOException">Reading the stream failed; passed on unchanged.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static async Task<MailContent> ReadAsync(Stream eml, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(eml);

        try
        {
            // persistent: false copies the parts into memory instead of referring to the stream.
            using MimeMessage message = await MimeMessage
                .LoadAsync(MimeParserDefaults.Create(), eml, persistent: false, ct)
                .ConfigureAwait(false);
            return ToContent(message, ct);
        }
        catch (FormatException)
        {
            // MimeKit reports unparsable input this way. Its message may quote parts of the mail, so it is not kept.
            throw new MailContentException();
        }
    }

    private static MailContent ToContent(MimeMessage message, CancellationToken ct)
    {
        (Dictionary<string, string> inlineImages, List<MailAttachmentInfo> attachments) = ReadParts(message, ct);
        List<MailSender> senders = message.From.Mailboxes
            .Select(mailbox => new MailSender(SafeDisplayText.Clean(mailbox.Name ?? string.Empty), SafeDisplayText.Clean(mailbox.Address)))
            .ToList();
        return new MailContent(
            SafeDisplayText.Clean(message.Subject ?? string.Empty),
            string.Join(", ", DescribeAddresses(message.From)),
            DescribeAddresses(message.To),
            DescribeAddresses(message.Cc),
            ReadDate(message),
            message.HtmlBody,
            ReadPlainText(message),
            inlineImages,
            attachments,
            senders,
            ContainsOpaquePart(message.Body));
    }

    // A generic part with a container type is what the parser leaves at the depth limit. The walk is bounded by that
    // limit. Delivery reports and the like have their own part types and are not opaque.
    private static bool ContainsOpaquePart(MimeEntity? entity)
    {
        return entity switch
        {
            Multipart multipart => multipart.Any(ContainsOpaquePart),
            MessagePart messagePart => ContainsOpaquePart(messagePart.Message?.Body),
            MimePart part => IsOpaqueContainer(part),
            _ => false,
        };
    }

    private static bool IsOpaqueContainer(MimePart part)
    {
        return part.GetType() == typeof(MimePart)
            && (part.ContentType.MediaType.Equals("multipart", StringComparison.OrdinalIgnoreCase)
                || part.ContentType.MediaType.Equals("message", StringComparison.OrdinalIgnoreCase));
    }

    // MimeMessage.Date is MinValue when the header is absent or unreadable.
    private static DateTimeOffset? ReadDate(MimeMessage message)
    {
        return message.Date == DateTimeOffset.MinValue ? null : message.Date;
    }

    // The text body as written when it has content, otherwise the text of the HTML body (or empty).
    private static string ReadPlainText(MimeMessage message)
    {
        string? textBody = message.TextBody;
        return string.IsNullOrWhiteSpace(textBody) ? MimeBodyText.Extract(message, int.MaxValue) : textBody;
    }

    // Group addresses are flattened to their members.
    private static List<string> DescribeAddresses(InternetAddressList addresses)
    {
        return addresses.Mailboxes.Select(DescribeMailbox).ToList();
    }

    private static string DescribeMailbox(MailboxAddress mailbox)
    {
        string description = string.IsNullOrWhiteSpace(mailbox.Name)
            ? mailbox.Address
            : $"{mailbox.Name} <{mailbox.Address}>";
        return SafeDisplayText.Clean(description);
    }

    // Sorts the leaf parts: body text is skipped, inline images within the limits are embedded, the rest is listed.
    private static (Dictionary<string, string> InlineImages, List<MailAttachmentInfo> Attachments) ReadParts(
        MimeMessage message,
        CancellationToken ct)
    {
        var inlineImages = new Dictionary<string, string>(StringComparer.Ordinal);
        var attachments = new List<MailAttachmentInfo>();
        long embeddedBytes = 0;

        foreach (MimeEntity entity in message.BodyParts)
        {
            ct.ThrowIfCancellationRequested();
            if (entity is not MimePart part)
            {
                // Attached messages and delivery reports have no content to decode here.
                attachments.Add(DescribeAttachment(entity, FileNameOf(entity), size: null));
                continue;
            }

            // An opaque container is no attachment of the sender but a limit of this program; it gets a notice instead.
            if (IsBodyText(part) || IsOpaqueContainer(part))
            {
                continue;
            }

            long? size = MeasureDecodedSize(part, ct);
            string? contentId = part.ContentId;
            bool isEmbeddable = !part.IsAttachment
                && !string.IsNullOrEmpty(contentId)
                && !inlineImages.ContainsKey(contentId)
                && InlineImageTypes.Contains(part.ContentType.MimeType)
                && size is <= MaxInlineImageBytes
                && embeddedBytes + size <= MaxInlineImagesTotalBytes;
            if (isEmbeddable)
            {
                inlineImages.Add(contentId!, ToDataUri(part, ct));
                embeddedBytes += size!.Value;
            }
            else
            {
                attachments.Add(DescribeAttachment(part, part.FileName, size));
            }
        }

        return (inlineImages, attachments);
    }

    // A nameless plain or HTML part that is not marked as attachment belongs to the body.
    private static bool IsBodyText(MimePart part)
    {
        return part is TextPart text
            && !text.IsAttachment
            && string.IsNullOrEmpty(text.FileName)
            && (text.IsPlain || text.IsHtml);
    }

    private static string? FileNameOf(MimeEntity entity)
    {
        return entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
    }

    // The name is only ever shown; it is cleaned here and never used as a path.
    private static MailAttachmentInfo DescribeAttachment(MimeEntity entity, string? fileName, long? size)
    {
        return new MailAttachmentInfo(
            SafeDisplayText.Clean(fileName ?? string.Empty),
            SafeDisplayText.Clean(entity.ContentType.MimeType),
            size);
    }

    private static long? MeasureDecodedSize(MimePart part, CancellationToken ct)
    {
        if (part.Content is null)
        {
            return null;
        }

        using var measuringStream = new MeasuringStream();
        part.Content.DecodeTo(measuringStream, ct);
        return measuringStream.Length;
    }

    private static string ToDataUri(MimePart part, CancellationToken ct)
    {
        using var decodedContent = new MemoryStream();
        part.Content!.DecodeTo(decodedContent, ct);
        string base64 = Convert.ToBase64String(decodedContent.GetBuffer(), 0, (int)decodedContent.Length);
        return $"data:{part.ContentType.MimeType.ToLowerInvariant()};base64,{base64}";
    }
}
