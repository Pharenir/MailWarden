namespace MailWarden.Core.Classification.Signals;

/// <summary>
/// "domain-not-in-body": non-freemail sender, non-empty body, the registrable label of the From domain does not occur
/// in the body text (case-insensitive substring) and no link host is aligned with the From domain → +15.
/// </summary>
internal sealed class DomainNotInBodySignal : ISpamSignal
{
    /// <summary>Signal id (part of the contract).</summary>
    public const string SignalId = "domain-not-in-body";

    private const int Points = 15;

    /// <inheritdoc/>
    public string Id => SignalId;

    /// <inheritdoc/>
    public SignalResult? Evaluate(ClassificationInput input, ClassificationContext context)
    {
        // Whitespace-only bodies count as empty: there is nothing to judge.
        if (context.FromIsFreemail || string.IsNullOrWhiteSpace(input.BodyText))
        {
            return null;
        }

        var label = DomainNames.GetRegistrableLabel(context.FromDomain);
        if (input.BodyText.Contains(label, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hasAlignedLink = input.LinkHosts.Any(host => string.Equals(
            DomainNames.TryGetOrganizationalDomain(host),
            context.FromOrganizationalDomain,
            StringComparison.Ordinal));
        if (hasAlignedLink)
        {
            return null;
        }

        return new SignalResult(
            SignalId,
            Points,
            $"Sender domain label '{label}' ({context.FromDomain}) occurs neither in the body text nor in a link host.");
    }
}
