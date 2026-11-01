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
}
