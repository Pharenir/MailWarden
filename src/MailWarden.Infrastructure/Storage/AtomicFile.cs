namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// Writes a file completely or not at all: a temporary file next to the target, then one move.
/// </summary>
public static class AtomicFile
{
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
