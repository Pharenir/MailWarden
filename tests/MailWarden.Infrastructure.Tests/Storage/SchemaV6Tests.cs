using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-06 case 1 / D1: schema v6 adds <c>contact_harvest_marker</c> and <c>sender_list</c>; older files migrate.</summary>
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:contacts")]
public sealed class SchemaV6Tests
{
    // Schema version 5 written out here: v4 plus the two classification tables (columns reduced to the keys), so the
    // test does not depend on the production migrations for v1..v5.
    private static readonly string SchemaV5Sql =
        SchemaV5Tests.SchemaV4Sql.Replace("VALUES (4)", "VALUES (5)", StringComparison.Ordinal) + """

        CREATE TABLE classification_log (
            account_id TEXT NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            message_key TEXT NOT NULL,
            PRIMARY KEY (account_id, message_key));
        CREATE TABLE classification_marker (
            folder_id INTEGER NOT NULL PRIMARY KEY REFERENCES folders (id) ON DELETE CASCADE,
            uid_validity INTEGER NOT NULL,
            classified_up_to_uid INTEGER NOT NULL);
        """;

    private static async Task<SqliteDatabase> OpenFileAsync(string databasePath, string schemaSql, string seedSql)
    {
        await SchemaV5Tests.CreateFileAsync(databasePath, schemaSql, seedSql);
        SqliteConnection.ClearAllPools();
        SqliteDatabase database = SqliteDatabase.ForFile(databasePath);
        await database.InitializeAsync();
        return database;
    }

    private static async Task ExecuteAsync(SqliteDatabase database, string sql)
    {
        await using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // Case 1
    [Fact]
    public void Current_schema_version_is_at_least_six()
    {
        Assert.True(SqliteDatabase.CurrentSchemaVersion >= 6);
    }

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Fresh_database_has_both_new_tables_with_their_columns_and_keys()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, storage.Database.SchemaVersion);
        HashSet<string> tables = await SchemaV5Tests.ReadNamesAsync(storage.Database, "SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.Contains("contact_harvest_marker", tables);
        Assert.Contains("sender_list", tables);
        Assert.Equal(
            new HashSet<string> { "folder_id", "uid_validity", "harvested_up_to_uid" },
            await SchemaV5Tests.ReadNamesAsync(storage.Database, "SELECT name FROM pragma_table_info('contact_harvest_marker')"));
        Assert.Equal(
            new HashSet<string> { "entry", "is_domain", "kind" },
            await SchemaV5Tests.ReadNamesAsync(storage.Database, "SELECT name FROM pragma_table_info('sender_list')"));
        Assert.Equal(
            new HashSet<string> { "folder_id" },
            await SchemaV5Tests.ReadNamesAsync(storage.Database, "SELECT name FROM pragma_table_info('contact_harvest_marker') WHERE pk > 0"));
        Assert.Equal(
            new HashSet<string> { "entry", "is_domain" },
            await SchemaV5Tests.ReadNamesAsync(storage.Database, "SELECT name FROM pragma_table_info('sender_list') WHERE pk > 0"));
        Assert.Equal(0, await storage.CountRowsAsync("sender_list"));
        Assert.Equal(0, await storage.CountRowsAsync("contact_harvest_marker"));
    }

    // Case 1: marker rows follow their folder
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Harvest_marker_is_removed_with_its_folder()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder folder = await storage.AddFolderAsync(account.Id, FolderRole.Sent);
        await ExecuteAsync(
            storage.Database,
            $"INSERT INTO contact_harvest_marker (folder_id, uid_validity, harvested_up_to_uid) VALUES ({folder.Id}, 1, 5)");
        Assert.Equal(1, await storage.CountRowsAsync("contact_harvest_marker"));

        await storage.Folders.DeleteAsync(folder.Id);

        Assert.Equal(0, await storage.CountRowsAsync("contact_harvest_marker"));
    }

    // Case 1: key (entry, is_domain), entry case-insensitive
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Sender_list_key_is_entry_and_flag_and_the_entry_ignores_case()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        await ExecuteAsync(storage.Database, "INSERT INTO sender_list (entry, is_domain, kind) VALUES ('Sender_A@Example.Test', 0, 1)");

        // Same text with the other flag is another entry.
        await ExecuteAsync(storage.Database, "INSERT INTO sender_list (entry, is_domain, kind) VALUES ('Sender_A@Example.Test', 1, 2)");
        SqliteException duplicate = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(storage.Database, "INSERT INTO sender_list (entry, is_domain, kind) VALUES ('sender_a@example.test', 0, 2)"));

        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await storage.CountRowsAsync("sender_list"));
    }

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task V5_database_migrates_to_current_and_keeps_all_old_rows()
    {
        using var temp = new TempDirectory();
        await using SqliteDatabase database = await OpenFileAsync(
            Path.Combine(temp.RootPath, "v5.db"), SchemaV5Sql, SchemaV5Tests.SeedRowsSql + SchemaV5Tests.SeedCommandSql);

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, database.SchemaVersion);
        HashSet<string> tables = await SchemaV5Tests.ReadNamesAsync(database, "SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.Contains("contact_harvest_marker", tables);
        Assert.Contains("sender_list", tables);
        Assert.Contains("classification_log", tables);
        Assert.Equal(1, await SchemaV5Tests.ScalarAsync(database, "SELECT COUNT(*) FROM schema_version"));
        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, await SchemaV5Tests.ScalarAsync(database, "SELECT MAX(version) FROM schema_version"));
        Assert.Equal("old_subject", (await new SqliteMessageStore(database).GetByUidAsync(7, 11))!.Subject);
        Assert.Equal(1, await SchemaV5Tests.ScalarAsync(database, "SELECT COUNT(*) FROM contacts WHERE email_address = 'contact_a@example.test'"));
        Assert.Equal(1, await SchemaV5Tests.ScalarAsync(database, "SELECT COUNT(*) FROM mail_commands WHERE id = 9 AND kind = 4"));
        Assert.Equal(7, Assert.Single(await new SqliteFolderStore(database).ListAsync(SchemaV5Tests.SeedAccountId)).Id);
    }

    // Case 1: "from v1 with data"
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task V1_database_with_data_migrates_all_the_way_and_the_new_tables_work()
    {
        using var temp = new TempDirectory();
        await using SqliteDatabase database = await OpenFileAsync(
            Path.Combine(temp.RootPath, "v1.db"), SchemaV5Tests.SchemaV1Sql, SchemaV5Tests.SeedRowsSql);

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, database.SchemaVersion);
        Assert.Equal("old_subject", (await new SqliteMessageStore(database).GetByUidAsync(7, 11))!.Subject);
        Assert.Equal(1, await SchemaV5Tests.ScalarAsync(database, "SELECT COUNT(*) FROM contacts"));
        Assert.Equal(1, await SchemaV5Tests.ScalarAsync(database, "SELECT COUNT(*) FROM schema_version"));
        var senderLists = new SqliteSenderListStore(database);
        await senderLists.SetAsync(new MailWarden.Core.Classification.SenderListEntry(
            "sender_a@example.test", false, MailWarden.Core.Classification.ListMembership.Blocked));
        Assert.Single(await senderLists.ListAsync());
    }

    // Case 1: contacts of an old file stay usable with the new store member
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Migrated_contact_is_found_by_AddIfMissing_and_stays_unchanged()
    {
        using var temp = new TempDirectory();
        await using SqliteDatabase database = await OpenFileAsync(
            Path.Combine(temp.RootPath, "v4.db"), SchemaV5Tests.SchemaV4Sql, SchemaV5Tests.SeedRowsSql);
        var contacts = new SqliteContactStore(database);

        Contact result = await contacts.AddIfMissingAsync(new Contact(0, "CONTACT_A@example.test", "Other Name", ContactSource.AutoCollected));

        Assert.Equal("Contact A", result.DisplayName);
        Assert.Equal(ContactSource.Manual, result.Source);
        Assert.Single(await contacts.ListAsync());
    }
}
