using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Mime;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace MailWarden.Infrastructure.Tests.Composition;

/// <summary>AP-04a cases 24, 25 and 27 (D6): <see cref="MailWardenRuntime"/> lock, lifecycle and connections.</summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:sync")]
public sealed class MailWardenRuntimeTests : IDisposable
{
    private const string LockFileName = "mailwarden.lock";

    private readonly string _baseDirectory = Path.Combine(AppContext.BaseDirectory, "ap04a-runtime", Guid.NewGuid().ToString("N"));

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

    private DataPaths NewPaths(string name = "data")
    {
        string root = Path.Combine(_baseDirectory, name);
        Directory.CreateDirectory(root);
        return new DataPaths(root);
    }

    // Case 25
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Open_exposes_the_parts_and_dispose_removes_the_lock_file()
    {
        DataPaths paths = NewPaths();
        string lockFile = Path.Combine(paths.Root, LockFileName);

        MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths);
        Assert.Equal(paths.Root, runtime.Paths.Root);
        Assert.NotNull(runtime.Service);
        Assert.NotNull(runtime.ReadModel);
        Assert.True(File.Exists(lockFile));
        Assert.True(File.Exists(paths.DatabasePath));

        await runtime.DisposeAsync();

        Assert.False(File.Exists(lockFile));
    }

    // Case 25
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Open_dispose_open_again_works_and_dispose_twice_is_fine()
    {
        DataPaths paths = NewPaths();

        MailWardenRuntime first = await MailWardenRuntime.OpenAsync(paths);
        await first.DisposeAsync();
        await first.DisposeAsync();
        MailWardenRuntime second = await MailWardenRuntime.OpenAsync(paths);

        Assert.True(File.Exists(Path.Combine(paths.Root, LockFileName)));
        await second.DisposeAsync();
        await second.DisposeAsync();
        Assert.False(File.Exists(Path.Combine(paths.Root, LockFileName)));
    }

    // Case 25: the data of a closed runtime is complete on disk and every handle is released.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Dispose_releases_all_handles_so_the_data_folder_can_be_deleted()
    {
        DataPaths paths = NewPaths("deletable");
        MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths);
        Account account = TestData.NewAccount("Account_A");
        SqliteDatabase owned = await runtime.OpenDatabaseAsync();
        await new SqliteAccountStore(owned).UpsertAsync(account);

        await runtime.DisposeAsync();

        Assert.Equal(account, Assert.Single(await ReadAccountsAfterDisposeAsync(paths)));
        SqliteConnection.ClearAllPools();
        Directory.Delete(paths.Root, recursive: true);
        Assert.False(Directory.Exists(paths.Root));
    }

    private static async Task<IReadOnlyList<Account>> ReadAccountsAfterDisposeAsync(DataPaths paths)
    {
        await using SqliteDatabase database = SqliteDatabase.ForFile(paths.DatabasePath);
        await database.InitializeAsync();
        return await new SqliteAccountStore(database).ListAsync();
    }

    // Case 24
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_open_on_the_same_root_throws_and_leaves_the_database_file_untouched()
    {
        DataPaths paths = NewPaths();
        await using MailWardenRuntime first = await MailWardenRuntime.OpenAsync(paths);
        FileInfo before = new(paths.DatabasePath);
        long sizeBefore = before.Length;
        DateTime writeTimeBefore = before.LastWriteTimeUtc;

        await Assert.ThrowsAsync<DataFolderInUseException>(() => MailWardenRuntime.OpenAsync(paths));

        FileInfo after = new(paths.DatabasePath);
        Assert.Equal(sizeBefore, after.Length);
        Assert.Equal(writeTimeBefore, after.LastWriteTimeUtc);
        Assert.True(File.Exists(Path.Combine(paths.Root, LockFileName)));
    }

    // Case 24: the lock is taken BEFORE the database is opened or migrated (review O2 of AP-03d).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Open_with_a_foreign_lock_does_not_create_the_database()
    {
        DataPaths paths = NewPaths();
        using DataFolderLock? foreign = DataFolderLock.TryAcquire(paths);
        Assert.NotNull(foreign);

        await Assert.ThrowsAsync<DataFolderInUseException>(() => MailWardenRuntime.OpenAsync(paths));

        Assert.False(File.Exists(paths.DatabasePath));
        Assert.True(File.Exists(Path.Combine(paths.Root, LockFileName)));
    }

    // Case 24: a failed second open must not release the lock of the first one.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failed_second_open_keeps_the_first_lock_and_a_third_open_works_after_the_first_is_disposed()
    {
        DataPaths paths = NewPaths();
        MailWardenRuntime first = await MailWardenRuntime.OpenAsync(paths);
        await Assert.ThrowsAsync<DataFolderInUseException>(() => MailWardenRuntime.OpenAsync(paths));
        await Assert.ThrowsAsync<DataFolderInUseException>(() => MailWardenRuntime.OpenAsync(paths));

        await first.DisposeAsync();

        await using MailWardenRuntime third = await MailWardenRuntime.OpenAsync(paths);
        Assert.NotNull(third.ReadModel);
    }

    // Case 25 (D6): "Failures after the lock release it again" - here a database schema newer than this build.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Newer_schema_throws_SchemaVersionException_and_releases_the_lock()
    {
        DataPaths paths = NewPaths();
        int newerVersion = SqliteDatabase.CurrentSchemaVersion + 1;
        await using (var seed = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await seed.OpenAsync();
            await using SqliteCommand command = seed.CreateCommand();
            command.CommandText =
                "CREATE TABLE schema_version(version INTEGER NOT NULL);" +
                $"INSERT INTO schema_version(version) VALUES ({newerVersion});";
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();

        await Assert.ThrowsAsync<SchemaVersionException>(() => MailWardenRuntime.OpenAsync(paths));

        Assert.False(File.Exists(Path.Combine(paths.Root, LockFileName)));
        using DataFolderLock? lockAgain = DataFolderLock.TryAcquire(paths);
        Assert.NotNull(lockAgain);
    }

    // Case 27 (review O1): every flow has its own connection, so parallel writers do not corrupt each other.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Two_runtime_connections_writing_in_parallel_lose_no_rows()
    {
        DataPaths paths = NewPaths();
        await using MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths);
        const int perConnection = 200;
        SqliteDatabase databaseA = await runtime.OpenDatabaseAsync();
        SqliteDatabase databaseB = await runtime.OpenDatabaseAsync();
        Assert.NotSame(databaseA, databaseB);
        Account account = TestData.NewAccount("Account_A");
        await new SqliteAccountStore(databaseA).UpsertAsync(account);
        MailFolder folderA = await new SqliteFolderStore(databaseA).UpsertAsync(TestData.NewFolder(account.Id, "INBOX", FolderRole.Inbox));
        MailFolder folderB = await new SqliteFolderStore(databaseB).UpsertAsync(TestData.NewFolder(account.Id, "Archive", FolderRole.Archive));

        async Task WriteAsync(SqliteDatabase database, MailFolder folder)
        {
            var messages = new SqliteMessageStore(database);
            for (uint uid = 1; uid <= perConnection; uid++)
            {
                await messages.UpsertAsync(TestData.NewMessage(account.Id, folder.Id, uid), "body");
            }
        }

        await Task.WhenAll(Task.Run(() => WriteAsync(databaseA, folderA)), Task.Run(() => WriteAsync(databaseB, folderB)));

        Assert.Equal(perConnection, await runtime.ReadModel.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox)));
        Assert.Equal(perConnection, await runtime.ReadModel.CountUnreadAsync(new MessageQuery(Role: FolderRole.Archive)));
        Assert.Equal(2 * perConnection, (await runtime.ReadModel.ListMessagesAsync(new MessageQuery(Limit: 1000))).Count);
    }

    // D6: the read model reflects what the other connections stored.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Read_model_lists_accounts_folders_and_messages_and_counts_unread()
    {
        DataPaths paths = NewPaths();
        await using MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths);
        SqliteDatabase database = await runtime.OpenDatabaseAsync();
        Account account = TestData.NewAccount("Account_A");
        await new SqliteAccountStore(database).UpsertAsync(account);
        MailFolder inbox = await new SqliteFolderStore(database).UpsertAsync(TestData.NewFolder(account.Id, "INBOX", FolderRole.Inbox));
        var messages = new SqliteMessageStore(database);
        await messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 1, "unread_subject"), null);
        await messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 2, "seen_subject", flags: MessageFlags.Seen), null);

        IReadOnlyList<Account> accounts = await runtime.ReadModel.ListAccountsAsync();
        IReadOnlyList<MailFolder> folders = await runtime.ReadModel.ListFoldersAsync(account.Id);
        IReadOnlyList<MailMessage> listed = await runtime.ReadModel.ListMessagesAsync(new MessageQuery(Role: FolderRole.Inbox));
        int unread = await runtime.ReadModel.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox));

        Assert.Equal(account, Assert.Single(accounts));
        Assert.Equal("INBOX", Assert.Single(folders).FullName);
        Assert.Equal(2, listed.Count);
        Assert.Equal(1, unread);
        Assert.Empty(await runtime.ReadModel.ListFoldersAsync(Guid.NewGuid()));
    }

    // D6: ReadContentAsync
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Read_content_returns_the_parsed_mail_and_holds_no_reader_open()
    {
        DataPaths paths = NewPaths();
        await using MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths);
        SqliteDatabase database = await runtime.OpenDatabaseAsync();
        Account account = TestData.NewAccount("Account_A");
        await new SqliteAccountStore(database).UpsertAsync(account);
        MailFolder inbox = await new SqliteFolderStore(database).UpsertAsync(TestData.NewFolder(account.Id, "INBOX", FolderRole.Inbox));
        var emlStore = new FileEmlStore(paths);
        string emlPath = emlStore.RelativePathFor(account.Id, inbox.Id, 1);
        MimeMessage mime = ReadingEml.NewMessage("subject_from_eml");
        mime.Body = new TextPart("plain") { Text = "body_from_eml" };
        await emlStore.WriteAsync(emlPath, ReadingEml.ToStream(mime));
        MailMessage stored = await new SqliteMessageStore(database).UpsertAsync(
            TestData.NewMessage(account.Id, inbox.Id, 1, emlPath: emlPath), null);

        MailContent? content = await runtime.ReadModel.ReadContentAsync(stored);

        Assert.NotNull(content);
        Assert.Equal("subject_from_eml", content.Subject);
        Assert.Contains("body_from_eml", content.PlainText, StringComparison.Ordinal);

        // The sync may overwrite or delete the file at any time: no handle may stay open after the read.
        string absolute = paths.ResolveMailPath(emlPath);
        await File.WriteAllBytesAsync(absolute, ReadingEml.ToBytes(mime));
        File.Delete(absolute);
        Assert.False(File.Exists(absolute));
    }

    // D6: null when there is nothing to read
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Read_content_is_null_without_eml_path_or_with_a_missing_file()
    {
        DataPaths paths = NewPaths();
        await using MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths);
        SqliteDatabase database = await runtime.OpenDatabaseAsync();
        Account account = TestData.NewAccount("Account_A");
        await new SqliteAccountStore(database).UpsertAsync(account);
        MailFolder inbox = await new SqliteFolderStore(database).UpsertAsync(TestData.NewFolder(account.Id, "INBOX", FolderRole.Inbox));
        var messages = new SqliteMessageStore(database);
        MailMessage withoutPath = await messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 1), null);
        var emlStore = new FileEmlStore(paths);
        MailMessage missingFile = await messages.UpsertAsync(
            TestData.NewMessage(account.Id, inbox.Id, 2, emlPath: emlStore.RelativePathFor(account.Id, inbox.Id, 2)), null);

        Assert.Null(await runtime.ReadModel.ReadContentAsync(withoutPath));
        Assert.Null(await runtime.ReadModel.ReadContentAsync(missingFile));
    }

    // D6 / non-goal: reading a message never marks it as seen.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Read_content_does_not_change_the_stored_flags()
    {
        DataPaths paths = NewPaths();
        await using MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths);
        SqliteDatabase database = await runtime.OpenDatabaseAsync();
        Account account = TestData.NewAccount("Account_A");
        await new SqliteAccountStore(database).UpsertAsync(account);
        MailFolder inbox = await new SqliteFolderStore(database).UpsertAsync(TestData.NewFolder(account.Id, "INBOX", FolderRole.Inbox));
        var emlStore = new FileEmlStore(paths);
        string emlPath = emlStore.RelativePathFor(account.Id, inbox.Id, 1);
        MimeMessage mime = ReadingEml.NewMessage();
        mime.Body = new TextPart("plain") { Text = "body" };
        await emlStore.WriteAsync(emlPath, ReadingEml.ToStream(mime));
        var messages = new SqliteMessageStore(database);
        MailMessage stored = await messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 1, emlPath: emlPath), null);

        await runtime.ReadModel.ReadContentAsync(stored);

        MailMessage? reloaded = await messages.GetAsync(stored.Id);
        Assert.Equal(MessageFlags.None, reloaded?.Flags);
        Assert.Equal(1, await runtime.ReadModel.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox)));
    }
}
