namespace MailWarden.Core.Classification;

/// <summary>
/// The four classification levels, ordered from most trusted to most suspicious.
/// </summary>
public enum SpamTier
{
    /// <summary>Known contact (or allow-listed sender) whose authentication is not in doubt.</summary>
    TrustedContact,

    /// <summary>Unknown sender without strong spam evidence; lands in the screener.</summary>
    LikelyLegit,

    /// <summary>Score reached the first threshold.</summary>
    LikelySpam,

    /// <summary>Score reached the second threshold, or the sender is block-listed.</summary>
    CertainSpam,
}
