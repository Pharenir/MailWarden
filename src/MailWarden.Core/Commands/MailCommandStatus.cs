namespace MailWarden.Core.Commands;

/// <summary>
/// State of a queued command. Completed commands are removed from the queue, so there is no "done" state. The numeric
/// values are stored in the database; never renumber them.
/// </summary>
public enum MailCommandStatus
{
    /// <summary>Waiting for its (next) execution.</summary>
    Pending = 1,

    /// <summary>Failed permanently; never retried. Stays visible until discarded.</summary>
    Failed = 2,
}
