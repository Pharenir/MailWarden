namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// Cross-process lock of one data folder: an exclusively opened file <c>&lt;root&gt;\mailwarden.lock</c>
/// (<see cref="FileShare.None"/>, <see cref="FileOptions.DeleteOnClose"/>). Held for the lifetime of the returned object;
/// the operating system releases it when the process ends, so a crash never leaves a stale lock behind.
/// </summary>
public sealed class DataFolderLock : IDisposable
{
    /// <summary>File name of the lock below the data root.</summary>
    public const string LockFileName = "mailwarden.lock";

    // Win32 errors of a file that another handle holds: ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION.
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly FileStream lockStream;

    private DataFolderLock(FileStream lockStream)
    {
        this.lockStream = lockStream;
    }

    /// <summary>
    /// Tries to take the lock of the data folder. Creates the data root when it does not exist yet.
    /// </summary>
    /// <param name="paths">Paths of the data folder to lock.</param>
    /// <returns>The held lock, or null when another process (or another instance in this process) holds it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <exception cref="IOException">The lock file could not be opened for another reason than a held lock.</exception>
    /// <exception cref="UnauthorizedAccessException">The data root is not writable.</exception>
    public static DataFolderLock? TryAcquire(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.Root);
        string lockPath = Path.Combine(paths.Root, LockFileName);
        try
        {
            var lockStream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return new DataFolderLock(lockStream);
        }
        catch (IOException exception) when (IsHeldByOtherHandle(exception))
        {
            return null;
        }
    }

    /// <summary>Releases the lock; the lock file is deleted. Safe to call more than once.</summary>
    public void Dispose()
    {
        lockStream.Dispose();
    }

    // Only "held by another handle" means "in use"; every other IO failure (disk, path) is a real error.
    private static bool IsHeldByOtherHandle(IOException exception)
    {
        int win32Error = exception.HResult & 0xFFFF;
        return win32Error is ErrorSharingViolation or ErrorLockViolation;
    }
}
