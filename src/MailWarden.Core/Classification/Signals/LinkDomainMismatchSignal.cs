using System.Globalization;

namespace MailWarden.Core.Classification.Signals;

/// <summary>
/// "link-domain-mismatch": non-freemail sender, at least one link host, and at least 80 % of the distinct link hosts
/// (case-insensitive, trailing '.' ignored) are not aligned with the From domain → +10. Empty host values are ignored.
/// </summary>
internal sealed class LinkDomainMismatchSignal : ISpamSignal
{
    /// <summary>Signal id (part of the contract).</summary>
    public const string SignalId = "link-domain-mismatch";

    private const int Points = 10;

    // 80 % as an exact integer ratio: notAligned / distinct >= 4 / 5.
    private const int ShareNumerator = 4;
    private const int ShareDenominator = 5;

    /// <inheritdoc/>
    public string Id => SignalId;

    /// <inheritdoc/>
    public SignalResult? Evaluate(ClassificationInput input, ClassificationContext context)
    {
        if (context.FromIsFreemail)
        {
            return null;
        }

        var distinctHosts = input.LinkHosts
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(DomainNames.Normalize)
            .Where(host => DomainNames.TryGetOrganizationalDomain(host) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinctHosts.Count == 0)
        {
            return null;
        }

        var notAlignedHosts = distinctHosts
            .Where(host => !string.Equals(
                DomainNames.TryGetOrganizationalDomain(host),
                context.FromOrganizationalDomain,
                StringComparison.Ordinal))
            .ToList();
        if (notAlignedHosts.Count * ShareDenominator < distinctHosts.Count * ShareNumerator)
        {
            return null;
        }

        return new SignalResult(
            SignalId,
            Points,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{notAlignedHosts.Count} of {distinctHosts.Count} distinct link hosts are not aligned with the sender domain '{context.FromDomain}' (e.g. '{notAlignedHosts[0]}')."));
    }
}
