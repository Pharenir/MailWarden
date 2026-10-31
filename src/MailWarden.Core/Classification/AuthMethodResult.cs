namespace MailWarden.Core.Classification;

/// <summary>
/// Result of one method in an Authentication-Results header. Domain: SPF = domain of <c>smtp.mailfrom</c> (address
/// part after '@' if an address is given), DKIM = <c>header.d</c> (or the domain of <c>header.i</c> when
/// <c>header.d</c> is absent), DMARC = <c>header.from</c>.
/// </summary>
/// <param name="Verdict">Result keyword of the method.</param>
/// <param name="Domain">Domain the result refers to; lower-case without trailing '.', null when absent.</param>
public sealed record AuthMethodResult(AuthVerdict Verdict, string? Domain);
