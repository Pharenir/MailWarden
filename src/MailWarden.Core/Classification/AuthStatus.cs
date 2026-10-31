namespace MailWarden.Core.Classification;

/// <summary>
/// Overall authentication state of a message, computed from the top-most Authentication-Results header only.
/// </summary>
public enum AuthStatus
{
    /// <summary>
    /// No header, only inconclusive results (none, neutral, softfail, temperror, ...), or DMARC fail overruled by
    /// Microsoft's compauth pass.
    /// </summary>
    Unknown,

    /// <summary>DMARC pass, or DKIM/SPF pass for a domain aligned with the From domain.</summary>
    Authenticated,

    /// <summary>
    /// Not authenticated and DMARC fail (without compauth pass), SPF fail, or every DKIM signature failed.
    /// </summary>
    Failed,
}
