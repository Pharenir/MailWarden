namespace MailWarden.Core.Classification;

/// <summary>
/// Parsed Authentication-Results header. <paramref name="Dkim"/> may contain several signatures in header order;
/// <paramref name="Spf"/> and <paramref name="Dmarc"/> are null when the method is not present.
/// </summary>
/// <param name="AuthServId">Identifier of the checking server (e.g. "mx.google.com"); null when the header has none.</param>
/// <param name="Spf">First SPF result, null when absent.</param>
/// <param name="Dkim">All DKIM results in header order (may be empty).</param>
/// <param name="Dmarc">First DMARC result, null when absent.</param>
public sealed record AuthenticationResults(
    string? AuthServId,
    AuthMethodResult? Spf,
    IReadOnlyList<AuthMethodResult> Dkim,
    AuthMethodResult? Dmarc)
{
    /// <summary>Result without any information (no header, or nothing parseable).</summary>
    public static AuthenticationResults Empty { get; } = new(null, null, Array.Empty<AuthMethodResult>(), null);

    /// <summary>
    /// True when the kept DMARC result has a <c>header.from</c> property that is present but yields no domain
    /// (e.g. <c>header.from=""</c>). Distinguishes that ambiguous case from an absent <c>header.from</c>, which both
    /// give <c>Dmarc.Domain == null</c>; a DMARC pass is not trusted in the first case (planner decision C22).
    /// Set by <see cref="AuthenticationResultsParser"/> only.
    /// </summary>
    internal bool IsDmarcHeaderFromWithoutDomain { get; init; }
}
