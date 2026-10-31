using System.Globalization;
using Microsoft.Data.Sqlite;
using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-03a fix.3 schema v3: column rename <c>highest_synced_uid</c> to <c>skipped_up_to_uid</c> with reset (case 39).</summary>
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:sync")]
public sealed class SchemaV3Tests
{
    private static readonly Guid SeedAccountId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly DateTimeOffset SeedSyncedAt = new(2026, 10, 1, 8, 30, 0, TimeSpan.Zero);

    // Schema version 2 as the contract of AP-03a describes it (v1 tables plus folder_sync_state). Written out here, so
    // the test does not depend on the production migrations for v1/v2.
    private const string SchemaV2Sql = """
        CREATE TABLE schema_version (version INTEGER NOT NULL);
        INSERT INTO schema_version (version) VALUES (2);
        CREATE TABLE accounts (
            id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            email_address TEXT NOT NULL UNIQUE COLLATE NOCASE,
            provider INTEGER NOT NULL);
        CREATE TABLE folders (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id TEXT NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            full_name TEXT NOT NULL,
            role INTEGER NOT NULL,
            uid_validity INTEGER NOT NULL,
            UNIQUE (account_id, full_name),
            UNIQUE (id, account_id));
        CREATE TABLE messages (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id TEXT NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            folder_id INTEGER NOT NULL,
            uid INTEGER NOT NULL,
            message_id TEXT NULL,
            subject TEXT NOT NULL,
            from_address TEXT NOT NULL,
            from_name TEXT NOT NULL,
            date TEXT NOT NULL,
            flags INTEGER NOT NULL,
            has_attachments INTEGER NOT NULL,
            size INTEGER NOT NULL,
            eml_path TEXT NULL,
            text_preview TEXT NOT NULL,
            FOREIGN KEY (folder_id, account_id) REFERENCES folders (id, account_id) ON DELETE CASCADE,
            UNIQUE (folder_id, uid));
        CREATE INDEX ix_messages_folder_date ON messages (folder_id, julianday(date) DESC, id DESC);
        CREATE INDEX ix_messages_date ON messages (julianday(date) DESC, id DESC);
        CREATE VIRTUAL TABLE messages_fts USING fts5(subject, from_address, from_name, body_text);
        CREATE TABLE contacts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            email_address TEXT NOT NULL UNIQUE COLLATE NOCASE,
            display_name TEXT NOT NULL,
            source INTEGER NOT NULL);
        CREATE TABLE folder_sync_state (
            folder_id          INTEGER NOT NULL PRIMARY KEY REFERENCES folders (id) ON DELETE CASCADE,
            highest_synced_uid INTEGER NOT NULL,
            highest_mod_seq    INTEGER NOT NULL,
            last_synced_at     TEXT    NULL);
        """;

    /// <summary>Creates a v2 file: one account, INBOX (id 7, validity 4242), message UID 11, sync state with high-water mark 50.</summary>
    private static async Task CreateV2DatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SchemaV2Sql + """

            INSERT INTO accounts (id, display_name, email_address, provider)
                VALUES ($accountId, 'Account_A', 'user_a@example.test', 1);
            INSERT INTO folders (id, account_id, full_name, role, uid_validity)
                VALUES (7, $accountId, 'INBOX', 1, 4242);
            INSERT INTO messages (id, account_id, folder_id, uid, message_id, subject, from_address, from_name, date,
                                  flags, has_attachments, size, eml_path, text_preview)
                VALUES (3, $accountId, 7, 11, 'msg_v2@example.test', 'v2_subject', 'sender_a@example.test', 'Sender A',
                        '2026-01-15T12:00:00.0000000+02:00', 1, 0, 2048, 'accounts/x/folders/7/11.eml', 'v2 preview');
            INSERT INTO folder_sync_state (folder_id, highest_synced_uid, highest_mod_seq, last_synced_at)
                VALUES (7, 50, 77, $syncedAt);
            """;
        command.Parameters.AddWithValue("$accountId", SeedAccountId.ToString("D"));
        command.Parameters.AddWithValue("$syncedAt", SeedSyncedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadColumnsAsync(SqliteDatabase database)
    {
        var names = new List<string>();
        await using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('folder_sync_state')";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    // Case 39 (storage part)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task V2_database_migrates_to_v3_renames_the_column_and_resets_the_value()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v2.db");
        await CreateV2DatabaseAsync(databasePath);
        SqliteConnection.ClearAllPools();

        await using SqliteDatabase database = SqliteDatabase.ForFile(databasePath);
        await database.InitializeAsync();

        Assert.Equal(3, database.SchemaVersion);
        List<string> columns = await ReadColumnsAsync(database);
        Assert.Contains("skipped_up_to_uid", columns);
        Assert.DoesNotContain("highest_synced_uid", columns);
        FolderSyncState state = (await new SqliteFolderSyncStateStore(database).GetAsync(7))!;
        Assert.Equal(0u, state.SkippedUpToUid);
        Assert.Equal(77UL, state.HighestModSeq);
        Assert.Equal(SeedSyncedAt, state.LastSyncedAt);
        MailMessage message = (await new SqliteMessageStore(database).GetByUidAsync(7, 11))!;
        Assert.Equal("v2_subject", message.Subject);
        Assert.Equal(7, Assert.Single(await new SqliteFolderStore(database).ListAsync(SeedAccountId)).Id);
    }

    // Case 39 (supporting): reopening the migrated file does not reset a floor that was saved afterwards
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reopening_a_v3_file_keeps_the_saved_floor()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v2_twice.db");
        await CreateV2DatabaseAsync(databasePath);
        SqliteConnection.ClearAllPools();
        await using (SqliteDatabase first = SqliteDatabase.ForFile(databasePath))
        {
            await first.InitializeAsync();
            await new SqliteFolderSyncStateStore(first).SaveAsync(new FolderSyncState(7, 9, 77, SeedSyncedAt));
        }

        SqliteConnection.ClearAllPools();
        await using SqliteDatabase second = SqliteDatabase.ForFile(databasePath);
        await second.InitializeAsync();

        Assert.Equal(3, second.SchemaVersion);
        Assert.Equal(9u, (await new SqliteFolderSyncStateStore(second).GetAsync(7))!.SkippedUpToUid);
    }

    // Case 39 (sync part): after the migration the next sync fetches every missing UID
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Next_sync_after_the_migration_fetches_every_missing_uid()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v2_sync.db");
        await CreateV2DatabaseAsync(databasePath);
        SqliteConnection.ClearAllPools();
        await using SqliteDatabase database = SqliteDatabase.ForFile(databasePath);
        await database.InitializeAsync();
        var accounts = new SqliteAccountStore(database);
        var folders = new SqliteFolderStore(database);
        var messages = new SqliteMessageStore(database);
        var states = new SqliteFolderSyncStateStore(database);
        var eml = new FileEmlStore(new DataPaths(Path.Combine(temp.RootPath, "data")));
        var server = new FakeMailServer();
        FakeMailFolder inbox = server.AddFolder("INBOX", ServerFolderFlags.Inbox, uidValidity: 4242);
        for (int index = 1; index <= 12; index++)
        {
            inbox.AddMessage(SyncTestEml.Plain($"m{index}", "body"));
        }

        var engine = new SyncEngine(
            new FakeMailTransportFactory(server),
            accounts,
            folders,
            messages,
            eml,
            states,
            new SyncOptions(0),
            new FixedTimeProvider(SyncHarness.FixedNow));

        SyncReport report = await engine.SyncAccountAsync(SeedAccountId);

        Assert.Null(Assert.Single(report.Folders).Error);
        Assert.Equal(
            Enumerable.Range(1, 12).Select(uid => ("INBOX", (uint)uid)).Where(fetch => fetch.Item2 != 11).ToArray(),
            server.Fetches.OrderBy(fetch => fetch.Uid).ToArray());
        IReadOnlyList<MailMessage> stored = await messages.ListAsync(new MessageQuery(FolderId: 7, Limit: 100));
        Assert.Equal(Enumerable.Range(1, 12).Select(uid => (uint)uid), stored.Select(row => row.Uid).OrderBy(uid => uid));
        Assert.Equal(0u, (await states.GetAsync(7))!.SkippedUpToUid);
    }
}
