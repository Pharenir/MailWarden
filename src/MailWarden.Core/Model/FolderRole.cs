namespace MailWarden.Core.Model;

/// <summary>
/// Role of a folder. Stored explicitly per folder; never derived from a name inside storage.
/// <see cref="Outbox"/> exists only locally (unsent queue). <see cref="All"/> is Gmail "All Mail" and is never part
/// of group views.
/// </summary>
public enum FolderRole
{
    /// <summary>Ordinary user folder without a special role.</summary>
    None,

    /// <summary>Incoming mail.</summary>
    Inbox,

    /// <summary>Sent mail.</summary>
    Sent,

    /// <summary>Drafts.</summary>
    Drafts,

    /// <summary>Provider spam folder.</summary>
    Junk,

    /// <summary>Deleted items.</summary>
    Trash,

    /// <summary>Archive.</summary>
    Archive,

    /// <summary>MailWarden folder for unknown but plausible senders.</summary>
    Screener,

    /// <summary>MailWarden folder for certain spam, deleted after the retention period.</summary>
    Quarantine,

    /// <summary>Gmail "All Mail" (contains every message again; excluded from group views).</summary>
    All,

    /// <summary>Local queue of messages not sent yet; no server folder.</summary>
    Outbox,
}
