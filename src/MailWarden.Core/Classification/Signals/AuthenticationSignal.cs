using System.Globalization;

namespace MailWarden.Core.Classification.Signals;

/// <summary>
/// "authentication": −20 when authenticated; otherwise DMARC fail +40, SPF fail +20, SPF softfail +10, all DKIM
/// signatures fail +20, capped at +50; no finding when nothing failed (e.g. no header).
/// </summary>
internal sealed class AuthenticationSignal : ISpamSignal
{
    /// <summary>Signal id (part of the contract).</summary>
    public const string SignalId = "authentication";

    private const int AuthenticatedPoints = -20;
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
                $"Sender domain '{context.FromDomain}' is authenticated: {passDescription}.");
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
            $"Authentication of sender domain '{context.FromDomain}' failed: {failureList}{capNote}.");
    }
}
