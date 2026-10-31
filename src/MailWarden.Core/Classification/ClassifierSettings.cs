using System.Collections.Frozen;

namespace MailWarden.Core.Classification;

/// <summary>
/// Classifier configuration (thresholds and lists). Lists are compared case-insensitively, independent of the
/// comparer of the given sets.
/// </summary>
/// <param name="LikelySpamThreshold">Score from which a message is <see cref="SpamTier.LikelySpam"/>; must be &gt; 0.</param>
/// <param name="CertainSpamThreshold">Score from which a message is <see cref="SpamTier.CertainSpam"/>; must be
/// greater than <paramref name="LikelySpamThreshold"/>.</param>
/// <param name="SuspiciousTlds">Top-level domains (without dot) that add points, e.g. "top".</param>
/// <param name="FreemailDomains">Domains of free mail providers, e.g. "gmail.com".</param>
public sealed record ClassifierSettings(
    int LikelySpamThreshold,
    int CertainSpamThreshold,
    IReadOnlySet<string> SuspiciousTlds,
    IReadOnlySet<string> FreemailDomains)
{
    /// <summary>
    /// Defaults: thresholds 30 / 70; suspicious TLDs top, xyz, click, loan, work, gq, tk, ml, cf, ga, icu, buzz, rest,
    /// cfd, sbs; common international and German freemail domains.
    /// </summary>
    public static ClassifierSettings Default { get; } = new(
        30,
        70,
        CreateSet("top", "xyz", "click", "loan", "work", "gq", "tk", "ml", "cf", "ga", "icu", "buzz", "rest", "cfd", "sbs"),
        CreateSet(
            "gmail.com", "googlemail.com", "hotmail.com", "hotmail.de", "outlook.com", "outlook.de", "live.com", "live.de",
            "msn.com", "yahoo.com", "yahoo.de", "gmx.de", "gmx.net", "web.de", "t-online.de", "icloud.com", "me.com",
            "aol.com", "proton.me", "protonmail.com", "freenet.de"));

    /// <summary>Checks the settings (fail fast when loading).</summary>
    /// <exception cref="ArgumentException">A threshold is &lt;= 0, <see cref="LikelySpamThreshold"/> &gt;=
    /// <see cref="CertainSpamThreshold"/>, a list is null, or a list contains null/empty entries.</exception>
    public void Validate()
    {
        if (LikelySpamThreshold <= 0 || CertainSpamThreshold <= 0)
        {
            throw new ArgumentException(
                $"Spam thresholds must be greater than 0 (likely {LikelySpamThreshold}, certain {CertainSpamThreshold}).");
        }

        if (LikelySpamThreshold >= CertainSpamThreshold)
        {
            throw new ArgumentException(
                $"LikelySpamThreshold ({LikelySpamThreshold}) must be lower than CertainSpamThreshold ({CertainSpamThreshold}).");
        }

        ValidateList(SuspiciousTlds, nameof(SuspiciousTlds));
        ValidateList(FreemailDomains, nameof(FreemailDomains));
    }

    /// <summary>Whether the top-level domain is on <see cref="SuspiciousTlds"/> (case-insensitive).</summary>
    /// <param name="topLevelDomain">Last label of a domain, without dot.</param>
    /// <returns>True when listed.</returns>
    internal bool IsSuspiciousTld(string topLevelDomain) => ContainsIgnoreCase(SuspiciousTlds, topLevelDomain);

    /// <summary>Whether the domain is on <see cref="FreemailDomains"/> (case-insensitive, exact domain).</summary>
    /// <param name="domain">Domain to look up.</param>
    /// <returns>True when listed.</returns>
    internal bool IsFreemailDomain(string domain) => ContainsIgnoreCase(FreemailDomains, domain);

    private static FrozenSet<string> CreateSet(params string[] entries)
    {
        return entries.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateList(IReadOnlySet<string>? entries, string listName)
    {
        if (entries is null)
        {
            throw new ArgumentException($"{listName} must not be null.");
        }

        if (entries.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"{listName} must not contain null or empty entries.");
        }
    }

    // Fast path via the set's own comparer, then a linear case-insensitive scan (lists are small).
    private static bool ContainsIgnoreCase(IReadOnlySet<string> entries, string value)
    {
        if (entries.Contains(value))
        {
            return true;
        }

        foreach (var entry in entries)
        {
            if (entry is not null && entry.AsSpan().Trim().Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
