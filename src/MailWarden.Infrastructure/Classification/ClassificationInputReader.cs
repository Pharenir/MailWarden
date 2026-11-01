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

    // Punctuation that ends a sentence after a URL in plain text and is not part of the host.
    private static readonly char[] UrlTrailingPunctuation = ['.', ',', ';', ':', '!', '?'];

    /// <summary>
    /// Parses the EML. FromName is the display name ("" when absent), ReplyToAddress the first Reply-To address (null when
    /// absent), AuthenticationResultsHeaders the header values in header order (top-most first), BodyText at most 200,000
    /// characters of <see cref="MimeBodyText.Extract"/>. LinkHosts holds the lower-cased hosts of http/https links from
    /// <c>&lt;a href&gt;</c> of the HTML body or, without an HTML body, from URLs in the text; duplicates stay and malformed
    /// URLs are skipped.
    /// </summary>
    /// <param name="eml">Raw message, read from its current position; not disposed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The input, or null when no single usable From address with '@' and a domain exists (no From header, more
    /// than one From header, more than one address, or an address without a domain).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="eml"/> is null.</exception>
    /// <exception cref="FormatException">The stream does not contain a parsable message.</exception>
    public static async Task<ClassificationInput?> ReadAsync(Stream eml, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(eml);

        using MimeMessage message = await MimeMessage.LoadAsync(eml, ct).ConfigureAwait(false);
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
            .Select(header => header.Value)
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
        while (tokenizer.ReadNextToken(out HtmlToken? token))
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
            AddHost(hosts, match.Value.TrimEnd(UrlTrailingPunctuation));
        }

        return hosts;
    }

    // Uri decides the host, so "http://good.example@evil.example/" counts as evil.example, like in a browser.
    private static void AddHost(List<string> hosts, string? url)
    {
        if (url is null
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
