namespace MailWarden.Core.Classification;

/// <summary>
/// Derives <see cref="AuthStatus"/> from the parsed top-most Authentication-Results header (rules of AP-07a, planner
/// decisions C11/C12/C22): the provider's DMARC result takes precedence –
/// DMARC fail → Failed (an aligned SPF/DKIM pass does not rescue it);
/// DMARC pass whose header.from is absent or aligned with the From domain → Authenticated;
/// otherwise (no DMARC, other verdicts, a pass for another domain, or a header.from without a determinable domain)
/// DKIM pass for an aligned domain or SPF pass for
/// an aligned domain → Authenticated; Failed = not Authenticated and (SPF fail, or at least one DKIM result and all of
/// them fail); Unknown otherwise.
/// </summary>
internal static class AuthStatusEvaluator
{
    /// <summary>Computes the authentication state.</summary>
    /// <param name="authentication">Parsed header (Empty when there is none).</param>
    /// <param name="fromDomain">Domain of the From address.</param>
    /// <returns>The state.</returns>
    public static AuthStatus Evaluate(AuthenticationResults authentication, string fromDomain)
    {
        if (IsDmarcFail(authentication))
        {
            return AuthStatus.Failed;
        }

        if (DescribePass(authentication, fromDomain) is not null)
        {
            return AuthStatus.Authenticated;
        }

        var spfFailed = authentication.Spf?.Verdict == AuthVerdict.Fail;
        return spfFailed || AllDkimFailed(authentication) ? AuthStatus.Failed : AuthStatus.Unknown;
    }

    /// <summary>
    /// Describes the first check that makes the message authenticated, for reason texts. Consistent with
    /// <see cref="Evaluate"/>: non-null exactly when the state is <see cref="AuthStatus.Authenticated"/>.
    /// </summary>
    /// <param name="authentication">Parsed header.</param>
    /// <param name="fromDomain">Domain of the From address.</param>
    /// <returns>E.g. "DMARC pass for 'example.test'" (domains truncated for display), or null when the message is not
    /// authenticated.</returns>
    public static string? DescribePass(AuthenticationResults authentication, string fromDomain)
    {
        if (IsDmarcFail(authentication))
        {
            return null;
        }

        // A DMARC pass counts only for the From domain (C12); a pass for another domain falls through to DKIM/SPF.
        // Domain null: trusted only when header.from is absent; present without a determinable domain is ambiguous
        // input and falls through as well (C22).
        var dmarc = authentication.Dmarc;
        if (dmarc?.Verdict == AuthVerdict.Pass)
        {
            if (dmarc.Domain is null && !authentication.IsDmarcHeaderFromWithoutDomain)
            {
                return "DMARC pass";
            }

            if (dmarc.Domain is not null && IsAligned(dmarc.Domain, fromDomain))
            {
                return $"DMARC pass for '{ReasonText.Truncate(dmarc.Domain)}'";
            }
        }

        foreach (var dkim in authentication.Dkim)
        {
            if (dkim.Verdict == AuthVerdict.Pass && dkim.Domain is not null && IsAligned(dkim.Domain, fromDomain))
            {
                return $"DKIM pass for aligned domain '{ReasonText.Truncate(dkim.Domain)}'";
            }
        }

        var spf = authentication.Spf;
        if (spf?.Verdict == AuthVerdict.Pass && spf.Domain is not null && IsAligned(spf.Domain, fromDomain))
        {
            return $"SPF pass for aligned domain '{ReasonText.Truncate(spf.Domain)}'";
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

    private static bool IsDmarcFail(AuthenticationResults authentication)
    {
        return authentication.Dmarc?.Verdict == AuthVerdict.Fail;
    }

    // Domains from the header are untrusted; a missing or label-less domain is never aligned.
    private static bool IsAligned(string resultDomain, string fromDomain)
    {
        var resultOrganizationalDomain = DomainNames.TryGetOrganizationalDomain(resultDomain);
        return resultOrganizationalDomain is not null
            && string.Equals(
                resultOrganizationalDomain,
                DomainNames.TryGetOrganizationalDomain(fromDomain),
                StringComparison.Ordinal);
    }
}
