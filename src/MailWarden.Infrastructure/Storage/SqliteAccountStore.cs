using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// <see cref="IAccountStore"/> on the <c>accounts</c> table. Deleting an account cascades to folders and messages
/// (foreign keys) and removes their full-text entries in the same transaction.
/// </summary>
/// <param name="database">Initialised database.</param>
public sealed class SqliteAccountStore(SqliteDatabase database) : IAccountStore
{
    private const string SelectColumns = "SELECT id, display_name, email_address, provider FROM accounts";

    private readonly SqliteDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = SelectColumns + " ORDER BY display_name COLLATE NOCASE, display_name, id;";
            return await ReadAccountsAsync(command, ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Listing accounts", exception);
        }
    }

    /// <inheritdoc />
    public async Task<Account?> GetAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = SelectColumns + " WHERE id = $id;";
            command.Parameters.AddWithValue("$id", SqliteValues.FromGuid(id));
            IReadOnlyList<Account> accounts = await ReadAccountsAsync(command, ct).ConfigureAwait(false);
            return accounts.Count == 0 ? null : accounts[0];
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Loading the account", exception);
        }
    }

    /// <inheritdoc />
    public async Task UpsertAsync(Account account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        try
        {
            // Conflict on id updates; a conflict on email_address (other id) is not handled and surfaces.
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO accounts (id, display_name, email_address, provider)
                VALUES ($id, $display_name, $email_address, $provider)
                ON CONFLICT (id) DO UPDATE SET
                    display_name = excluded.display_name,
                    email_address = excluded.email_address,
                    provider = excluded.provider;
                """;
            command.Parameters.AddWithValue("$id", SqliteValues.FromGuid(account.Id));
            command.Parameters.AddWithValue("$display_name", account.DisplayName);
            command.Parameters.AddWithValue("$email_address", account.EmailAddress);
            command.Parameters.AddWithValue("$provider", (int)account.Provider);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Storing the account", exception);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            SqliteConnection connection = database.Connection;
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(ct)
                .ConfigureAwait(false);

            // Index entries first (they reference message ids that the cascade removes).
            await MessageSearchIndex.DeleteForAccountAsync(connection, transaction, id, ct).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM accounts WHERE id = $id;";
                command.Parameters.AddWithValue("$id", SqliteValues.FromGuid(id));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Deleting the account", exception);
        }
    }

    private static async Task<IReadOnlyList<Account>> ReadAccountsAsync(SqliteCommand command, CancellationToken ct)
    {
        var accounts = new List<Account>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            accounts.Add(new Account(
                SqliteValues.ReadGuid(reader, 0),
                reader.GetString(1),
                reader.GetString(2),
                SqliteValues.ReadEnum<MailProvider>(reader, 3)));
        }

        return accounts;
    }
}
