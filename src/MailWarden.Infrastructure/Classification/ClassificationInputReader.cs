using System.Text;
using System.Text.RegularExpressions;
using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Mime;
using MimeKit;
using MimeKit.Text;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// Builds the <see cref="ClassificationInput"/> of a message from its raw EML.
/// </summary>
public static partial class ClassificationInputReader
{
    private const int BodyTextMaxChars = 200_000;
    private const int LinkHostsMaxCount = 10_000;

    // Punctuation that ends a sentence after a URL in plain text and is not part of the host.
    private static readonly char[] UrlTrailingPunctuation = ['.', ',', ';', ':', '!', '?'];

    /// <summary>
    /// Parses the EML and returns only its classifier input; see <see cref="ReadMessageAsync"/>.
    /// </summary>
    /// <param name="eml">Raw message, read from its current position; not disposed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The input, or null when the bytes are no message, no single usable From address exists or building the input failed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="eml"/> is null.</exception>
    /// <exception cref="IOException">Reading the stream failed.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static async Task<ClassificationInput?> ReadAsync(Stream eml, CancellationToken ct = default)
    {
        ClassificationMessage message = await ReadMessageAsync(eml, ct).ConfigureAwait(false);
        return message.Input;
    }

    /// <summary>
    /// Parses the EML. Never throws for unparsable bytes: <see cref="ClassificationMessage.Parsed"/> is false then, also for
    /// bytes in which no header was found. TopReceived is read before the input is built, so a failure there does not lose
    /// it. Input is null when no single usable From address with '@' and a domain exists (no From header, more than one From
    /// header, more than one address, an empty group, or an address without a domain) or when building the input failed; the
    /// latter also sets <see cref="ClassificationMessage.InputFailed"/>. Parsed with <see cref="MimeParserDefaults.Create"/>.
    /// In the input, FromName is the display name ("" when absent), ReplyToAddress the first Reply-To address (null when
    /// absent), BodyText at most 200,000 characters of <see cref="MimeBodyText.Extract"/>, and LinkHosts the lower-cased hosts
    /// of http/https links from <c>&lt;a href&gt;</c> of the HTML body or, without an HTML body, from URLs in the text;
    /// duplicates stay, malformed URLs are skipped, at most 10,000 entries. AuthenticationResultsHeaders (in header order,
    /// top-most first) and TopReceived are the raw values, unfolded but never decoded: Header.Value decodes encoded words,
    /// which would let a sender create authentication tokens inside a property value.
    /// </summary>
    /// <param name="eml">Raw message, read from its current position; not disposed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was found in the EML.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="eml"/> is null.</exception>
    /// <exception cref="IOException">Reading the stream failed.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static async Task<ClassificationMessage> ReadMessageAsync(Stream eml, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(eml);

        try
        {
            using MimeMessage message = await MimeMessage
                .LoadAsync(MimeParserDefaults.Create(), eml, ct)
                .ConfigureAwait(false);
            if (message.Headers.Count == 0)
            {
                return NoMessage();
            }

            string? topReceived = GetTopReceived(message.Headers);
            return CreateParsedMessage(message, topReceived);
        }
        catch (Exception exception) when (IsUnparsableContent(exception))
        {
            return NoMessage();
        }
    }

    /// <summary>
    /// Reads only the header block and returns the same value as <see cref="ClassificationMessage.TopReceived"/> of
    /// <see cref="ReadMessageAsync"/>: enough for the message key, without parsing the body.
    /// </summary>
    /// <param name="eml">Raw message, read from its current position; not disposed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The raw value of the top-most Received header; null when there is none or the bytes are no headers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="eml"/> is null.</exception>
    /// <exception cref="IOException">Reading the stream failed.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static async Task<string?> ReadTopReceivedAsync(Stream eml, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(eml);

        try
        {
            HeaderList headers = await HeaderList
                .LoadAsync(MimeParserDefaults.Create(), eml, ct)
                .ConfigureAwait(false);
            return GetTopReceived(headers);
        }
        catch (Exception exception) when (IsUnparsableContent(exception))
        {
            return null;
        }
    }

    private static ClassificationMessage NoMessage() => new(false, null, null);

    // Mainly the FormatException of the MIME parser. The exception is sender content, so nothing of it is kept. Failures of
    // the stream and cancellation are not about the content.
    private static bool IsUnparsableContent(Exception exception)
    {
        return exception is not (IOException or OperationCanceledException or ObjectDisposedException or OutOfMemoryException);
    }

    // The message parsed, so a failure here points to a programming error. It is reported apart from "no usable sender", and
    // the exception is dropped because it can quote message data.
    private static ClassificationMessage CreateParsedMessage(MimeMessage message, string? topReceived)
    {
        try
        {
            return new ClassificationMessage(true, topReceived, CreateInput(message));
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return new ClassificationMessage(true, topReceived, null) { InputFailed = true };
        }
    }

    private static ClassificationInput? CreateInput(MimeMessage message)
    {
        MailboxAddress? from = GetSingleFrom(message);
        if (from is null)
        {
            return null;
        }

        return new ClassificationInput(
            from.Address,
            from.Name ?? string.Empty,
            message.ReplyTo.Mailboxes.FirstOrDefault()?.Address,
            GetAuthenticationResultsHeaders(message),
            MimeBodyText.Extract(message, BodyTextMaxChars),
            GetLinkHosts(message));
    }

    // Latin-1 maps every byte to one character, so nothing is decoded or lost. Unfolding only removes line breaks.
    private static string GetRawValue(Header header) => Header.Unfold(Encoding.Latin1.GetString(header.RawValue));

    // The one way to the value that goes into the message key, for the pass and for the retention.
    private static string? GetTopReceived(HeaderList headers)
    {
        Header? topReceived = headers.FirstOrDefault(header => header.Id == HeaderId.Received);
        if (topReceived is null)
        {
            return null;
        }

        string value = GetRawValue(topReceived);
        return value.Length == 0 ? null : value;
    }

    // Exactly one From header holding exactly one mailbox; two senders are not decidable, so no decision is derived.
    private static MailboxAddress? GetSingleFrom(MimeMessage message)
    {
        if (message.Headers.Count(header => header.Id == HeaderId.From) != 1)
        {
            return null;
        }

        List<MailboxAddress> mailboxes = [.. message.From.Mailboxes];
        if (mailboxes.Count != 1 || !HasLocalPartAndDomain(mailboxes[0].Address))
        {
            return null;
        }

        return mailboxes[0];
    }

    private static bool HasLocalPartAndDomain(string address)
    {
        if (address.LastIndexOf('@') <= 0)
        {
            return false;
        }

        try
        {
            return DomainNames.GetDomain(address).Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static List<string> GetAuthenticationResultsHeaders(MimeMessage message)
    {
        return message.Headers
            .Where(header => header.Id == HeaderId.AuthenticationResults)
            .Select(GetRawValue)
            .ToList();
    }

    private static List<string> GetLinkHosts(MimeMessage message)
    {
        string? htmlBody = message.HtmlBody;
        if (!string.IsNullOrWhiteSpace(htmlBody))
        {
            return GetHtmlLinkHosts(htmlBody);
        }

        string? textBody = message.TextBody;
        return string.IsNullOrEmpty(textBody) ? [] : GetTextLinkHosts(textBody);
    }

    private static List<string> GetHtmlLinkHosts(string html)
    {
        var hosts = new List<string>();
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        while (hosts.Count < LinkHostsMaxCount && tokenizer.ReadNextToken(out HtmlToken? token))
        {
            if (token is not HtmlTagToken { Id: HtmlTagId.A, IsEndTag: false } anchor)
            {
                continue;
            }

            foreach (HtmlAttribute attribute in anchor.Attributes)
            {
                if (attribute.Id == HtmlAttributeId.Href)
                {
                    AddHost(hosts, attribute.Value);
                }
            }
        }

        return hosts;
    }

    private static List<string> GetTextLinkHosts(string text)
    {
        var hosts = new List<string>();
        foreach (Match match in PlainUrlRegex().Matches(text))
        {
            if (hosts.Count >= LinkHostsMaxCount)
            {
                break;
            }

            AddHost(hosts, match.Value.TrimEnd(UrlTrailingPunctuation));
        }

        return hosts;
    }

    // Uri decides the host, so "http://good.example@evil.example/" counts as evil.example, like in a browser. An anchor with
    // several href attributes cannot pass the limit: the limit is checked here as well.
    private static void AddHost(List<string> hosts, string? url)
    {
        if (hosts.Count >= LinkHostsMaxCount
            || url is null
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0)
        {
            return;
        }

        hosts.Add(uri.Host.ToLowerInvariant());
    }

    [GeneratedRegex("""https?://[^\s"'<>\)\]]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlainUrlRegex();
}
