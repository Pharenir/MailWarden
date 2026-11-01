namespace MailWarden.Core.Classification;

/// <summary>
/// Move a decision asks for, independent of whether it was carried out (see <see cref="ClassificationOutcome"/>).
/// </summary>
public enum ClassificationAction
{
    /// <summary>The message stays where it is.</summary>
    None = 0,

    /// <summary>Rescue from Junk to the Inbox.</summary>
    MoveToInbox = 1,

    /// <summary>Unknown but plausible sender: to the Screener.</summary>
    MoveToScreener = 2,

    /// <summary>Likely spam: to the Junk folder.</summary>
    MoveToJunk = 3,

    /// <summary>Certain spam: to the Quarantine.</summary>
    MoveToQuarantine = 4,
}
