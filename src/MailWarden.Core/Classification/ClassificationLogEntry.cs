namespace MailWarden.Core.Classification;

/// <summary>
/// The stored decision for one message. <see cref="Signals"/> and <see cref="Override"/> contain the classifier's reason
/// texts; they stay in the local database and never go to a log file, a report or an exception.
/// </summary>
/// <param name="AccountId">Owning account.</param>
/// <param name="MessageKey">Key from <see cref="Classification.MessageKey.For"/>.</param>
/// <param name="SourceFolderId">Folder the message was in when it was decided.</param>
/// <param name="SourceUid">UID of the message in that folder.</param>
/// <param name="ClassifiedAt">Decision time; the clock of the quarantine retention.</param>
/// <param name="EffectiveMode">Mode the decision was made in (RecordOnly also while Act is suspended).</param>
/// <param name="Outcome">What became of the decision.</param>
/// <param name="PlannedAction">Action of the routing table; <see cref="ClassificationAction.None"/> without a tier.</param>
/// <param name="Tier">Decided tier, null for <see cref="ClassificationOutcome.ReadFailed"/>.</param>
/// <param name="Score">Sum of the points; 0 without a tier.</param>
/// <param name="AuthStatus">Authentication state of the decision, null without a tier.</param>
/// <param name="Override">Reason text when an allow/block/contact rule decided, else null.</param>
/// <param name="Signals">All findings; empty without a tier.</param>
public sealed record ClassificationLogEntry(
    Guid AccountId,
    string MessageKey,
    long SourceFolderId,
    uint SourceUid,
    DateTimeOffset ClassifiedAt,
    ClassificationMode EffectiveMode,
    ClassificationOutcome Outcome,
    ClassificationAction PlannedAction,
    SpamTier? Tier,
    int Score,
    AuthStatus? AuthStatus,
    string? Override,
    IReadOnlyList<SignalResult> Signals);
