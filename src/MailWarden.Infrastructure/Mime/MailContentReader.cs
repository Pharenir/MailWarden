using System.Collections.Frozen;
using MailWarden.Core.Text;
using MimeKit;
using MimeKit.IO;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// Parses a raw message into <see cref="MailContent"/> for the reading pane. Nothing refers to the stream afterwards, so
/// the sync can overwrite the EML file while the message is displayed. Only the copy of one attachment works on the stream
/// while it runs, because its content does not go through memory as a whole.
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

    /// <summary>
    /// Writes the decoded content of one attachment to the destination; an attached message is written as that message. The
    /// parts stay in the stream, only what is copied passes through memory. Position and sorting are those of
    /// <see cref="ReadAsync"/>.
    /// </summary>
    /// <param name="eml">Raw message, read from its current position; seekable, so nothing is copied into memory. Not disposed.</param>
    /// <param name="attachmentIndex">Position in <see cref="MailContent.Attachments"/>.</param>
    /// <param name="shownAttachments">
    /// The list the user chose from; the part at that position must still be the same. Null when no list is known.
    /// </param>
    /// <param name="destination">Receives the content; not disposed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the content is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="eml"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attachmentIndex"/> lies outside the list of this message.</exception>
    /// <exception cref="MailContentException">
    /// The stream cannot be parsed, the part has no content, or it no longer matches <paramref name="shownAttachments"/>. Nothing
    /// was written then.
    /// </exception>
    /// <exception cref="IOException">Reading or writing failed; passed on unchanged.</exception>
    /// <exception cref="OperationCanceledException">The copy was cancelled.</exception>
    public static async Task CopyAttachmentAsync(
        Stream eml,
        int attachmentIndex,
        IReadOnlyList<MailAttachmentInfo>? shownAttachments,
        Stream destination,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(eml);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(attachmentIndex);

        try
        {
            // persistent: true leaves the content of the parts in the stream instead of copying it into memory.
            using MimeMessage message = await MimeMessage
                .LoadAsync(MimeParserDefaults.Create(), eml, persistent: true, ct)
                .ConfigureAwait(false);
            List<ListedAttachment> attachments = SortParts(message, ct).Attachments;
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(attachmentIndex, attachments.Count);

            ListedAttachment attachment = attachments[attachmentIndex];
            bool isStillTheSamePart = shownAttachments is null
                || (attachmentIndex < shownAttachments.Count && shownAttachments[attachmentIndex] == attachment.Info);
            if (!isStillTheSamePart)
            {
                throw new MailContentException();
            }

            await WriteContentAsync(attachment.Entity, destination, ct).ConfigureAwait(false);
        }
        catch (FormatException)
        {
            // As in ReadAsync: the message may quote parts of the mail, so it is not kept.
            throw new MailContentException();
        }
    }

    private static async Task WriteContentAsync(MimeEntity entity, Stream destination, CancellationToken ct)
    {
        switch (entity)
        {
            case MimePart { Content: IMimeContent content }:
                await content.DecodeToAsync(destination, ct).ConfigureAwait(false);
                break;
            case MessagePart { Message: MimeMessage attachedMessage }:
                await attachedMessage.WriteToAsync(FormatOptions.Default, destination, ct).ConfigureAwait(false);
                break;
            default:
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

    // What the parser left unparsed: a generic part with a container type at the depth limit, and a multipart whose
    // boundary never occurs, which keeps its whole content as preamble. The walk is bounded by the depth limit.
    private static bool ContainsOpaquePart(MimeEntity? entity)
    {
        return entity switch
        {
            Multipart { Count: 0 } emptyMultipart => !string.IsNullOrWhiteSpace(emptyMultipart.Preamble),
            Multipart multipart => multipart.Any(ContainsOpaquePart),
            MessagePart messagePart => ContainsOpaquePart(messagePart.Message?.Body),
            MimePart part => IsOpaqueContainer(part),
            _ => false,
        };
    }

    // Other message types such as message/partial or delivery reports are normal attachments.
    private static bool IsOpaqueContainer(MimePart part)
    {
        return part.ContentType.IsMimeType("multipart", "*") || part.ContentType.IsMimeType("message", "rfc822");
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

    private static (Dictionary<string, string> InlineImages, List<MailAttachmentInfo> Attachments) ReadParts(
        MimeMessage message,
        CancellationToken ct)
    {
        PartSorting sorting = SortParts(message, ct);
        var inlineImages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (MimePart image in sorting.EmbeddedImages)
        {
            inlineImages.Add(image.ContentId!, ToDataUri(image, ct));
        }

        return (inlineImages, sorting.Attachments.Select(attachment => attachment.Info).ToList());
    }

    // The one rule for all readers. Sorts the leaf parts: body text is skipped, inline images within the limits are embedded, the
    // rest is listed. The position in the list is what addresses an attachment.
    private static PartSorting SortParts(MimeMessage message, CancellationToken ct)
    {
        var embeddedContentIds = new HashSet<string>(StringComparer.Ordinal);
        var embeddedImages = new List<MimePart>();
        var attachments = new List<ListedAttachment>();
        long embeddedBytes = 0;

        foreach (MimeEntity entity in message.BodyParts)
        {
            ct.ThrowIfCancellationRequested();
            if (entity is not MimePart part)
            {
                // Attached messages have no content to decode here. One that ends below the depth limit gets the notice
                // instead of an unnamed entry.
                if (!ContainsOpaquePart(entity))
                {
                    attachments.Add(new ListedAttachment(entity, DescribeAttachment(entity, FileNameOf(entity), size: null)));
                }

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
                && !embeddedContentIds.Contains(contentId)
                && InlineImageTypes.Contains(part.ContentType.MimeType)
                && size is <= MaxInlineImageBytes
                && embeddedBytes + size <= MaxInlineImagesTotalBytes;
            if (isEmbeddable)
            {
                embeddedContentIds.Add(contentId!);
                embeddedImages.Add(part);
                embeddedBytes += size!.Value;
            }
            else
            {
                attachments.Add(new ListedAttachment(part, DescribeAttachment(part, part.FileName, size)));
            }
        }

        return new PartSorting(embeddedImages, attachments);
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

    // The name is cleaned here and never used as a path; a file on disk gets a name made by AttachmentFileName.ToSafe.
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

    /// <summary>One listed attachment.</summary>
    /// <param name="Entity">Its part in the parsed message.</param>
    /// <param name="Info">What the list shows of it.</param>
    private sealed record ListedAttachment(MimeEntity Entity, MailAttachmentInfo Info);

    /// <summary>The result of sorting the parts of a message.</summary>
    /// <param name="EmbeddedImages">Inline images within the limits, each with its own Content-ID.</param>
    /// <param name="Attachments">Everything else that is listed, in the order of the message.</param>
    private sealed record PartSorting(List<MimePart> EmbeddedImages, List<ListedAttachment> Attachments);
}
