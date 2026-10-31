namespace MailWarden.Core.Accounts;

/// <summary>
/// Server-side folder flags (IMAP LIST / SPECIAL-USE attributes) in provider-neutral form. Infrastructure maps the
/// IMAP library's attributes to this type.
/// </summary>
[Flags]
public enum ServerFolderFlags
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>The inbox.</summary>
    Inbox = 1,

    /// <summary><c>\Sent</c>.</summary>
    Sent = 2,

    /// <summary><c>\Drafts</c>.</summary>
    Drafts = 4,

    /// <summary><c>\Junk</c>.</summary>
    Junk = 8,

    /// <summary><c>\Trash</c>.</summary>
    Trash = 16,

    /// <summary><c>\Archive</c>.</summary>
    Archive = 32,

    /// <summary><c>\All</c> (Gmail "All Mail").</summary>
    All = 64,

    /// <summary><c>\Flagged</c> (virtual folder of flagged messages, Gmail "Starred").</summary>
    Flagged = 128,

    /// <summary><c>\Important</c> (Gmail virtual folder).</summary>
    Important = 256,

    /// <summary><c>\Noselect</c>: the folder cannot hold messages.</summary>
    NoSelect = 512,

    /// <summary><c>\NonExistent</c>: pure hierarchy container.</summary>
    NonExistent = 1024,
}
