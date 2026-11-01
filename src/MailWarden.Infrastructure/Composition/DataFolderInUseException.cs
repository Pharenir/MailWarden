namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// The data folder is locked by another MailWarden instance. Thrown before the database is touched.
/// </summary>
public sealed class DataFolderInUseException : Exception
{
    /// <summary>Creates the exception with its fixed message.</summary>
    public DataFolderInUseException()
        : base("The data folder is in use by another MailWarden instance.")
    {
    }
}
