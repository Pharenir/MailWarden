using System.Globalization;
using Microsoft.Data.Sqlite;
using MailWarden.Core.Commands;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-03c case 4: schema v4 adds the <c>mail_commands</c> table; the migration keeps old rows; deletes cascade.</summary>
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:sync")]
public sealed class SchemaV4Tests
{
    private static readonly Guid SeedAccountId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly DateTimeOffset SeedSyncedAt = new(2026, 10, 1, 8, 30, 0, TimeSpan.Zero);

    // Schema version 3 as the contract of AP-03a fix.3 describes it. Written out here, so the test does not depend on
    // the production migrations for v1..v3.
    private const string SchemaV3Sql = """
        CREATE TABLE schema_version (version INTEGER NOT NULL);
        INSERT INTO schema_version (version) VALUES (3);
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
        """;

    /// <summary>Creates a v3 file: one account, INBOX (id 7, validity 4242), message UID 11, sync state with floor 9.</summary>
    private static async Task CreateV3DatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SchemaV3Sql + """

            INSERT INTO accounts (id, display_name, email_address, provider)
                VALUES ($accountId, 'Account_A', 'user_a@example.test', 1);
            INSERT INTO folders (id, account_id, full_name, role, uid_validity)
                VALUES (7, $accountId, 'INBOX', 1, 4242);
            INSERT INTO messages (id, account_id, folder_id, uid, message_id, subject, from_address, from_name, date,
                                  flags, has_attachments, size, eml_path, text_preview)
                VALUES (3, $accountId, 7, 11, 'msg_v3@example.test', 'v3_subject', 'sender_a@example.test', 'Sender A',
                        '2026-01-15T12:00:00.0000000+02:00', 1, 0, 2048, 'accounts/x/folders/7/11.eml', 'v3 preview');
            INSERT INTO folder_sync_state (folder_id, skipped_up_to_uid, highest_mod_seq, last_synced_at)
                VALUES (7, 9, 77, $syncedAt);
            """;
        command.Parameters.AddWithValue("$accountId", SeedAccountId.ToString("D"));
        command.Parameters.AddWithValue("$syncedAt", SeedSyncedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadColumnsAsync(SqliteDatabase database, string table)
    {
        var names = new List<string>();
        await using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static readonly string[] RequiredColumns =
    [
        "id", "account_id", "kind", "folder_id", "folder_uid_validity", "uids", "target_folder_id",
        "flags_add", "flags_remove", "status", "attempts", "created_at", "next_attempt_at", "last_error",
    ];

    [Fact]
    public void Current_schema_version_is_at_least_four()
    {
        Assert.True(SqliteDatabase.CurrentSchemaVersion >= 4);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Fresh_database_is_current_version_and_has_the_command_table()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, storage.Database.SchemaVersion);
        List<string> columns = await ReadColumnsAsync(storage.Database, "mail_commands");
        foreach (string required in RequiredColumns)
        {
            Assert.Contains(required, columns);
        }

        Assert.Equal(0, await storage.CountRowsAsync("mail_commands"));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task V3_database_migrates_to_v4_keeps_old_rows_and_gets_the_command_table()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v3.db");
        await CreateV3DatabaseAsync(databasePath);
        SqliteConnection.ClearAllPools();

        await using SqliteDatabase database = SqliteDatabase.ForFile(databasePath);
        await database.InitializeAsync();

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, database.SchemaVersion);
        List<string> columns = await ReadColumnsAsync(database, "mail_commands");
        foreach (string required in RequiredColumns)
        {
            Assert.Contains(required, columns);
        }

        MailMessage message = (await new SqliteMessageStore(database).GetByUidAsync(7, 11))!;
        Assert.Equal("v3_subject", message.Subject);
        Assert.Equal(7, Assert.Single(await new SqliteFolderStore(database).ListAsync(SeedAccountId)).Id);
        FolderSyncState state = (await new SqliteFolderSyncStateStore(database).GetAsync(7))!;
        Assert.Equal(9u, state.SkippedUpToUid);
        Assert.Equal(77UL, state.HighestModSeq);
        Assert.Equal(SeedSyncedAt, state.LastSyncedAt);
        var queue = new SqliteMailCommandQueue(database, new FixedTimeProvider(SyncHarness.FixedNow));
        Assert.Empty(await queue.ListAsync(SeedAccountId));
        MailCommand command = await queue.EnqueueSetFlagsAsync(7, 4242, [11], MessageFlags.Seen, MessageFlags.None);
        Assert.Equal(4242u, command.FolderUidValidity);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reopening_a_v4_file_keeps_the_queued_commands()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "v3_twice.db");
        await CreateV3DatabaseAsync(databasePath);
        SqliteConnection.ClearAllPools();
        var clock = new FixedTimeProvider(SyncHarness.FixedNow);
        await using (SqliteDatabase first = SqliteDatabase.ForFile(databasePath))
        {
            await first.InitializeAsync();
            await new SqliteMailCommandQueue(first, clock).EnqueueDeletePermanentlyAsync(7, 4242, [11]);
        }

        SqliteConnection.ClearAllPools();
        await using SqliteDatabase second = SqliteDatabase.ForFile(databasePath);
        await second.InitializeAsync();

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, second.SchemaVersion);
        MailCommand kept = Assert.Single(await new SqliteMailCommandQueue(second, clock).ListAsync(SeedAccountId));
        Assert.Equal(new uint[] { 11 }, kept.Uids);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Deleting_a_folder_removes_its_commands_as_source_and_as_target()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        await fixture.SeedAsync(fixture.Junk, 1);
        MailCommand sourceOnly = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Junk.Id, fixture.Junk.UidValidity, [1], MessageFlags.Seen, MessageFlags.None);
        MailCommand movedIn = await fixture.Queue.EnqueueMoveAsync(fixture.Inbox.Id, fixture.Inbox.UidValidity, [2], fixture.Archive.Id);
        MailCommand unrelated = await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, fixture.Inbox.UidValidity, [3], MessageFlags.Seen, MessageFlags.None);

        await fixture.Storage.Folders.DeleteAsync(fixture.Junk.Id);
        Assert.DoesNotContain(sourceOnly.Id, (await fixture.Queue.ListAsync(fixture.Account.Id)).Select(command => command.Id));

        await fixture.Storage.Folders.DeleteAsync(fixture.Archive.Id);

        MailCommand remaining = Assert.Single(await fixture.Queue.ListAsync(fixture.Account.Id));
        Assert.Equal(unrelated.Id, remaining.Id);
        Assert.NotEqual(movedIn.Id, remaining.Id);
        Assert.Equal(1, await fixture.CommandRowCountAsync());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Deleting_the_account_removes_all_its_commands()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        await fixture.SeedAsync(fixture.Junk, 2);
        await fixture.Queue.EnqueueSetFlagsAsync(fixture.Inbox.Id, fixture.Inbox.UidValidity, [1], MessageFlags.Seen, MessageFlags.None);
        await fixture.Queue.EnqueueDeletePermanentlyAsync(fixture.Junk.Id, fixture.Junk.UidValidity, [2]);

        await fixture.Storage.Accounts.DeleteAsync(fixture.Account.Id);

        Assert.Equal(0, await fixture.CommandRowCountAsync());
    }
}
