using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Tests.Storage;

[Trait("Category", "feature:storage")]
public sealed class SqliteDatabaseTests
{
    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Fresh_database_has_current_version_and_all_tables()
    {
        await using SqliteDatabase database = SqliteDatabase.InMemory();
        await database.InitializeAsync();

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, database.SchemaVersion);

        var names = new HashSet<string>();
        await using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        foreach (string expected in new[] { "schema_version", "accounts", "folders", "messages", "messages_fts", "contacts" })
        {
            Assert.Contains(expected, names);
        }
    }

    // Case 1 (supporting): exactly one schema_version row holding the current version
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Fresh_database_has_exactly_one_schema_version_row()
    {
        await using SqliteDatabase database = SqliteDatabase.InMemory();
        await database.InitializeAsync();

        await using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MAX(version) FROM schema_version";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, reader.GetInt32(1));
    }

    // Case 2
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_initialisation_of_existing_file_keeps_version_and_data()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "second_init.db");
        Account account = TestData.NewAccount("Account_A");

        await using (SqliteDatabase first = SqliteDatabase.ForFile(databasePath))
        {
            await first.InitializeAsync();
            await new SqliteAccountStore(first).UpsertAsync(account);
        }

        await using SqliteDatabase second = SqliteDatabase.ForFile(databasePath);
        await second.InitializeAsync();

        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, second.SchemaVersion);
        Account? loaded = await new SqliteAccountStore(second).GetAsync(account.Id);
        Assert.Equal(account, loaded);
    }

    // Case 3
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Newer_schema_version_throws_SchemaVersionException_with_both_numbers()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "newer.db");
        int newerVersion = SqliteDatabase.CurrentSchemaVersion + 1;

        await using (var seed = new SqliteConnection($"Data Source={databasePath}"))
        {
            await seed.OpenAsync();
            await using SqliteCommand command = seed.CreateCommand();
            command.CommandText =
                "CREATE TABLE schema_version(version INTEGER NOT NULL);" +
                $"INSERT INTO schema_version(version) VALUES ({newerVersion});";
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();

        await using SqliteDatabase database = SqliteDatabase.ForFile(databasePath);
        SchemaVersionException exception =
            await Assert.ThrowsAsync<SchemaVersionException>(() => database.InitializeAsync());

        Assert.Equal(newerVersion, exception.FoundVersion);
        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, exception.SupportedVersion);
        Assert.Contains(newerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message);
        Assert.Contains(
            SqliteDatabase.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            exception.Message);
        Assert.IsAssignableFrom<StorageException>(exception);
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Connection_before_initialisation_throws_InvalidOperationException()
    {
        await using SqliteDatabase database = SqliteDatabase.InMemory();

        Assert.Throws<InvalidOperationException>(() => database.Connection);
        await Task.CompletedTask;
    }

    // Case 4a (D8): initialising the same instance twice fails fast
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_initialisation_on_same_instance_throws_InvalidOperationException()
    {
        await using SqliteDatabase database = SqliteDatabase.InMemory();
        await database.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InitializeAsync());
    }

    // Supporting: foreign keys are enforced (basis for cascade cases 22/23)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Foreign_keys_pragma_is_on_after_initialisation()
    {
        await using SqliteDatabase database = SqliteDatabase.InMemory();
        await database.InitializeAsync();

        await using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys";
        object? value = await command.ExecuteScalarAsync();
        Assert.Equal(1L, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
    }
}
