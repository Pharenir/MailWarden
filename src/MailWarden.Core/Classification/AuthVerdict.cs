namespace MailWarden.Core.Classification;

/// <summary>
/// Result keyword of one method in an Authentication-Results header (RFC 8601 result values).
/// </summary>
public enum AuthVerdict
{
    /// <summary>
    /// "none": the method was not applicable (e.g. message not signed); also Microsoft's "bestguesspass" (no DMARC
    /// record published). For <see cref="AuthenticationResults.CompAuth"/> it also means "absent".
    /// </summary>
    None,

    /// <summary>"pass".</summary>
    Pass,

    /// <summary>"fail" (also the obsolete RFC 5451 synonym "hardfail").</summary>
    Fail,

    /// <summary>"softfail" (SPF).</summary>
    SoftFail,

    /// <summary>"neutral".</summary>
    Neutral,

    /// <summary>"temperror" (also "timeout"): temporary failure while checking.</summary>
    TempError,

    /// <summary>"permerror": permanent error while checking (e.g. broken record).</summary>
    PermError,

    /// <summary>"policy" (DKIM signature rejected by local policy).</summary>
    Policy,

    /// <summary>Any keyword not listed above.</summary>
    Unknown,
}
