using MailWarden.Infrastructure.Storage;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// The files that attachments become: a chosen target for saving, and a folder of its own for opening. A file is either
/// complete or not there. It carries the mark "from the internet", so that Windows and the opening program treat it as foreign:
/// an opened file always, a saved one wherever the target drive can hold it. Saving and preparing run off the caller's thread,
/// <see cref="Clear"/> runs on it.
/// </summary>
/// <param name="openFolder">Fully qualified folder for opened attachments, <c>&lt;data&gt;\attachments-open</c>.</param>
public sealed class AttachmentFiles(string openFolder)
{
    /// <summary>
    /// Key in <see cref="Exception.Data"/> of the <see cref="IOException"/> of <see cref="PrepareForOpeningAsync"/>, set to true
    /// when the mark could not be written, as on a drive without NTFS. The caller tells the user that this is the reason.
    /// </summary>
    public const string MarkFailureDataKey = "MailWarden.AttachmentMarkFailure";

    // Windows reads the zone of a file from this alternate stream; 3 is the zone of the internet.
    private const string ZoneStreamSuffix = ":Zone.Identifier";
    private const string ZoneMark = "[ZoneTransfer]\r\nZoneId=3\r\n";

    private readonly string openFolder = NormaliseFolder(openFolder);

    /// <summary>
    /// Writes the content beside the target, marks it as from the internet and moves it over the target, so the target never
    /// exists without the mark where the drive can hold one. A drive that cannot, such as a USB stick without NTFS, gets the file
    /// without it: the save does not fail for that. On any failure and on cancellation the temporary file is gone and an empty
    /// target, as the save dialog of Windows leaves it, is removed; an existing target with content stays.
    /// </summary>
    /// <param name="writeContent">Writes the content to the stream it gets.</param>
    /// <param name="targetPath">Fully qualified path the user chose.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the target is complete.</returns>
    /// <exception cref="ArgumentException"><paramref name="targetPath"/> is empty, not fully qualified or has no folder.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="writeContent"/> is null.</exception>
    /// <exception cref="IOException">Writing failed, for instance because the disk is full.</exception>
    /// <exception cref="UnauthorizedAccessException">Writing is not allowed.</exception>
    /// <exception cref="OperationCanceledException">The save was cancelled.</exception>
    public Task SaveAsync(Func<Stream, CancellationToken, Task> writeContent, string targetPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writeContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!Path.IsPathFullyQualified(targetPath))
        {
            throw new ArgumentException("The target must be a fully qualified path.", nameof(targetPath));
        }

        // Fail fast: without a folder the temporary file would land in the current directory of the process.
        string targetFolder = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException("The target must lie in a folder.", nameof(targetPath));

        // Without the token: a cancelled save still has to remove the empty target of the dialog.
        return Task.Run(() => SaveCoreAsync(writeContent, targetFolder, targetPath, ct), CancellationToken.None);
    }

    /// <summary>
    /// Writes the content to <c>&lt;openFolder&gt;\&lt;guid&gt;\&lt;safeFileName&gt;</c> and marks the file as from the internet.
    /// The mark is read back: without it there is no file to open.
    /// </summary>
    /// <param name="writeContent">Writes the content to the stream it gets.</param>
    /// <param name="safeFileName">A name made by <c>AttachmentFileName.ToSafe</c>, without any path part.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full path of the file, below the open folder.</returns>
    /// <exception cref="ArgumentException">The name would not lead to a file directly in the new folder, or it names a device.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="writeContent"/> is null.</exception>
    /// <exception cref="IOException">
    /// Writing failed, or the mark could not be written (<see cref="MarkFailureDataKey"/>). No file stays.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">Writing is not allowed. No file stays.</exception>
    /// <exception cref="OperationCanceledException">The copy was cancelled. No file stays.</exception>
    public Task<string> PrepareForOpeningAsync(Func<Stream, CancellationToken, Task> writeContent, string safeFileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writeContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeFileName);
        string fileFolder = Path.Combine(openFolder, Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(fileFolder, safeFileName);
        if (!IsDirectlyIn(filePath, fileFolder))
        {
            throw new ArgumentException("The name must not lead out of its folder.", nameof(safeFileName));
        }

        return Task.Run(() => PrepareCoreAsync(writeContent, fileFolder, filePath, ct), ct);
    }

    /// <summary>
    /// Empties the open folder as far as possible: what a program still holds open stays until the next call, and so does
    /// whatever else cannot be deleted. A link (junction, symbolic link) is removed as a link and never followed, so nothing outside
    /// the folder is touched. Never throws for that.
    /// </summary>
    public void Clear()
    {
        try
        {
            var folder = new DirectoryInfo(openFolder);
            if (!folder.Exists)
            {
                return;
            }

            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteEmptyFolderBestEffort(openFolder);
                return;
            }

            EmptyFolder(openFolder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ignored on purpose: the next start tries again.
        }
    }

    private static string NormaliseFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Path.IsPathFullyQualified(folder))
        {
            throw new ArgumentException("The open folder must be a fully qualified path.", nameof(folder));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    // The normalised path must be the same text: Windows would drop trailing dots and spaces, and ".." changes it. A stream or a
    // device would look like a file below the folder and is none.
    private static bool IsDirectlyIn(string filePath, string folder)
    {
        string fileName = Path.GetFileName(filePath);
        return Path.GetFullPath(filePath) == filePath
            && Path.GetDirectoryName(filePath) == folder
            && !fileName.Contains(':', StringComparison.Ordinal)
            && !DataPaths.IsDosDeviceName(fileName);
    }

    // A walk of its own instead of a recursive listing or delete, because every entry is looked at before anything follows it. Without
    // a call stack: the depth of the folders is not ours to limit.
    private static void EmptyFolder(string root)
    {
        var foldersToWalk = new Stack<string>([root]);
        var foundFolders = new List<string>();
        while (foldersToWalk.TryPop(out string? folder))
        {
            foreach (FileSystemInfo entry in ListEntriesBestEffort(folder))
            {
                bool isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                bool isFolder = entry.Attributes.HasFlag(FileAttributes.Directory);
                if (isFolder && isLink)
                {
                    DeleteEmptyFolderBestEffort(entry.FullName);
                }
                else if (isFolder)
                {
                    foldersToWalk.Push(entry.FullName);
                    foundFolders.Add(entry.FullName);
                }
                else if (isLink)
                {
                    DeleteLinkToFileBestEffort(entry.FullName);
                }
                else
                {
                    DeleteFileBestEffort(entry.FullName);
                }
            }
        }

        // Parents were found before their children, so the reverse order is deepest first. A folder that still holds a locked file stays.
        for (int index = foundFolders.Count - 1; index >= 0; index--)
        {
            DeleteEmptyFolderBestEffort(foundFolders[index]);
        }
    }

    // One level only, and the attributes are those of the entry itself, not of what a link points to.
    private static FileSystemInfo[] ListEntriesBestEffort(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).GetFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task SaveCoreAsync(Func<Stream, CancellationToken, Task> writeContent, string targetFolder, string targetPath, CancellationToken ct)
    {
        // Short: a name that just fits on the disk must not fail because of this one.
        string temporaryPath = Path.Combine(targetFolder, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteFileAsync(writeContent, temporaryPath, ct).ConfigureAwait(false);

            // Beside the target means on its drive, so the move is a rename and takes the stream along. The result is optional here.
            _ = TryWriteMark(temporaryPath);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch
        {
            DeleteFileBestEffort(temporaryPath);
            DeleteIfEmptyBestEffort(targetPath);
            throw;
        }
    }

    private static async Task<string> PrepareCoreAsync(Func<Stream, CancellationToken, Task> writeContent, string fileFolder, string filePath, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(fileFolder);
            await WriteFileAsync(writeContent, filePath, ct).ConfigureAwait(false);
            if (!TryWriteMark(filePath))
            {
                // Names no path.
                var failure = new IOException("The mark of the file could not be written.");
                failure.Data[MarkFailureDataKey] = true;
                throw failure;
            }

            return filePath;
        }
        catch
        {
            DeleteFolderBestEffort(fileFolder);
            throw;
        }
    }

    private static async Task WriteFileAsync(Func<Stream, CancellationToken, Task> writeContent, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await AtomicFile.WriteNewFileAsync(path, writeContent, ct).ConfigureAwait(false);
    }

    // Read back, because a file system may accept the stream without keeping it.
    private static bool TryWriteMark(string filePath)
    {
        string markPath = filePath + ZoneStreamSuffix;
        try
        {
            File.WriteAllText(markPath, ZoneMark);
            return File.ReadAllText(markPath) == ZoneMark;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    // Best effort like everything below: the original exception (or success) matters more than a leftover in a folder of our own.
    private static void DeleteFolderBestEffort(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ignored on purpose, see comment above; the next start empties the folder.
        }
    }

    // Not recursive: for a link to a folder this removes the link and leaves its target alone.
    private static void DeleteEmptyFolderBestEffort(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ignored on purpose: it still holds a locked file.
        }
    }

    // No change of attributes: that would reach the target of the link.
    private static void DeleteLinkToFileBestEffort(string link)
    {
        try
        {
            File.Delete(link);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ignored on purpose: the next start tries again.
        }
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            // A viewer may have set it read-only, which alone would keep it here for good.
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ignored on purpose: locked or already gone, and the unique name keeps it apart from real data.
        }
    }

    // Only what the save dialog can have left: content the user had before is not touched.
    private static void DeleteIfEmptyBestEffort(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length == 0)
            {
                file.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ignored on purpose, the failure of the save is what counts.
        }
    }
}
