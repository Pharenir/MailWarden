using Microsoft.Data.Sqlite;
using MailWarden.Core.Classification;

namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-07c case 4: schema v5 adds <c>classification_log</c> and <c>classification_marker</c>; older files migrate.</summary>
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:classifier")]
public sealed class SchemaV5Tests
{
    internal static readonly Guid SeedAccountId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // Schema version 4 written out here (v3 plus the command table), so the test does not depend on the production
    // migrations for v1..v4. The command table is only there to prove that its rows survive.
    internal const string SchemaV4Sql = """
        CREATE TABLE schema_version (version INTEGER NOT NULL);
        INSERT INTO schema_version (version) VALUES (4);
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
            skipped_up_to_uid  INTEGER NOT NULL,
            highest_mod_seq    INTEGER NOT NULL,
            last_synced_at     TEXT    NULL);
        CREATE TABLE mail_commands (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id TEXT NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            kind INTEGER NOT NULL,
            folder_id INTEGER NOT NULL REFERENCES folders (id) ON DELETE CASCADE,
            folder_uid_validity INTEGER NOT NULL,
            uids TEXT NOT NULL,
            target_folder_id INTEGER NULL REFERENCES folders (id) ON DELETE CASCADE,
            flags_add INTEGER NOT NULL,
            flags_remove INTEGER NOT NULL,
            status INTEGER NOT NULL,
            attempts INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            next_attempt_at TEXT NULL,
            last_error TEXT NULL);
        """;

    // Schema version 1 as in SchemaV2Tests: the oldest file the migration chain has to handle.
    internal const string SchemaV1Sql = """
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

    internal const string SeedRowsSql = """

        INSERT INTO accounts (id, display_name, email_address, provider)
            VALUES ($accountId, 'Account_A', 'user_a@example.test', 1);
        INSERT INTO folders (id, account_id, full_name, role, uid_validity)
            VALUES (7, $accountId, 'INBOX', 1, 4242);
        INSERT INTO messages (id, account_id, folder_id, uid, message_id, subject, from_address, from_name, date,
                              flags, has_attachments, size, eml_path, text_preview)
            VALUES (3, $accountId, 7, 11, 'msg_old@example.test', 'old_subject', 'sender_a@example.test', 'Sender A',
                    '2026-01-15T12:00:00.0000000+02:00', 1, 0, 2048, 'accounts/x/folders/7/11.eml', 'old preview');
        INSERT INTO contacts (id, email_address, display_name, source)
            VALUES (5, 'contact_a@example.test', 'Contact A', 0);
        """;

    internal const string SeedCommandSql = """

        INSERT INTO folder_sync_state (folder_id, skipped_up_to_uid, highest_mod_seq, last_synced_at)
            VALUES (7, 9, 77, NULL);
        INSERT INTO mail_commands (id, account_id, kind, folder_id, folder_uid_validity, uids, target_folder_id,
                                   flags_add, flags_remove, status, attempts, created_at, next_attempt_at, last_error)
            VALUES (9, $accountId, 4, 7, 4242, '[11]', NULL, 0, 0, 1, 0, '2026-10-30T10:00:00.0000000+00:00', NULL, NULL);
        """;

    internal static async Task CreateFileAsync(string databasePath, string schemaSql, string seedSql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = schemaSql + seedSql;
        command.Parameters.AddWithValue("$accountId", SeedAccountId.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<HashSet<string>> ReadNamesAsync(SqliteDatabase database, string sql)
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

    internal static async Task<long> ScalarAsync(SqliteDatabase database, string sql)
    {
        await using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // Case 4
    [Fact]
    public void Current_schema_version_is_at_least_five()
    {
        Assert.True(SqliteDatabase.CurrentSchemaVersion >= 5);
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Fresh_database_is_version_five_and_has_both_new_tables_with_their_keys()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, storage.Database.SchemaVersion);
        HashSet<string> tables = await ReadNamesAsync(storage.Database, "SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.Contains("classification_log", tables);
        Assert.Contains("classification_marker", tables);
        HashSet<string> logColumns = await ReadNamesAsync(storage.Database, "SELECT name FROM pragma_table_info('classification_log')");
        Assert.Contains("account_id", logColumns);
        Assert.Contains("message_key", logColumns);
        HashSet<string> markerColumns = await ReadNamesAsync(storage.Database, "SELECT name FROM pragma_table_info('classification_marker')");
        Assert.Contains("folder_id", markerColumns);
        Assert.Equal(0, await storage.CountRowsAsync("classification_log"));
        Assert.Equal(0, await storage.CountRowsAsync("classification_marker"));
    }

    // Case 4: primary key (account_id, message_key) and folder_id
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Primary_keys_and_cascades_follow_the_contract()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();

        HashSet<string> logKey = await ReadNamesAsync(storage.Database, "SELECT name FROM pragma_table_info('classification_log') WHERE pk > 0");
        Assert.Equal(new HashSet<string> { "account_id", "message_key" }, logKey);
        HashSet<string> markerKey = await ReadNamesAsync(storage.Database, "SELECT name FROM pragma_table_info('classification_marker') WHERE pk > 0");
        Assert.Equal(new HashSet<string> { "folder_id" }, markerKey);
        Assert.Contains(
            "CASCADE",
            await ReadNamesAsync(storage.Database, "SELECT on_delete FROM pragma_foreign_key_list('classification_log') WHERE \"from\" = 'account_id'"));
        Assert.Contains(
            "CASCADE",
            await ReadNamesAsync(storage.Database, "SELECT on_delete FROM pragma_foreign_key_list('classification_marker') WHERE \"from\" = 'folder_id'"));
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task V4_database_migrates_to_v5_and_keeps_all_old_rows()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v4.db");
        await CreateFileAsync(databasePath, SchemaV4Sql, SeedRowsSql + SeedCommandSql);
        SqliteConnection.ClearAllPools();

        await using SqliteDatabase database = SqliteDatabase.ForFile(databasePath);
        await database.InitializeAsync();

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, database.SchemaVersion);
        HashSet<string> tables = await ReadNamesAsync(database, "SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.Contains("classification_log", tables);
        Assert.Contains("classification_marker", tables);
        Assert.Equal(1, await ScalarAsync(database, "SELECT COUNT(*) FROM schema_version"));
        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, await ScalarAsync(database, "SELECT MAX(version) FROM schema_version"));
        MailMessage message = (await new SqliteMessageStore(database).GetByUidAsync(7, 11))!;
        Assert.Equal("old_subject", message.Subject);
        Assert.Equal(new Account(SeedAccountId, "Account_A", "user_a@example.test", MailProvider.Google), Assert.Single(await new SqliteAccountStore(database).ListAsync()));
        Assert.Equal(7, Assert.Single(await new SqliteFolderStore(database).ListAsync(SeedAccountId)).Id);
        Assert.Equal(1, await ScalarAsync(database, "SELECT COUNT(*) FROM contacts WHERE email_address = 'contact_a@example.test'"));
        Assert.Equal(1, await ScalarAsync(database, "SELECT COUNT(*) FROM mail_commands WHERE id = 9 AND kind = 4 AND uids = '[11]' AND folder_uid_validity = 4242"));
        Assert.Equal(77, await ScalarAsync(database, "SELECT highest_mod_seq FROM folder_sync_state WHERE folder_id = 7"));
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task V1_database_with_data_migrates_all_the_way_to_v5()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v1.db");
        await CreateFileAsync(databasePath, SchemaV1Sql, SeedRowsSql);
        SqliteConnection.ClearAllPools();

        await using SqliteDatabase database = SqliteDatabase.ForFile(databasePath);
        await database.InitializeAsync();

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, database.SchemaVersion);
        HashSet<string> tables = await ReadNamesAsync(database, "SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.Contains("classification_log", tables);
        Assert.Contains("classification_marker", tables);
        Assert.Equal("old_subject", (await new SqliteMessageStore(database).GetByUidAsync(7, 11))!.Subject);
        Assert.Equal(1, await ScalarAsync(database, "SELECT COUNT(*) FROM contacts"));
        Assert.Equal(1, await ScalarAsync(database, "SELECT COUNT(*) FROM schema_version"));
        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, await ScalarAsync(database, "SELECT MAX(version) FROM schema_version"));
    }

    // Case 4: the migrated file is usable with the new stores and cascades work there
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Migrated_v4_file_works_with_the_new_stores_and_cascades_on_account_delete()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v4_use.db");
        await CreateFileAsync(databasePath, SchemaV4Sql, SeedRowsSql);
        SqliteConnection.ClearAllPools();
        await using SqliteDatabase database = SqliteDatabase.ForFile(databasePath);
        await database.InitializeAsync();
        var log = new SqliteClassificationLog(database);
        var entry = new ClassificationLogEntry(
            SeedAccountId, "m:cccc", 7, 11, DateTimeOffset.UnixEpoch.AddDays(20000),
            ClassificationMode.RecordOnly,
            ClassificationOutcome.Recorded,
            ClassificationAction.MoveToScreener,
            SpamTier.LikelyLegit, -20,
            AuthStatus.Authenticated, null, []);

        await log.CommitAsync([entry], new ClassificationMarker(7, 4242, 11));
        Assert.NotNull(await log.GetAsync(SeedAccountId, "m:cccc"));
        await new SqliteAccountStore(database).DeleteAsync(SeedAccountId);

        Assert.Equal(0, await ScalarAsync(database, "SELECT COUNT(*) FROM classification_log"));
        Assert.Equal(0, await ScalarAsync(database, "SELECT COUNT(*) FROM classification_marker"));
    }

    // Case 4: reopening a v5 file keeps its log
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reopening_a_v5_file_keeps_the_log_entries_and_markers()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v5_twice.db");
        await CreateFileAsync(databasePath, SchemaV4Sql, SeedRowsSql);
        SqliteConnection.ClearAllPools();
        await using (SqliteDatabase first = SqliteDatabase.ForFile(databasePath))
        {
            await first.InitializeAsync();
            var log = new SqliteClassificationLog(first);
            await log.CommitAsync(
                [],
                new ClassificationMarker(7, 4242, 11));
        }

        SqliteConnection.ClearAllPools();
        await using SqliteDatabase second = SqliteDatabase.ForFile(databasePath);
        await second.InitializeAsync();

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, second.SchemaVersion);
        var reopened = new SqliteClassificationLog(second);
        Assert.Equal(new ClassificationMarker(7, 4242, 11), await reopened.GetMarkerAsync(7));
    }

    // Case 4: a newer version is rejected as before
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task File_with_a_newer_version_than_the_current_one_is_rejected()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "vnewer.db");
        await CreateFileAsync(databasePath, SchemaV4Sql.Replace("VALUES (4)", $"VALUES ({SqliteDatabase.CurrentSchemaVersion + 1})", StringComparison.Ordinal), string.Empty);
        SqliteConnection.ClearAllPools();

        await using SqliteDatabase database = SqliteDatabase.ForFile(databasePath);
        SchemaVersionException exception = await Assert.ThrowsAsync<SchemaVersionException>(() => database.InitializeAsync());

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, exception.SupportedVersion);
    }
}
