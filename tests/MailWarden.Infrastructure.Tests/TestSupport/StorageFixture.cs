using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>
/// Fresh in-memory database with initialised schema and the three real SQLite stores. No mocks.
/// </summary>
internal sealed class StorageFixture : IAsyncDisposable
{
    private StorageFixture(SqliteDatabase database)
    {
        Database = database;
        Accounts = new SqliteAccountStore(database);
        Folders = new SqliteFolderStore(database);
        Messages = new SqliteMessageStore(database);
    }

    public SqliteDatabase Database { get; }

    public SqliteAccountStore Accounts { get; }

    public SqliteFolderStore Folders { get; }

    public SqliteMessageStore Messages { get; }

    /// <summary>Creates the fixture: <c>SqliteDatabase.InMemory()</c> followed by <c>InitializeAsync()</c>.</summary>
    public static async Task<StorageFixture> CreateAsync()
    {
        SqliteDatabase database = SqliteDatabase.InMemory();
        await database.InitializeAsync();
        return new StorageFixture(database);
    }

    public async Task<Account> AddAccountAsync(string displayName = "Account_A", string? emailAddress = null)
    {
        Account account = TestData.NewAccount(displayName, emailAddress);
        await Accounts.UpsertAsync(account);
        return account;
    }

    public Task<MailFolder> AddFolderAsync(Guid accountId, FolderRole role, string? fullName = null) =>
        Folders.UpsertAsync(TestData.NewFolder(accountId, fullName ?? $"folder_{role}", role));

    public Task<MailMessage> AddMessageAsync(
        MailFolder folder,
        uint uid,
        string subject = "sample_subject",
        DateTimeOffset? date = null,
        string? bodyText = null,
        string fromAddress = "sender_a@example.test",
        string fromName = "Sender A") =>
        Messages.UpsertAsync(
            TestData.NewMessage(
                folder.AccountId,
                folder.Id,
                uid,
                subject,
                fromAddress,
                fromName,
                date),
            bodyText);

    /// <summary>Counts rows of a table directly through the connection (table names are test constants).</summary>
    public async Task<long> CountRowsAsync(string tableName)
    {
        await using SqliteCommand command = Database.Connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
