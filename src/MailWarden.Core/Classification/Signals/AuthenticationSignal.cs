using System.Globalization;

namespace MailWarden.Core.Classification.Signals;

/// <summary>
/// "authentication": −20 when authenticated; 0 points with an explaining reason when DMARC failed but Microsoft's
/// compauth passed (AP-07b E1, no failure points at all); otherwise DMARC fail +40, SPF fail +20, SPF softfail +10,
/// all DKIM signatures fail +20, capped at +50; no finding when nothing failed (e.g. no header).
/// </summary>
internal sealed class AuthenticationSignal : ISpamSignal
{
    /// <summary>Signal id (part of the contract).</summary>
    public const string SignalId = "authentication";

    private const int AuthenticatedPoints = -20;
    private const int CompAuthOverrulePoints = 0;
    private const int DmarcFailPoints = 40;
    private const int SpfFailPoints = 20;
    private const int SpfSoftFailPoints = 10;
    private const int AllDkimFailPoints = 20;
    private const int MaximumFailurePoints = 50;

    /// <inheritdoc/>
    public string Id => SignalId;

    /// <inheritdoc/>
    public SignalResult? Evaluate(ClassificationInput input, ClassificationContext context)
    {
        var authentication = context.Authentication;
        if (context.AuthStatus == AuthStatus.Authenticated)
        {
            var passDescription = AuthStatusEvaluator.DescribePass(authentication, context.FromDomain);
            return new SignalResult(
                SignalId,
                AuthenticatedPoints,
                $"Sender domain '{ReasonText.Truncate(context.FromDomain)}' is authenticated: {passDescription}.");
        }

        // E1: the provider's composite authentication overrules its DMARC fail; no SPF/DKIM points either, the sender
        // is only unverified. The finding is kept (0 points) so the "Why?" view explains the decision.
        if (AuthStatusEvaluator.IsDmarcFailOverruledByCompAuth(authentication))
        {
            return new SignalResult(
                SignalId,
                CompAuthOverrulePoints,
                $"DMARC failed for sender domain '{ReasonText.Truncate(context.FromDomain)}', but the provider's composite authentication (compauth) passed; the sender is treated as unverified.");
        }

        // Collect every failed check with its points.
        var failures = new List<(string Description, int Points)>();
        if (authentication.Dmarc?.Verdict == AuthVerdict.Fail)
        {
            failures.Add(("DMARC fail", DmarcFailPoints));
        }

        if (authentication.Spf?.Verdict == AuthVerdict.Fail)
        {
            failures.Add(("SPF fail", SpfFailPoints));
        }
        else if (authentication.Spf?.Verdict == AuthVerdict.SoftFail)
        {
            failures.Add(("SPF softfail", SpfSoftFailPoints));
        }

        if (AuthStatusEvaluator.AllDkimFailed(authentication))
        {
            failures.Add(("all DKIM signatures fail", AllDkimFailPoints));
        }

        var uncappedPoints = failures.Sum(failure => failure.Points);
        if (uncappedPoints == 0)
        {
            return null;
        }

        var points = Math.Min(uncappedPoints, MaximumFailurePoints);
        var failureList = string.Join(
            ", ",
            failures.Select(failure => string.Create(CultureInfo.InvariantCulture, $"{failure.Description} (+{failure.Points})")));
        var capNote = uncappedPoints > MaximumFailurePoints
            ? string.Create(CultureInfo.InvariantCulture, $", capped at +{MaximumFailurePoints}")
            : string.Empty;
        return new SignalResult(
            SignalId,
            points,
            $"Authentication of sender domain '{ReasonText.Truncate(context.FromDomain)}' failed: {failureList}{capNote}.");
    }
}
