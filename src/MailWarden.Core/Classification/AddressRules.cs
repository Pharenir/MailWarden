using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MailWarden.Core.Text;

namespace MailWarden.Core.Classification;

/// <summary>
/// The rules for addresses and domains that enter the address book or the allow/block lists, and the form in which a sender
/// is looked up in those lists. Messages of this class never quote a value.
/// </summary>
public static class AddressRules
{
    // RFC 5321: a forward path holds at most 256 characters including the angle brackets.
    private const int MaxAddressLength = 254;
    private const int MaxDomainLength = 253;
    private const char ReplacementCharacter = '\uFFFD';

    // Syntax characters of an address list or a mail header. Quoted local parts are rejected with them on purpose: nobody
    // writes to them by hand, and they would be the way to hide an address inside an address.
    private static readonly SearchValues<char> ForbiddenCharacters = SearchValues.Create("<>()[]\\,;:\"");

    /// <summary>
    /// Whether the text is usable as an address: one '@' with text on both sides, a domain without empty labels (one label
    /// is enough), at most 254 characters, no whitespace, no control or invisible characters and no syntax characters of an
    /// address list. Surrounding whitespace counts as malformed; callers trim first.
    /// </summary>
    /// <param name="address">Address or null.</param>
    /// <returns>True when the address is well-formed.</returns>
    public static bool IsWellFormedAddress([NotNullWhen(true)] string? address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > MaxAddressLength || !HasOnlyAddressCharacters(address))
        {
            return false;
        }

        int atIndex = address.IndexOf('@', StringComparison.Ordinal);
        return atIndex > 0
            && atIndex == address.LastIndexOf('@')
            && HasOnlyNonEmptyLabels(address[(atIndex + 1)..]);
    }

    /// <summary>
    /// Whether the text is usable as a domain entry: no '@', at least one dot, no empty labels, at most 253 characters, no
    /// whitespace, no control or invisible characters and no syntax characters of an address list. Surrounding whitespace
    /// counts as malformed; callers trim first.
    /// </summary>
    /// <param name="domain">Domain or null.</param>
    /// <returns>True when the domain is well-formed.</returns>
    public static bool IsWellFormedDomain(string? domain)
    {
        return !string.IsNullOrEmpty(domain)
            && domain.Length <= MaxDomainLength
            && HasOnlyAddressCharacters(domain)
            && !domain.Contains('@', StringComparison.Ordinal)
            && domain.Contains('.', StringComparison.Ordinal)
            && HasOnlyNonEmptyLabels(domain);
    }

    /// <summary>
    /// Whether a sender is exact enough for the allow list: well-formed as it stands, and its domain part is the same text as
    /// its normalised form except for the case of ASCII letters. So normalising did not make it equal to a listed domain,
    /// as it would for a Kelvin sign or a trailing no-break space.
    /// </summary>
    /// <param name="address">Sender as given, untrimmed, or null.</param>
    /// <returns>True when an allow entry may apply to this sender.</returns>
    public static bool IsExactSender(string? address)
    {
        if (!IsWellFormedAddress(address))
        {
            return false;
        }

        string domain = address[(address.IndexOf('@', StringComparison.Ordinal) + 1)..];
        string normalized = DomainNames.Normalize(domain);
        return domain.Length == normalized.Length
            && domain.Zip(normalized).All(pair => pair.First == pair.Second
                || (char.IsAsciiLetter(pair.First) && char.ToLowerInvariant(pair.First) == pair.Second));
    }

    /// <summary>
    /// Form in which domains of entries and senders are compared: trimmed, without trailing dots, lower-case, with Punycode
    /// labels decoded, so that both forms of an international domain match. A value that is no valid international name stays
    /// as it is, apart from the case.
    /// </summary>
    /// <param name="domain">Domain text.</param>
    /// <returns>The comparable form, possibly empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="domain"/> is null.</exception>
    public static string NormalizeDomain(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        string normalized = DomainNames.Normalize(domain);
        try
        {
            return new IdnMapping().GetUnicode(normalized).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return normalized;
        }
    }

    /// <summary>
    /// Lookup form of a sender or of an address entry: trimmed, without the quotes of a fully quoted local part, with the domain
    /// of <see cref="NormalizeDomain"/>. Deliberately more tolerant than <see cref="IsWellFormedAddress"/>: a quoted local part
    /// or a trailing dot in the domain must not carry a sender past the block list. Plus and dot variants of a local part stay
    /// different addresses: which ones reach one mailbox is the provider's rule.
    /// </summary>
    /// <param name="address">Address or null.</param>
    /// <returns>The lookup form, or null when the text is blank or has no local part or no domain.</returns>
    public static string? ToLookupForm(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        string trimmed = address.Trim();
        int atIndex = trimmed.LastIndexOf('@');
        if (atIndex <= 0 || string.IsNullOrWhiteSpace(trimmed[..atIndex]))
        {
            return null;
        }

        string? domain = DomainNames.TryGetDomain(trimmed);
        return domain is null ? null : $"{RemoveQuotes(trimmed[..atIndex])}@{NormalizeDomain(domain)}";
    }

    // Only around the whole local part. Empty quotes stay, so that such a sender still has a local part for the block list.
    private static string RemoveQuotes(string localPart)
    {
        bool isQuoted = localPart.Length > 2 && localPart[0] == '"' && localPart[^1] == '"';
        return isQuoted && !string.IsNullOrWhiteSpace(localPart[1..^1]) ? localPart[1..^1] : localPart;
    }

    // SafeDisplayText knows the control, bidi and invisible characters; an address that it would change is disguised.
    private static bool HasOnlyAddressCharacters(string text)
    {
        return !text.Any(char.IsWhiteSpace)
            && !text.AsSpan().ContainsAny(ForbiddenCharacters)
            && !text.Contains(ReplacementCharacter, StringComparison.Ordinal)
            && ReferenceEquals(SafeDisplayText.Clean(text), text);
    }

    private static bool HasOnlyNonEmptyLabels(string domain)
    {
        return domain.Split('.').All(label => label.Length > 0);
    }
}
