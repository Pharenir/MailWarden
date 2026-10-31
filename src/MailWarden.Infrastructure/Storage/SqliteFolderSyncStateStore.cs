using MailWarden.Core.Storage;
using MailWarden.Core.Sync;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// <see cref="IFolderSyncStateStore"/> on the <c>folder_sync_state</c> table (schema v2). The row is removed by the
/// foreign-key cascade when its folder is deleted. <see cref="FolderSyncState.HighestModSeq"/> is stored bit-exact in
/// the signed 64-bit column (CONDSTORE values are 63-bit, so stored values are never negative in practice).
/// </summary>
/// <param name="database">Initialised database.</param>
public sealed class SqliteFolderSyncStateStore(SqliteDatabase database) : IFolderSyncStateStore
{
    private readonly SqliteDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public async Task<FolderSyncState?> GetAsync(long folderId, CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                SELECT folder_id, highest_synced_uid, highest_mod_seq, last_synced_at
                FROM folder_sync_state
                WHERE folder_id = $folder_id;
                """;
            command.Parameters.AddWithValue("$folder_id", folderId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new FolderSyncState(
                FolderId: reader.GetInt64(0),
                HighestSyncedUid: SqliteValues.ReadUInt32(reader, 1),
                HighestModSeq: unchecked((ulong)reader.GetInt64(2)),
                LastSyncedAt: reader.IsDBNull(3) ? null : SqliteValues.ReadDate(reader, 3));
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Loading the folder sync state", exception);
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(FolderSyncState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            // Upsert by folder; an unknown folder violates the foreign key and surfaces as StorageException.
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO folder_sync_state (folder_id, highest_synced_uid, highest_mod_seq, last_synced_at)
                VALUES ($folder_id, $highest_synced_uid, $highest_mod_seq, $last_synced_at)
                ON CONFLICT (folder_id) DO UPDATE SET
                    highest_synced_uid = excluded.highest_synced_uid,
                    highest_mod_seq = excluded.highest_mod_seq,
                    last_synced_at = excluded.last_synced_at;
                """;
            command.Parameters.AddWithValue("$folder_id", state.FolderId);
            command.Parameters.AddWithValue("$highest_synced_uid", (long)state.HighestSyncedUid);
            command.Parameters.AddWithValue("$highest_mod_seq", unchecked((long)state.HighestModSeq));
            command.Parameters.AddWithValue(
                "$last_synced_at",
                state.LastSyncedAt is DateTimeOffset lastSyncedAt ? SqliteValues.FromDate(lastSyncedAt) : (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Storing the folder sync state", exception);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long folderId, CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = "DELETE FROM folder_sync_state WHERE folder_id = $folder_id;";
            command.Parameters.AddWithValue("$folder_id", folderId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Deleting the folder sync state", exception);
        }
    }
}
