using MailWarden.Core.Classification;
using MailWarden.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// <see cref="IContactHarvestMarkerStore"/> on the <c>contact_harvest_marker</c> table (schema v6). The row is removed by the
/// foreign-key cascade when its folder is deleted.
/// </summary>
/// <param name="database">Initialised database.</param>
public sealed class SqliteContactHarvestMarkerStore(SqliteDatabase database) : IContactHarvestMarkerStore
{
    private readonly SqliteDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public async Task<ContactHarvestMarker?> GetAsync(long folderId, CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                SELECT folder_id, uid_validity, harvested_up_to_uid
                FROM contact_harvest_marker
                WHERE folder_id = $folder_id;
                """;
            command.Parameters.AddWithValue("$folder_id", folderId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new ContactHarvestMarker(
                reader.GetInt64(0),
                SqliteValues.ReadUInt32(reader, 1),
                SqliteValues.ReadUInt32(reader, 2));
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Loading the contact harvest marker", exception);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="marker"/> is null.</exception>
    public async Task SaveAsync(ContactHarvestMarker marker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        try
        {
            // Upsert by folder; an unknown folder violates the foreign key and surfaces as StorageException.
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO contact_harvest_marker (folder_id, uid_validity, harvested_up_to_uid)
                VALUES ($folder_id, $uid_validity, $harvested_up_to_uid)
                ON CONFLICT (folder_id) DO UPDATE SET
                    uid_validity = excluded.uid_validity,
                    harvested_up_to_uid = excluded.harvested_up_to_uid;
                """;
            command.Parameters.AddWithValue("$folder_id", marker.FolderId);
            command.Parameters.AddWithValue("$uid_validity", (long)marker.UidValidity);
            command.Parameters.AddWithValue("$harvested_up_to_uid", (long)marker.HarvestedUpToUid);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Storing the contact harvest marker", exception);
        }
    }
}
