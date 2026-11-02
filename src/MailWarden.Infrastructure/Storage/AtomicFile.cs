namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// Writes a file completely or not at all: a temporary file next to the target, then one move.
/// </summary>
public static class AtomicFile
{
    /// <summary>Buffer of the file streams that copy mail content.</summary>
    internal const int StreamBufferSize = 81920;

    /// <summary>Creates missing directories and replaces the file with the content.</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="content">The complete new content.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    /// <exception cref="IOException">Writing failed; an existing file stays unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">Writing is not allowed; an existing file stays unchanged.</exception>
    public static void WriteAllBytes(string path, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            DeleteBestEffort(temporaryPath);
        }
    }

    /// <summary>
    /// Creates a file that must not exist yet, opened for this writer alone, lets the callback write it and flushes it to disk. The
    /// one way to write a temporary file from a stream; moving it and removing it after a failure stay with the caller.
    /// </summary>
    /// <param name="path">Path of the new file.</param>
    /// <param name="writeContent">Writes the content to the stream it gets.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the content is on the disk and the file is closed.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="writeContent"/> is null.</exception>
    /// <exception cref="IOException">Creating or writing failed, also when the file exists.</exception>
    /// <exception cref="UnauthorizedAccessException">Writing is not allowed.</exception>
    /// <exception cref="OperationCanceledException">The write was cancelled.</exception>
    public static async Task WriteNewFileAsync(string path, Func<Stream, CancellationToken, Task> writeContent, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writeContent);

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            BufferSize = StreamBufferSize,
        };
        var file = new FileStream(path, options);
        await using (file.ConfigureAwait(false))
        {
            await writeContent(file, ct).ConfigureAwait(false);
            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
        }
    }

    // After a successful move there is nothing to delete; a failure here must not hide the original exception.
    private static void DeleteBestEffort(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ignored on purpose, see comment above; the unique name never shadows real data.
        }
    }
}
