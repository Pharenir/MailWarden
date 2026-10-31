namespace MailWarden.Core.Classification;

/// <summary>
/// Derives <see cref="AuthStatus"/> from the parsed top-most Authentication-Results header (rules of AP-07a):
/// Authenticated = DMARC pass, or DKIM pass for an aligned domain, or SPF pass for an aligned domain;
/// Failed = not Authenticated and (DMARC fail, SPF fail, or at least one DKIM result and all of them fail);
/// Unknown otherwise.
/// </summary>
internal static class AuthStatusEvaluator
{
    /// <summary>Computes the authentication state.</summary>
    /// <param name="authentication">Parsed header (Empty when there is none).</param>
    /// <param name="fromDomain">Domain of the From address.</param>
    /// <returns>The state.</returns>
    public static AuthStatus Evaluate(AuthenticationResults authentication, string fromDomain)
    {
        if (DescribePass(authentication, fromDomain) is not null)
        {
            return AuthStatus.Authenticated;
        }

        var dmarcFailed = authentication.Dmarc?.Verdict == AuthVerdict.Fail;
        var spfFailed = authentication.Spf?.Verdict == AuthVerdict.Fail;
        return dmarcFailed || spfFailed || AllDkimFailed(authentication) ? AuthStatus.Failed : AuthStatus.Unknown;
    }

    /// <summary>Describes the first check that makes the message authenticated, for reason texts.</summary>
    /// <param name="authentication">Parsed header.</param>
    /// <param name="fromDomain">Domain of the From address.</param>
    /// <returns>E.g. "DMARC pass for 'example.test'", or null when the message is not authenticated.</returns>
    public static string? DescribePass(AuthenticationResults authentication, string fromDomain)
    {
        var dmarc = authentication.Dmarc;
        if (dmarc?.Verdict == AuthVerdict.Pass)
        {
            return dmarc.Domain is null ? "DMARC pass" : $"DMARC pass for '{dmarc.Domain}'";
        }

        foreach (var dkim in authentication.Dkim)
        {
            if (dkim.Verdict == AuthVerdict.Pass && IsAligned(dkim.Domain, fromDomain))
            {
                return $"DKIM pass for aligned domain '{dkim.Domain}'";
            }
        }

        var spf = authentication.Spf;
        if (spf?.Verdict == AuthVerdict.Pass && IsAligned(spf.Domain, fromDomain))
        {
            return $"SPF pass for aligned domain '{spf.Domain}'";
        }

        return null;
    }

    /// <summary>True when at least one DKIM result exists and every one of them is Fail.</summary>
    /// <param name="authentication">Parsed header.</param>
    /// <returns>True when all DKIM signatures failed.</returns>
    public static bool AllDkimFailed(AuthenticationResults authentication)
    {
        return authentication.Dkim.Count > 0 && authentication.Dkim.All(dkim => dkim.Verdict == AuthVerdict.Fail);
    }

    // Domains from the header are untrusted; a missing or label-less domain is never aligned.
    private static bool IsAligned(string? resultDomain, string fromDomain)
    {
        var resultOrganizationalDomain = DomainNames.TryGetOrganizationalDomain(resultDomain);
        return resultOrganizationalDomain is not null
            && string.Equals(
                resultOrganizationalDomain,
                DomainNames.TryGetOrganizationalDomain(fromDomain),
                StringComparison.Ordinal);
    }
}
