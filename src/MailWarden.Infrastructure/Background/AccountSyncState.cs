namespace MailWarden.Infrastructure.Background;

/// <summary>
/// State of the background loop of one account (<see cref="MailboxService"/>).
/// </summary>
public enum AccountSyncState
{
    /// <summary>The last cycle succeeded; the loop waits for the next poll or a trigger.</summary>
    Idle = 1,

    /// <summary>A cycle (queued commands, then sync) is running.</summary>
    Running = 2,

    /// <summary>The last cycle failed; the loop waits for the backoff to elapse or for a retry.</summary>
    WaitingAfterError = 3,

    /// <summary>The account needs an interactive sign-in; no cycle runs until a retry.</summary>
    NeedsSignIn = 4,

    /// <summary>The loop has ended (service stopped or account removed).</summary>
    Stopped = 5,
}
