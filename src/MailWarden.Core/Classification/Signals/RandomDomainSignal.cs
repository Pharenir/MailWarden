using System.Globalization;

namespace MailWarden.Core.Classification.Signals;

/// <summary>
/// "random-domain": <see cref="RandomDomainScorer"/> of the registrable label of the From domain ≥ 0.8 → +35,
/// ≥ 0.5 → +15. Never for freemail senders (their domain says nothing about the sender). For authenticated senders
/// only the strong band counts (AP-07b E3: the weak band hits abbreviation domains of legitimate senders).
/// </summary>
internal sealed class RandomDomainSignal : ISpamSignal
{
    /// <summary>Signal id (part of the contract).</summary>
    public const string SignalId = "random-domain";

    private const double StrongThreshold = 0.8;
    private const double WeakThreshold = 0.5;
    private const int StrongPoints = 35;
    private const int WeakPoints = 15;

    /// <inheritdoc/>
    public string Id => SignalId;

    /// <inheritdoc/>
    public SignalResult? Evaluate(ClassificationInput input, ClassificationContext context)
    {
        if (context.FromIsFreemail)
        {
            return null;
        }

        var label = DomainNames.GetRegistrableLabel(context.FromDomain);
        var score = RandomDomainScorer.Score(label);
        if (score < WeakThreshold)
        {
            return null;
        }

        var isWeakBand = score < StrongThreshold;
        if (isWeakBand && context.AuthStatus == AuthStatus.Authenticated)
        {
            return null;
        }

        // Shown truncated to two decimals so the text never claims a higher score than the decision used.
        var shownScore = Math.Floor((score * 100) + 1e-9) / 100;
        var points = isWeakBand ? WeakPoints : StrongPoints;
        return new SignalResult(
            SignalId,
            points,
            string.Create(CultureInfo.InvariantCulture, $"Sender domain label '{ReasonText.Truncate(label)}' looks random (score {shownScore:0.00})."));
    }
}
