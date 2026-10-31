using System.Globalization;
using Microsoft.Data.Sqlite;
using MailWarden.Core.Sync;

namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-03a schema v2: <c>folder_sync_state</c> table and the in-place migration from v1 (case 22).</summary>
[Trait("Category", "feature:storage")]
public sealed class SchemaV2Tests
{
    // Schema version 1, re-derived from work/AP-01/requirement.md (incl. planner decisions D3 and D4).
    // The test does not call production migration code for v1, so a later edit of version 1 cannot hide here.
    private const string SchemaV1Sql = """
        CREATE TABLE schema_version (version INTEGER NOT NULL);
        INSERT INTO schema_version (version) VALUES (1);
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
        """;

    private static readonly Guid SeedAccountId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static async Task CreateV1DatabaseWithDataAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SchemaV1Sql + """

            INSERT INTO accounts (id, display_name, email_address, provider)
                VALUES ($accountId, 'Account_A', 'user_a@example.test', 1);
            INSERT INTO folders (id, account_id, full_name, role, uid_validity)
                VALUES (7, $accountId, 'INBOX', 1, 4242);
            INSERT INTO messages (id, account_id, folder_id, uid, message_id, subject, from_address, from_name, date,
                                  flags, has_attachments, size, eml_path, text_preview)
                VALUES (3, $accountId, 7, 11, 'msg_v1@example.test', 'v1_subject', 'sender_a@example.test', 'Sender A',
                        '2026-01-15T12:00:00.0000000+02:00', 1, 0, 2048, 'accounts/x/folders/7/11.eml', 'v1 preview');
            INSERT INTO messages_fts (rowid, subject, from_address, from_name, body_text)
                VALUES (3, 'v1_subject', 'sender_a@example.test', 'Sender A', 'legacybodyword');
            """;
        command.Parameters.AddWithValue("$accountId", SeedAccountId.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<HashSet<string>> ReadNamesAsync(SqliteDatabase database, string sql)
    {
        var names = new HashSet<string>();
        await using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    [Fact]
    public void Current_schema_version_is_two()
    {
        Assert.Equal(2, SqliteDatabase.CurrentSchemaVersion);
    }

    // Smoke: a fresh database has the v2 table
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Fresh_database_has_version_two_and_the_sync_state_table()
    {
        await using SqliteDatabase database = SqliteDatabase.InMemory();
        await database.InitializeAsync();

        Assert.Equal(2, database.SchemaVersion);
        HashSet<string> tables = await ReadNamesAsync(database, "SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.Contains("folder_sync_state", tables);
        await using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MAX(version) FROM schema_version";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(2, reader.GetInt32(1));
    }

    // Schema v2: column names of the contract
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Sync_state_table_has_the_contract_columns()
    {
        await using SqliteDatabase database = SqliteDatabase.InMemory();
        await database.InitializeAsync();

        HashSet<string> columns = await ReadNamesAsync(database, "SELECT name FROM pragma_table_info('folder_sync_state')");

        Assert.Equal(
            new HashSet<string> { "folder_id", "highest_synced_uid", "highest_mod_seq", "last_synced_at" },
            columns);
    }

    // Case 22
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task V1_database_migrates_in_place_and_keeps_its_rows()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v1.db");
        await CreateV1DatabaseWithDataAsync(databasePath);
        SqliteConnection.ClearAllPools();

        await using SqliteDatabase database = SqliteDatabase.ForFile(databasePath);
        await database.InitializeAsync();

        Assert.Equal(2, database.SchemaVersion);
        Assert.Contains("folder_sync_state", await ReadNamesAsync(database, "SELECT name FROM sqlite_master WHERE type = 'table'"));
        await using (SqliteCommand versionCommand = database.Connection.CreateCommand())
        {
            versionCommand.CommandText = "SELECT COUNT(*), MAX(version) FROM schema_version";
            await using SqliteDataReader reader = await versionCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(2, reader.GetInt32(1));
        }

        var accounts = new SqliteAccountStore(database);
        var folders = new SqliteFolderStore(database);
        var messages = new SqliteMessageStore(database);
        Account account = Assert.Single(await accounts.ListAsync());
        Assert.Equal(new Account(SeedAccountId, "Account_A", "user_a@example.test", MailProvider.Google), account);
        MailFolder folder = Assert.Single(await folders.ListAsync(SeedAccountId));
        Assert.Equal(new MailFolder(7, SeedAccountId, "INBOX", FolderRole.Inbox, 4242), folder);
        MailMessage message = (await messages.GetByUidAsync(7, 11))!;
        Assert.Equal(3, message.Id);
        Assert.Equal("v1_subject", message.Subject);
        Assert.Equal("msg_v1@example.test", message.MessageId);
        Assert.Equal(MessageFlags.Seen, message.Flags);
        Assert.Equal(2048L, message.Size);
        Assert.Equal("accounts/x/folders/7/11.eml", message.EmlPath);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(2)), message.Date);
        Assert.Equal(message.Id, Assert.Single(await messages.SearchAsync("legacybodyword")).Id);
    }

    // Case 22 (supporting): the migrated database accepts sync state for an old folder, and cascades on delete
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Migrated_database_stores_sync_state_for_existing_folders_and_cascades()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v1_state.db");
        await CreateV1DatabaseWithDataAsync(databasePath);
        SqliteConnection.ClearAllPools();
        await using SqliteDatabase database = SqliteDatabase.ForFile(databasePath);
        await database.InitializeAsync();
        var states = new SqliteFolderSyncStateStore(database);

        await states.SaveAsync(new FolderSyncState(7, 11, 0, null));
        Assert.Equal(11u, (await states.GetAsync(7))!.HighestSyncedUid);
        await new SqliteFolderStore(database).DeleteAsync(7);

        Assert.Null(await states.GetAsync(7));
    }

    // Case 22 (supporting): a second start on the migrated file does not migrate again or lose data
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reopening_a_migrated_file_keeps_version_two_and_the_data()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v1_twice.db");
        await CreateV1DatabaseWithDataAsync(databasePath);
        SqliteConnection.ClearAllPools();
        await using (SqliteDatabase first = SqliteDatabase.ForFile(databasePath))
        {
            await first.InitializeAsync();
            await new SqliteFolderSyncStateStore(first).SaveAsync(new FolderSyncState(7, 3, 4, null));
        }

        SqliteConnection.ClearAllPools();
        await using SqliteDatabase second = SqliteDatabase.ForFile(databasePath);
        await second.InitializeAsync();

        Assert.Equal(2, second.SchemaVersion);
        Assert.Single(await new SqliteAccountStore(second).ListAsync());
        Assert.Equal(3u, (await new SqliteFolderSyncStateStore(second).GetAsync(7))!.HighestSyncedUid);
    }

    // Case 22 (D3 sanity of the seed): the seeded v1 file is really version 1 for a v1 reader
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Seeded_file_is_version_one_before_the_migration()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v1_check.db");
        await CreateV1DatabaseWithDataAsync(databasePath);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version";
        object? version = await command.ExecuteScalarAsync();

        Assert.Equal(1L, Convert.ToInt64(version, CultureInfo.InvariantCulture));
    }
}
