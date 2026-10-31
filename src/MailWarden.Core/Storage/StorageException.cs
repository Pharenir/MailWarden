namespace MailWarden.Core.Storage;

/// <summary>
/// Base type for all storage failures that are not programming errors (constraint violations, IO errors,
/// corrupt or incompatible data). Argument errors are reported with the regular <see cref="ArgumentException"/> family.
/// </summary>
public class StorageException : Exception
{
    /// <summary>Creates a storage exception with a message.</summary>
    /// <param name="message">Description of the failure.</param>
    public StorageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a storage exception that wraps the underlying provider exception.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="inner">Original exception (e.g. SQLite or IO error).</param>
    public StorageException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
