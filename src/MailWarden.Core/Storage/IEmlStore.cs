namespace MailWarden.Core.Storage;

/// <summary>
/// Stores raw messages (EML files) below the mail root. All paths are relative to the mail root and use forward
/// slashes.
/// </summary>
public interface IEmlStore
{
    /// <summary>Builds the storage path of a message.</summary>
    /// <param name="accountId">Owning account.</param>
    /// <param name="folderId">Database id of the folder (greater than 0).</param>
    /// <param name="uid">IMAP UID (greater than 0).</param>
    /// <returns><c>accounts/{accountId:D}/folders/{folderId}/{uid}.eml</c>.</returns>
    string RelativePathFor(Guid accountId, long folderId, uint uid);

    /// <summary>
    /// Writes the content to the path. Creates missing directories, writes atomically (temp file + move) and
    /// overwrites an existing file.
    /// </summary>
    /// <param name="relativePath">Path relative to the mail root.</param>
    /// <param name="content">Raw message; read from its current position to the end, not disposed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the file is in place.</returns>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is not a valid path below the mail root.</exception>
    /// <exception cref="StorageException">
    /// Writing failed, including overwriting a file for which a stream from <see cref="OpenReadAsync"/> is still open.
    /// </exception>
    Task WriteAsync(string relativePath, Stream content, CancellationToken ct = default);

    /// <summary>Opens an EML file for reading. The caller disposes the stream.</summary>
    /// <param name="relativePath">Path relative to the mail root.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A readable stream positioned at the start of the file.</returns>
    /// <exception cref="EmlNotFoundException">The file does not exist.</exception>
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Checks whether the EML file exists.</summary>
    /// <param name="relativePath">Path relative to the mail root.</param>
    /// <returns>True when the file exists.</returns>
    bool Exists(string relativePath);

    /// <summary>Deletes the EML file. Missing file = no-op.</summary>
    /// <param name="relativePath">Path relative to the mail root.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the file is gone.</returns>
    Task DeleteAsync(string relativePath, CancellationToken ct = default);
}
