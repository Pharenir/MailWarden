namespace MailWarden.Core.Classification;

/// <summary>
/// Final decision for one message.
/// </summary>
/// <param name="Tier">Decided tier.</param>
/// <param name="Score">Sum of the points of all findings.</param>
/// <param name="AuthStatus">Authentication state used for the decision.</param>
/// <param name="Signals">All non-null signal results in evaluation order (also when an override decided).</param>
/// <param name="Override">Reason text when an allow/block/contact rule decided the tier instead of the score, else
/// null.</param>
public sealed record ClassificationResult(
    SpamTier Tier,
    int Score,
    AuthStatus AuthStatus,
    IReadOnlyList<SignalResult> Signals,
    string? Override);
