using MailWarden.Infrastructure.Classification;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Tests.Composition;

/// <summary>AP-07d D3 / case 12: <see cref="MailWardenRuntime.OpenAsync"/> loads the two classifier files once at start.</summary>
[Trait("Category", "feature:classifier")]
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class MailWardenRuntimeClassificationFilesTests : IDisposable
{
    private const string LockFileName = "mailwarden.lock";

    private readonly string _baseDirectory = Path.Combine(AppContext.BaseDirectory, "ap07d-runtime", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was created.
        }
        catch (IOException)
        {
            // Best effort: a failing test may leave a handle open.
        }
    }

    private DataPaths NewPaths()
    {
        string root = Path.Combine(_baseDirectory, "data");
        Directory.CreateDirectory(root);
        return new DataPaths(root);
    }

    private static string FilePath(DataPaths paths, string fileName) => Path.Combine(paths.Root, fileName);

    // Case 12: missing files open with defaults.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Missing_files_open_with_defaults()
    {
        DataPaths paths = NewPaths();
        Assert.False(File.Exists(FilePath(paths, ClassificationPolicyFile.FileName)));
        Assert.False(File.Exists(FilePath(paths, ClassifierSettingsFile.FileName)));

        await using MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths);

        Assert.NotNull(runtime.Service);
        Assert.True(File.Exists(FilePath(paths, LockFileName)));
    }

    // Case 12: valid files, a log and a cancellation token are accepted.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Valid_files_open_and_the_log_receives_no_line_at_start()
    {
        DataPaths paths = NewPaths();
        File.WriteAllText(FilePath(paths, ClassificationPolicyFile.FileName), "{ \"mode\": \"recordOnly\", \"maxMovesPerPass\": 10 }");
        File.WriteAllText(FilePath(paths, ClassifierSettingsFile.FileName), "{}");
        var log = new RecordingCriticalLog();

        await using MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths, CancellationToken.None, log);

        Assert.NotNull(runtime.ReadModel);
        Assert.Empty(log.Entries);
    }

    // Case 12: invalid files fail the open with InvalidDataException naming the file; the lock is released.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("classification-policy.json", "not json")]
    [InlineData("classification-policy.json", "{ \"mode\": \"zzunknownmodevalue\" }")]
    [InlineData("classification-policy.json", "{ \"maxMovesPerPass\": 0 }")]
    [InlineData("classifier-settings.json", "not json")]
    public async Task Invalid_file_fails_the_open_names_the_file_and_releases_the_lock(string fileName, string content)
    {
        DataPaths paths = NewPaths();
        File.WriteAllText(FilePath(paths, fileName), content);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => MailWardenRuntime.OpenAsync(paths));

        Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(paths.Root, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(FilePath(paths, LockFileName)));
        using DataFolderLock? lockAgain = DataFolderLock.TryAcquire(paths);
        Assert.NotNull(lockAgain);
    }

    // Case 12: after the failure and the repair of the file, a second open works.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("classification-policy.json")]
    [InlineData("classifier-settings.json")]
    public async Task Second_open_works_after_the_invalid_file_was_removed(string fileName)
    {
        DataPaths paths = NewPaths();
        string path = FilePath(paths, fileName);
        File.WriteAllText(path, "not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => MailWardenRuntime.OpenAsync(paths));
        SqliteConnection.ClearAllPools();

        File.Delete(path);
        await using MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths);

        Assert.True(File.Exists(FilePath(paths, LockFileName)));
        Assert.NotNull(runtime.Service);
    }

    // Case 12: the files are read once at start; a later change does not affect the running runtime or its dispose.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task File_that_becomes_invalid_after_start_does_not_disturb_the_running_runtime()
    {
        DataPaths paths = NewPaths();
        MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths);

        File.WriteAllText(FilePath(paths, ClassificationPolicyFile.FileName), "not json");

        Exception? failure = await Record.ExceptionAsync(async () => await runtime.DisposeAsync());
        Assert.Null(failure);
        Assert.False(File.Exists(FilePath(paths, LockFileName)));
    }
}
