using System.Text.Json;
using MailWarden.Core.Classification;
using MailWarden.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// <see cref="IClassificationLog"/> on the tables <c>classification_log</c> and <c>classification_marker</c> (schema
/// v5). Findings are stored as a JSON array. Reason texts stay in the database: no message of this class contains one.
/// </summary>
/// <param name="database">Initialised database.</param>
public sealed class SqliteClassificationLog(SqliteDatabase database) : IClassificationLog
{
    private static readonly JsonSerializerOptions SignalJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public async Task<ClassificationLogEntry?> GetAsync(Guid accountId, string messageKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageKey);
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                SELECT account_id, message_key, source_folder_id, source_uid, classified_at, effective_mode, outcome,
                       planned_action, tier, score, auth_status, override_reason, signals
                FROM classification_log
                WHERE account_id = $account_id AND message_key = $message_key;
                """;
            command.Parameters.AddWithValue("$account_id", SqliteValues.FromGuid(accountId));
            command.Parameters.AddWithValue("$message_key", messageKey);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new ClassificationLogEntry(
                AccountId: SqliteValues.ReadGuid(reader, 0),
                MessageKey: reader.GetString(1),
                SourceFolderId: reader.GetInt64(2),
                SourceUid: SqliteValues.ReadUInt32(reader, 3),
                ClassifiedAt: SqliteValues.ReadDate(reader, 4),
                EffectiveMode: SqliteValues.ReadEnum<ClassificationMode>(reader, 5),
                Outcome: SqliteValues.ReadEnum<ClassificationOutcome>(reader, 6),
                PlannedAction: SqliteValues.ReadEnum<ClassificationAction>(reader, 7),
                Tier: reader.IsDBNull(8) ? null : SqliteValues.ReadEnum<SpamTier>(reader, 8),
                Score: reader.GetInt32(9),
                AuthStatus: reader.IsDBNull(10) ? null : SqliteValues.ReadEnum<AuthStatus>(reader, 10),
                Override: SqliteValues.ReadNullableString(reader, 11),
                Signals: ReadSignals(reader, 12));
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Loading the classification entry", exception);
        }
    }

    /// <inheritdoc />
    public async Task<ClassificationMarker?> GetMarkerAsync(long folderId, CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                SELECT folder_id, uid_validity, classified_up_to_uid
                FROM classification_marker
                WHERE folder_id = $folder_id;
                """;
            command.Parameters.AddWithValue("$folder_id", folderId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new ClassificationMarker(
                reader.GetInt64(0),
                SqliteValues.ReadUInt32(reader, 1),
                SqliteValues.ReadUInt32(reader, 2));
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Loading the classification marker", exception);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or <paramref name="marker"/> is null.</exception>
    /// <exception cref="ArgumentException">An entry is null or has no key or no signal list.</exception>
    /// <exception cref="StorageException">The account or the folder does not exist; nothing is stored then.</exception>
    public async Task CommitAsync(
        IReadOnlyCollection<ClassificationLogEntry> entries,
        ClassificationMarker marker,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(marker);
        if (entries.Any(entry => entry is null || string.IsNullOrEmpty(entry.MessageKey) || entry.Signals is null))
        {
            throw new ArgumentException("Every entry needs a message key and a signal list.", nameof(entries));
        }

        try
        {
            SqliteConnection connection = database.Connection;
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(ct)
                .ConfigureAwait(false);

            foreach (ClassificationLogEntry entry in entries)
            {
                await InsertEntryAsync(connection, transaction, entry, ct).ConfigureAwait(false);
            }

            await using (var markerCommand = connection.CreateCommand())
            {
                markerCommand.Transaction = transaction;
                markerCommand.CommandText = """
                    INSERT INTO classification_marker (folder_id, uid_validity, classified_up_to_uid)
                    VALUES ($folder_id, $uid_validity, $classified_up_to_uid)
                    ON CONFLICT (folder_id) DO UPDATE SET
                        uid_validity = excluded.uid_validity,
                        classified_up_to_uid = excluded.classified_up_to_uid;
                    """;
                markerCommand.Parameters.AddWithValue("$folder_id", marker.FolderId);
                markerCommand.Parameters.AddWithValue("$uid_validity", (long)marker.UidValidity);
                markerCommand.Parameters.AddWithValue("$classified_up_to_uid", (long)marker.ClassifiedUpToUid);
                await markerCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Storing the classification entries", exception);
        }
    }

    // The first decision for a key wins; a later entry for the same key is dropped without touching the stored one.
    private static async Task InsertEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClassificationLogEntry entry,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO classification_log (account_id, message_key, source_folder_id, source_uid, classified_at,
                                            effective_mode, outcome, planned_action, tier, score, auth_status,
                                            override_reason, signals)
            VALUES ($account_id, $message_key, $source_folder_id, $source_uid, $classified_at,
                    $effective_mode, $outcome, $planned_action, $tier, $score, $auth_status,
                    $override_reason, $signals)
            ON CONFLICT (account_id, message_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$account_id", SqliteValues.FromGuid(entry.AccountId));
        command.Parameters.AddWithValue("$message_key", entry.MessageKey);
        command.Parameters.AddWithValue("$source_folder_id", entry.SourceFolderId);
        command.Parameters.AddWithValue("$source_uid", (long)entry.SourceUid);
        command.Parameters.AddWithValue("$classified_at", SqliteValues.FromDate(entry.ClassifiedAt));
        command.Parameters.AddWithValue("$effective_mode", (int)entry.EffectiveMode);
        command.Parameters.AddWithValue("$outcome", (int)entry.Outcome);
        command.Parameters.AddWithValue("$planned_action", (int)entry.PlannedAction);
        command.Parameters.AddWithValue("$tier", entry.Tier is SpamTier tier ? (int)tier : DBNull.Value);
        command.Parameters.AddWithValue("$score", entry.Score);
        command.Parameters.AddWithValue(
            "$auth_status",
            entry.AuthStatus is AuthStatus authStatus ? (int)authStatus : DBNull.Value);
        command.Parameters.AddWithValue("$override_reason", (object?)entry.Override ?? DBNull.Value);
        command.Parameters.AddWithValue("$signals", JsonSerializer.Serialize(entry.Signals, SignalJsonOptions));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // Deliberately without the parser message and the column value: the JSON holds the classifier's reason texts.
    private static IReadOnlyList<SignalResult> ReadSignals(SqliteDataReader reader, int ordinal)
    {
        List<SignalResult>? signals;
        try
        {
            signals = JsonSerializer.Deserialize<List<SignalResult>>(reader.GetString(ordinal), SignalJsonOptions);
        }
        catch (JsonException)
        {
            signals = null;
        }

        if (signals is null || signals.Any(signal => signal is null || signal.SignalId is null || signal.Reason is null))
        {
            throw new StorageException($"Column '{reader.GetName(ordinal)}' contains an unexpected value.");
        }

        return signals.AsReadOnly();
    }
}
