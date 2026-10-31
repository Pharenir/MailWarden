namespace MailWarden.Core.Model;

/// <summary>
/// IMAP system flags of a message.
/// </summary>
[Flags]
public enum MessageFlags
{
    /// <summary>No flag set.</summary>
    None = 0,

    /// <summary>\Seen – message has been read.</summary>
    Seen = 1,

    /// <summary>\Answered – message has been replied to.</summary>
    Answered = 2,

    /// <summary>\Flagged – message is marked for attention.</summary>
    Flagged = 4,

    /// <summary>\Deleted – message is marked for deletion.</summary>
    Deleted = 8,

    /// <summary>\Draft – message is a draft.</summary>
    Draft = 16,
}
