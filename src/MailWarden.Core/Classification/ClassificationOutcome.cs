namespace MailWarden.Core.Classification;

/// <summary>
/// What became of one decision in a classification pass.
/// </summary>
public enum ClassificationOutcome
{
    /// <summary>Decided; the routing says the message stays.</summary>
    NoAction = 1,

    /// <summary>An action was planned but the effective mode is RecordOnly (also while Act is suspended).</summary>
    Recorded = 2,

    /// <summary>The move command is stored in the queue.</summary>
    Enqueued = 3,

    /// <summary>The account has no folder for the target role; the message stays.</summary>
    TargetMissing = 4,

    /// <summary>The queue rejected the command; the message stays.</summary>
    EnqueueFailed = 5,

    /// <summary>EML missing, unreadable or no message at all; no tier. The message stays where it is and this outcome is
    /// final.</summary>
    ReadFailed = 6,

    /// <summary>The message parsed, but building the classifier input or classifying it threw. This points to a programming
    /// error rather than to the sender, so nothing moves: no tier, no action, the message stays and this outcome is
    /// final.</summary>
    EvaluationFailed = 7,

    /// <summary>The move was planned in effective Act, but the moves planned for this folder in this pass exceed
    /// <see cref="ClassificationPolicy.MaxMovesPerPass"/>, so none of them is enqueued. The planned action stays visible,
    /// the message stays where it is and this outcome is final.</summary>
    BrakeHeld = 8,
}
