using System.Collections.Frozen;

namespace MailWarden.Core.Classification;

/// <summary>
/// Domain helpers for the classifier. The organisational domain is approximated without a public suffix list:
/// last two labels, or last three when the last two form a known multi-label public suffix (built-in list).
/// All results are lower-case (invariant culture) without trailing '.'.
/// </summary>
public static class DomainNames
{
    // Known multi-label public suffixes; a full public suffix list is a non-goal of AP-07a.
    private static readonly FrozenSet<string> MultiLabelPublicSuffixes = new[]
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au",
        "co.jp", "ne.jp", "or.jp",
        "co.nz", "net.nz", "org.nz",
        "com.br", "com.tr", "co.za", "com.cn", "net.cn", "org.cn",
        "co.at", "or.at", "co.in", "co.kr", "com.mx", "com.ar", "com.sg", "com.hk", "com.tw",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Domain part of an address: text after the last '@', trimmed, lower-case, trailing '.' removed.</summary>
    /// <param name="emailAddress">E-mail address.</param>
    /// <returns>The domain, e.g. "mail.example.test" for "A.User@Mail.Example.TEST.".</returns>
    /// <exception cref="ArgumentNullException"><paramref name="emailAddress"/> is null.</exception>
    /// <exception cref="ArgumentException">The address contains no '@' or the domain part is empty.</exception>
    public static string GetDomain(string emailAddress)
    {
        ArgumentNullException.ThrowIfNull(emailAddress);
        var domain = TryGetDomain(emailAddress);
        if (domain is null)
        {
            // The address itself is personal data and deliberately not part of the message.
            throw new ArgumentException("E-mail address must contain '@' followed by a domain.", nameof(emailAddress));
        }

        return domain;
    }

    /// <summary>
    /// Approximated organisational domain: last two labels, or last three when the last two form a known multi-label
    /// public suffix (e.g. "co.uk"). Single-label input is returned unchanged.
    /// </summary>
    /// <param name="domain">Host or domain name.</param>
    /// <returns>Lower-case organisational domain, e.g. "qzxkvb73.top" for "mail.qzxkvb73.top".</returns>
    /// <exception cref="ArgumentNullException"><paramref name="domain"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="domain"/> contains no label.</exception>
    public static string GetOrganizationalDomain(string domain)
    {
        var labels = SplitLabels(domain);
        return string.Join('.', labels[^CountOrganizationalLabels(labels)..]);
    }

    /// <summary>
    /// Registrable label = organisational domain without its public suffix ("mail.qzxkvb73.top" → "qzxkvb73",
    /// "shop.example.co.uk" → "example"). Single-label input is returned unchanged.
    /// </summary>
    /// <param name="domain">Host or domain name.</param>
    /// <returns>Lower-case registrable label.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="domain"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="domain"/> contains no label.</exception>
    public static string GetRegistrableLabel(string domain)
    {
        var labels = SplitLabels(domain);
        return labels[^CountOrganizationalLabels(labels)];
    }

    /// <summary>
    /// Public suffix of the organisational domain ("top", "de", "co.uk"). Empty string for single-label input.
    /// </summary>
    /// <param name="domain">Host or domain name.</param>
    /// <returns>Lower-case public suffix.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="domain"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="domain"/> contains no label.</exception>
    public static string GetPublicSuffix(string domain)
    {
        var labels = SplitLabels(domain);
        var suffixLabelCount = CountOrganizationalLabels(labels) - 1;
        return suffixLabelCount == 0 ? string.Empty : string.Join('.', labels[^suffixLabelCount..]);
    }

    /// <summary>True when both domains have the same organisational domain (relaxed DMARC-style alignment).</summary>
    /// <param name="domainA">First host or domain name.</param>
    /// <param name="domainB">Second host or domain name.</param>
    /// <returns>True when aligned.</returns>
    /// <exception cref="ArgumentNullException">One of the domains is null.</exception>
    /// <exception cref="ArgumentException">One of the domains contains no label.</exception>
    public static bool AreAligned(string domainA, string domainB)
    {
        return string.Equals(GetOrganizationalDomain(domainA), GetOrganizationalDomain(domainB), StringComparison.Ordinal);
    }

    /// <summary>Non-throwing variant of <see cref="GetDomain"/> for untrusted values.</summary>
    /// <param name="emailAddress">Address or null.</param>
    /// <returns>The domain, or null when there is no '@' or the domain part is empty.</returns>
    internal static string? TryGetDomain(string? emailAddress)
    {
        if (emailAddress is null)
        {
            return null;
        }

        var atIndex = emailAddress.LastIndexOf('@');
        if (atIndex < 0)
        {
            return null;
        }

        // A domain part without any label ("a@...", "a@. .") counts as empty.
        var domain = Normalize(emailAddress[(atIndex + 1)..]);
        return TrySplitLabels(domain) is null ? null : domain;
    }

    /// <summary>Non-throwing variant of <see cref="GetOrganizationalDomain"/> for untrusted values.</summary>
    /// <param name="domain">Host/domain or null.</param>
    /// <returns>The organisational domain, or null when the value contains no label.</returns>
    internal static string? TryGetOrganizationalDomain(string? domain)
    {
        var labels = TrySplitLabels(domain);
        return labels is null ? null : string.Join('.', labels[^CountOrganizationalLabels(labels)..]);
    }

    /// <summary>Trims whitespace, removes trailing dots and lower-cases (invariant).</summary>
    /// <param name="domain">Raw domain text.</param>
    /// <returns>Normalised text, possibly empty.</returns>
    internal static string Normalize(string domain)
    {
        return domain.Trim().TrimEnd('.').Trim().ToLowerInvariant();
    }

    // Number of trailing labels that form the organisational domain (1 for single-label input).
    private static int CountOrganizationalLabels(string[] labels)
    {
        if (labels.Length >= 3 && MultiLabelPublicSuffixes.Contains(labels[^2] + "." + labels[^1]))
        {
            return 3;
        }

        return Math.Min(labels.Length, 2);
    }

    private static string[] SplitLabels(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return TrySplitLabels(domain)
            ?? throw new ArgumentException("Domain must contain at least one label.", nameof(domain));
    }

    // Empty labels ("a..b", leading '.') are dropped; null when nothing is left.
    private static string[]? TrySplitLabels(string? domain)
    {
        if (domain is null)
        {
            return null;
        }

        var labels = Normalize(domain).Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return labels.Length == 0 ? null : labels;
    }
}
