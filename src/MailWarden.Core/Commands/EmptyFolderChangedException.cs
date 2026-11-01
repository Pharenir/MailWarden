namespace MailWarden.Core.Commands;

/// <summary>
/// "Ordner leeren" was not stored: the folder no longer holds only what the user confirmed. Nothing was changed. This is not
/// "nothing to delete", which the queue reports with null.
/// </summary>
public sealed class EmptyFolderChangedException : Exception
{
    /// <summary>Creates the exception with a fixed text.</summary>
    public EmptyFolderChangedException()
        : base("The folder has changed since the confirmation; nothing was stored.")
    {
    }
}
