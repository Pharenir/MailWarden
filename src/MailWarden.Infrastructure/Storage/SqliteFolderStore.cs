using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// <see cref="IFolderStore"/> on the <c>folders</c> table. The folder role is stored as given; it is never derived
/// from the folder name here. Deleting a folder cascades to its messages and removes their full-text entries.
/// </summary>
/// <param name="database">Initialised database.</param>
public sealed class SqliteFolderStore(SqliteDatabase database) : IFolderStore
{
    private const string SelectColumns = "SELECT id, account_id, full_name, role, uid_validity FROM folders";

    private readonly SqliteDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailFolder>> ListAsync(Guid accountId, CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText =
                SelectColumns + " WHERE account_id = $account_id ORDER BY full_name COLLATE NOCASE, full_name, id;";
            command.Parameters.AddWithValue("$account_id", SqliteValues.FromGuid(accountId));
            return await ReadFoldersAsync(command, ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Listing folders", exception);
        }
    }

    /// <inheritdoc />
    public async Task<MailFolder?> GetAsync(long id, CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = SelectColumns + " WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            IReadOnlyList<MailFolder> folders = await ReadFoldersAsync(command, ct).ConfigureAwait(false);
            return folders.Count == 0 ? null : folders[0];
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Loading the folder", exception);
        }
    }

    /// <inheritdoc />
    public async Task<MailFolder> UpsertAsync(MailFolder folder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            // Key (account_id, full_name); RETURNING yields the id for both insert and update.
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO folders (account_id, full_name, role, uid_validity)
                VALUES ($account_id, $full_name, $role, $uid_validity)
                ON CONFLICT (account_id, full_name) DO UPDATE SET
                    role = excluded.role,
                    uid_validity = excluded.uid_validity
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$account_id", SqliteValues.FromGuid(folder.AccountId));
            command.Parameters.AddWithValue("$full_name", folder.FullName);
            command.Parameters.AddWithValue("$role", (int)folder.Role);
            command.Parameters.AddWithValue("$uid_validity", (long)folder.UidValidity);
            object? storedId = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return folder with { Id = (long)storedId! };
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Storing the folder", exception);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        try
        {
            SqliteConnection connection = database.Connection;
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(ct)
                .ConfigureAwait(false);

            // Index entries first (they reference message ids that the cascade removes).
            await MessageSearchIndex.DeleteForFolderAsync(connection, transaction, id, ct).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM folders WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Deleting the folder", exception);
        }
    }

    private static async Task<IReadOnlyList<MailFolder>> ReadFoldersAsync(SqliteCommand command, CancellationToken ct)
    {
        var folders = new List<MailFolder>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            folders.Add(new MailFolder(
                reader.GetInt64(0),
                SqliteValues.ReadGuid(reader, 1),
                reader.GetString(2),
                SqliteValues.ReadEnum<FolderRole>(reader, 3),
                SqliteValues.ReadUInt32(reader, 4)));
        }

        return folders;
    }
}
