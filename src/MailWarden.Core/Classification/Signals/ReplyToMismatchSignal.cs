namespace MailWarden.Core.Classification.Signals;

/// <summary>
/// "reply-to-mismatch": Reply-To present and not aligned with the From domain → +15 when the Reply-To is a freemail
/// address while From is not, otherwise +10. A Reply-To without a usable domain gives no finding.
/// </summary>
internal sealed class ReplyToMismatchSignal : ISpamSignal
{
    /// <summary>Signal id (part of the contract).</summary>
    public const string SignalId = "reply-to-mismatch";

    private const int FreemailPoints = 15;
    private const int OtherDomainPoints = 10;

    /// <inheritdoc/>
    public string Id => SignalId;

    /// <inheritdoc/>
    public SignalResult? Evaluate(ClassificationInput input, ClassificationContext context)
    {
        var replyToDomain = DomainNames.TryGetDomain(input.ReplyToAddress);
        var replyToOrganizationalDomain = DomainNames.TryGetOrganizationalDomain(replyToDomain);
        if (replyToDomain is null
            || replyToOrganizationalDomain is null
            || string.Equals(replyToOrganizationalDomain, context.FromOrganizationalDomain, StringComparison.Ordinal))
        {
            return null;
        }

        var replyToIsFreemail = context.Settings.IsFreemailDomain(replyToDomain)
            || context.Settings.IsFreemailDomain(replyToOrganizationalDomain);
        if (replyToIsFreemail && !context.FromIsFreemail)
        {
            return new SignalResult(
                SignalId,
                FreemailPoints,
                $"Reply-To goes to the freemail domain '{ReasonText.Truncate(replyToDomain)}' instead of the sender domain '{ReasonText.Truncate(context.FromDomain)}'.");
        }

        return new SignalResult(
            SignalId,
            OtherDomainPoints,
            $"Reply-To domain '{ReasonText.Truncate(replyToDomain)}' differs from the sender domain '{ReasonText.Truncate(context.FromDomain)}'.");
    }
}
