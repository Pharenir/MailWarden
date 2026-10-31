namespace MailWarden.Core.Classification;

/// <summary>
/// Overall authentication state of a message, computed from the top-most Authentication-Results header only.
/// </summary>
public enum AuthStatus
{
    /// <summary>No header, or only inconclusive results (none, neutral, softfail, temperror, ...).</summary>
    Unknown,

    /// <summary>DMARC pass, or DKIM/SPF pass for a domain aligned with the From domain.</summary>
    Authenticated,

    /// <summary>Not authenticated and DMARC fail, SPF fail, or every DKIM signature failed.</summary>
    Failed,
}
