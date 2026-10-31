namespace MailWarden.Core.Commands;

/// <summary>
/// Kind of a queued server change. The numeric values are stored in the database; never renumber them.
/// </summary>
public enum MailCommandKind
{
    /// <summary>Adds and/or removes system flags (Seen, Flagged, Answered, Draft).</summary>
    SetFlags = 1,

    /// <summary>Moves messages to another folder of the same account.</summary>
    Move = 2,

    /// <summary>Moves messages to the account's folder with role Trash (Del key).</summary>
    MoveToTrash = 3,

    /// <summary>Deletes messages permanently (Shift+Del, "Ordner leeren").</summary>
    DeletePermanently = 4,
}
