namespace MailWarden.Core.Classification.Signals;

/// <summary>
/// "suspicious-tld": the public suffix of the From domain (its last label for multi-label suffixes) is listed in
/// <see cref="ClassifierSettings.SuspiciousTlds"/> → +10.
/// </summary>
internal sealed class SuspiciousTldSignal : ISpamSignal
{
    /// <summary>Signal id (part of the contract).</summary>
    public const string SignalId = "suspicious-tld";

    private const int Points = 10;

    /// <inheritdoc/>
    public string Id => SignalId;

    /// <inheritdoc/>
    public SignalResult? Evaluate(ClassificationInput input, ClassificationContext context)
    {
        var publicSuffix = DomainNames.GetPublicSuffix(context.FromDomain);
        if (publicSuffix.Length == 0)
        {
            return null;
        }

        var topLevelDomain = publicSuffix[(publicSuffix.LastIndexOf('.') + 1)..];
        if (!context.Settings.IsSuspiciousTld(topLevelDomain))
        {
            return null;
        }

        return new SignalResult(
            SignalId,
            Points,
            $"Sender domain '{context.FromDomain}' uses the suspicious top-level domain '.{topLevelDomain}'.");
    }
}
