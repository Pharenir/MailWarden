namespace MailWarden.Core.Storage;

/// <summary>
/// The EML file referenced by a message does not exist. Never replaced by empty content.
/// </summary>
public sealed class EmlNotFoundException : StorageException
{
    /// <summary>Creates the exception; the message contains the relative path.</summary>
    /// <param name="relativePath">Path relative to the mail root, as passed by the caller.</param>
    public EmlNotFoundException(string relativePath)
        : base($"EML file not found: {relativePath}")
    {
        RelativePath = relativePath;
    }

    /// <summary>Creates the exception for a file that vanished while it was being opened.</summary>
    /// <param name="relativePath">Path relative to the mail root, as passed by the caller.</param>
    /// <param name="inner">Original file system exception.</param>
    public EmlNotFoundException(string relativePath, Exception inner)
        : base($"EML file not found: {relativePath}", inner)
    {
        RelativePath = relativePath;
    }

    /// <summary>Path relative to the mail root, as passed by the caller.</summary>
    public string RelativePath { get; }
}
