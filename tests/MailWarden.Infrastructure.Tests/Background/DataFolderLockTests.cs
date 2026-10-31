namespace MailWarden.Infrastructure.Tests.Background;

/// <summary>AP-03d case 1: cross-process data-folder lock (D1), exercised within one process.</summary>
[Trait("Category", "feature:sync")]
[Trait("Category", "feature:storage")]
public sealed class DataFolderLockTests : IDisposable
{
    private const string LockFileName = "mailwarden.lock";

    private readonly string _baseDirectory = Path.Combine(AppContext.BaseDirectory, "ap03d-lock", Guid.NewGuid().ToString("N"));

    public DataFolderLockTests() => Directory.CreateDirectory(_baseDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a failing test may leave the lock file open.
        }
    }

    private DataPaths NewPaths(string name = "data")
    {
        string root = Path.Combine(_baseDirectory, name);
        Directory.CreateDirectory(root);
        return new DataPaths(root);
    }

    // Case 1
    [Fact]
    public void Second_acquire_returns_null_and_after_dispose_acquire_works_again_and_lock_file_is_gone()
    {
        DataPaths paths = NewPaths();
        string lockFile = Path.Combine(paths.Root, LockFileName);

        DataFolderLock? first = DataFolderLock.TryAcquire(paths);
        Assert.NotNull(first);
        Assert.True(File.Exists(lockFile));
        Assert.Null(DataFolderLock.TryAcquire(paths));

        first.Dispose();
        Assert.False(File.Exists(lockFile));

        DataFolderLock? again = DataFolderLock.TryAcquire(paths);
        Assert.NotNull(again);
        again.Dispose();
        Assert.False(File.Exists(lockFile));
    }

    [Fact]
    public void Locks_of_different_data_folders_are_independent()
    {
        using DataFolderLock? first = DataFolderLock.TryAcquire(NewPaths("data_a"));
        using DataFolderLock? second = DataFolderLock.TryAcquire(NewPaths("data_b"));

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void Dispose_twice_does_not_throw()
    {
        DataFolderLock? held = DataFolderLock.TryAcquire(NewPaths());
        Assert.NotNull(held);

        held.Dispose();
        held.Dispose();
    }
}
