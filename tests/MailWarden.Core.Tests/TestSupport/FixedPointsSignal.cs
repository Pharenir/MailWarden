namespace MailWarden.Core.Tests.TestSupport;

/// <summary>Stub signal returning fixed points (or no finding when <c>points</c> is null).</summary>
internal sealed class FixedPointsSignal(string id, int? points) : ISpamSignal
{
    public string Id { get; } = id;

    public SignalResult? Evaluate(ClassificationInput input, ClassificationContext context) =>
        points is null ? null : new SignalResult(Id, points.Value, $"Fixed {points.Value} points from stub '{Id}'");
}
