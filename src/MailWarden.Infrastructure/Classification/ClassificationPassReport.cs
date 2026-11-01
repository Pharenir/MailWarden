using MailWarden.Core.Classification;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// Counts of one classification pass; all counts are messages, not commands. Holds no message data.
/// </summary>
/// <param name="Classified">Messages that got a tier.</param>
/// <param name="SkippedKnown">Messages whose key was already decided (in the log or earlier in this pass).</param>
/// <param name="ReadFailed">Messages stored with outcome <see cref="ClassificationOutcome.ReadFailed"/>.</param>
/// <param name="MovesEnqueued">Messages covered by stored move commands.</param>
/// <param name="RetentionDeletesEnqueued">Messages newly covered by stored delete commands of the quarantine retention;
/// messages that are already in a queued command do not count.</param>
/// <param name="ActSuspendedNoContacts">True when the policy says Act but the address book is empty.</param>
/// <param name="TierCounts">Classified messages per tier; every tier is present, also with 0.</param>
/// <param name="RetentionDeletesRejected">Messages of retention delete commands the queue rejected; the next pass tries
/// them again.</param>
public sealed record ClassificationPassReport(
    int Classified,
    int SkippedKnown,
    int ReadFailed,
    int MovesEnqueued,
    int RetentionDeletesEnqueued,
    bool ActSuspendedNoContacts,
    IReadOnlyDictionary<SpamTier, int> TierCounts,
    int RetentionDeletesRejected);
