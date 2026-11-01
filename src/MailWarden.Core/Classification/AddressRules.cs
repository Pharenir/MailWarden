using System.Buffers;
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
    private const char ReplacementCharacter = '�';

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
    public static bool IsWellFormedAddress(string? address)
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
    /// Lookup form of a sender or of an address entry: trimmed, with the domain as <see cref="DomainNames.GetDomain"/> gives it.
    /// Deliberately more tolerant than <see cref="IsWellFormedAddress"/>: a quoted local part or a trailing dot in the domain
    /// must not carry a sender past the block list.
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
        return domain is null ? null : string.Concat(trimmed.AsSpan(0, atIndex + 1), domain);
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
