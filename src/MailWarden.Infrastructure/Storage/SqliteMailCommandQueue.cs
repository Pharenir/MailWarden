using System.Globalization;
using MailWarden.Core.Commands;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// <see cref="IMailCommandQueue"/> (UI side) and <see cref="IMailCommandStore"/> (executor side) on the
/// <c>mail_commands</c> table (schema v4). Enqueue only validates and stores; it never changes messages or the server.
/// Commands are removed by the foreign-key cascade when their account, source folder or target folder is deleted.
/// Queue order is the insertion order (id ascending).
/// </summary>
/// <param name="database">Initialised database.</param>
/// <param name="timeProvider">Clock for <see cref="MailCommand.CreatedAt"/>.</param>
public sealed class SqliteMailCommandQueue(SqliteDatabase database, TimeProvider timeProvider)
    : IMailCommandQueue, IMailCommandStore
{
    /// <summary>Maximum number of distinct UIDs per command given by the caller (not for EmptyFolder).</summary>
    public const int MaxUidsPerCommand = 1_000;

    private const MessageFlags SettableFlags =
        MessageFlags.Seen | MessageFlags.Flagged | MessageFlags.Answered | MessageFlags.Draft;

    private const string SelectColumns = """
        SELECT id, account_id, kind, folder_id, folder_uid_validity, uids, target_folder_id, flags_add, flags_remove,
               status, attempts, created_at, next_attempt_at, last_error
        FROM mail_commands
        """;

    private readonly SqliteDatabase database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly SqliteFolderStore folderStore = new(database);
    private readonly SqliteMessageStore messageStore = new(database);

    /// <inheritdoc />
    public async Task<MailCommand> EnqueueSetFlagsAsync(
        long folderId,
        IReadOnlyCollection<uint> uids,
        MessageFlags flagsToAdd,
        MessageFlags flagsToRemove,
        CancellationToken ct = default)
    {
        List<uint> normalizedUids = NormalizeUids(uids);
        ValidateFlagChange(flagsToAdd, flagsToRemove);
        MailFolder source = await RequireSyncedFolderAsync(folderId, ct).ConfigureAwait(false);
        return await InsertAsync(source, MailCommandKind.SetFlags, normalizedUids, null, flagsToAdd, flagsToRemove, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MailCommand> EnqueueMoveAsync(
        long folderId,
        IReadOnlyCollection<uint> uids,
        long targetFolderId,
        CancellationToken ct = default)
    {
        List<uint> normalizedUids = NormalizeUids(uids);
        MailFolder source = await RequireSyncedFolderAsync(folderId, ct).ConfigureAwait(false);

        // The target must be another folder of the same account.
        MailFolder target = await folderStore.GetAsync(targetFolderId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"Unknown target folder id {targetFolderId}.", nameof(targetFolderId));
        if (target.AccountId != source.AccountId)
        {
            throw new ArgumentException("The target folder belongs to another account.", nameof(targetFolderId));
        }

        if (target.Id == source.Id)
        {
            throw new ArgumentException("The target folder is the source folder.", nameof(targetFolderId));
        }

        return await InsertAsync(source, MailCommandKind.Move, normalizedUids, target.Id, MessageFlags.None, MessageFlags.None, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MailCommand> EnqueueMoveToTrashAsync(
        long folderId,
        IReadOnlyCollection<uint> uids,
        CancellationToken ct = default)
    {
        List<uint> normalizedUids = NormalizeUids(uids);
        MailFolder source = await RequireSyncedFolderAsync(folderId, ct).ConfigureAwait(false);

        // Trash is resolved now; the command keeps its id even if roles change later.
        MailFolder trash = await FindTrashFolderAsync(source.AccountId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account has no folder with role Trash.");
        if (trash.Id == source.Id)
        {
            throw new ArgumentException(
                "The source folder is the Trash folder; delete the messages permanently instead.",
                nameof(folderId));
        }

        return await InsertAsync(source, MailCommandKind.MoveToTrash, normalizedUids, trash.Id, MessageFlags.None, MessageFlags.None, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MailCommand> EnqueueDeletePermanentlyAsync(
        long folderId,
        IReadOnlyCollection<uint> uids,
        CancellationToken ct = default)
    {
        List<uint> normalizedUids = NormalizeUids(uids);
        MailFolder source = await RequireSyncedFolderAsync(folderId, ct).ConfigureAwait(false);
        return await InsertAsync(source, MailCommandKind.DeletePermanently, normalizedUids, null, MessageFlags.None, MessageFlags.None, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MailCommand?> EnqueueEmptyFolderAsync(long folderId, CancellationToken ct = default)
    {
        MailFolder source = await RequireSyncedFolderAsync(folderId, ct).ConfigureAwait(false);
        if (source.Role is not (FolderRole.Trash or FolderRole.Junk))
        {
            throw new ArgumentException("Only a Trash or Junk folder can be emptied.", nameof(folderId));
        }

        // Snapshot of the local UIDs: messages that arrive later are never part of this command.
        IReadOnlyDictionary<uint, MessageFlags> localMessages = await messageStore
            .GetUidFlagsAsync(source.Id, ct)
            .ConfigureAwait(false);
        if (localMessages.Count == 0)
        {
            return null;
        }

        // Q2 (planner): one command with all local UIDs, without the per-command limit (MailKit sends UID ranges).
        List<uint> normalizedUids = NormalizeUids(localMessages.Keys.ToList(), enforceLimit: false);
        return await InsertAsync(source, MailCommandKind.DeletePermanently, normalizedUids, null, MessageFlags.None, MessageFlags.None, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailCommand>> ListAsync(Guid accountId, CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = SelectColumns + " WHERE account_id = $account_id ORDER BY id;";
            command.Parameters.AddWithValue("$account_id", SqliteValues.FromGuid(accountId));
            return await ReadCommandsAsync(command, ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Listing mail commands", exception);
        }
    }

    /// <inheritdoc />
    public Task DiscardAsync(long commandId, CancellationToken ct = default)
    {
        return DeleteAsync(commandId, "Discarding the mail command", ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailCommand>> ListDueAsync(
        Guid accountId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = SelectColumns + " WHERE account_id = $account_id AND status = $pending ORDER BY id;";
            command.Parameters.AddWithValue("$account_id", SqliteValues.FromGuid(accountId));
            command.Parameters.AddWithValue("$pending", (int)MailCommandStatus.Pending);
            IReadOnlyList<MailCommand> pending = await ReadCommandsAsync(command, ct).ConfigureAwait(false);

            // Due check in .NET: exact instant comparison across offsets (SQLite julianday keeps milliseconds only).
            return pending
                .Where(candidate => candidate.NextAttemptAt is not DateTimeOffset nextAttemptAt || nextAttemptAt <= now)
                .ToList();
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Listing due mail commands", exception);
        }
    }

    /// <inheritdoc />
    public Task CompleteAsync(long commandId, CancellationToken ct = default)
    {
        return DeleteAsync(commandId, "Completing the mail command", ct);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="error"/> is null or empty.</exception>
    public async Task RecordFailureAsync(
        long commandId,
        string error,
        DateTimeOffset? nextAttemptAt,
        bool permanent,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(error);
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                UPDATE mail_commands
                SET attempts = attempts + 1,
                    last_error = $last_error,
                    next_attempt_at = $next_attempt_at,
                    status = CASE WHEN $permanent = 1 THEN $failed ELSE status END
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", commandId);
            command.Parameters.AddWithValue("$last_error", error);
            command.Parameters.AddWithValue(
                "$next_attempt_at",
                nextAttemptAt is DateTimeOffset nextAttempt ? SqliteValues.FromDate(nextAttempt) : (object)DBNull.Value);
            command.Parameters.AddWithValue("$permanent", permanent ? 1 : 0);
            command.Parameters.AddWithValue("$failed", (int)MailCommandStatus.Failed);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Recording the mail command failure", exception);
        }
    }

    // UIDs: non-empty, no 0, duplicates removed, ascending; at most MaxUidsPerCommand distinct values unless the
    // limit is lifted (EmptyFolder only).
    private static List<uint> NormalizeUids(IReadOnlyCollection<uint> uids, bool enforceLimit = true)
    {
        ArgumentNullException.ThrowIfNull(uids);
        if (uids.Count == 0)
        {
            throw new ArgumentException("At least one UID is required.", nameof(uids));
        }

        List<uint> normalizedUids = [.. uids.Distinct().Order()];
        if (normalizedUids[0] == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uids), "UID 0 is not a valid IMAP UID.");
        }

        if (enforceLimit && normalizedUids.Count > MaxUidsPerCommand)
        {
            throw new ArgumentOutOfRangeException(
                nameof(uids),
                normalizedUids.Count,
                $"At most {MaxUidsPerCommand} UIDs per command are allowed.");
        }

        return normalizedUids;
    }

    // SetFlags: only Seen | Flagged | Answered | Draft; at least one change; add and remove disjoint.
    private static void ValidateFlagChange(MessageFlags flagsToAdd, MessageFlags flagsToRemove)
    {
        if ((flagsToAdd & ~SettableFlags) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(flagsToAdd),
                flagsToAdd,
                "Only Seen, Flagged, Answered and Draft can be set; use the delete commands instead of Deleted.");
        }

        if ((flagsToRemove & ~SettableFlags) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(flagsToRemove),
                flagsToRemove,
                "Only Seen, Flagged, Answered and Draft can be removed.");
        }

        if (flagsToAdd == MessageFlags.None && flagsToRemove == MessageFlags.None)
        {
            throw new ArgumentException("At least one flag must be added or removed.", nameof(flagsToAdd));
        }

        if ((flagsToAdd & flagsToRemove) != 0)
        {
            throw new ArgumentException("A flag cannot be added and removed at the same time.", nameof(flagsToRemove));
        }
    }

    // The source must exist and must have been synced (UIDVALIDITY known), so the executor can verify it.
    private async Task<MailFolder> RequireSyncedFolderAsync(long folderId, CancellationToken ct)
    {
        MailFolder folder = await folderStore.GetAsync(folderId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"Unknown folder id {folderId}.", nameof(folderId));
        if (folder.UidValidity == 0)
        {
            throw new ArgumentException(
                "The folder was never synced (UIDVALIDITY unknown); no command can be queued for it.",
                nameof(folderId));
        }

        return folder;
    }

    // Same rule as the executor: the first folder of the account with role Trash (folder list order).
    private async Task<MailFolder?> FindTrashFolderAsync(Guid accountId, CancellationToken ct)
    {
        IReadOnlyList<MailFolder> accountFolders = await folderStore.ListAsync(accountId, ct).ConfigureAwait(false);
        return accountFolders.FirstOrDefault(folder => folder.Role == FolderRole.Trash);
    }

    private async Task<MailCommand> InsertAsync(
        MailFolder source,
        MailCommandKind kind,
        List<uint> uids,
        long? targetFolderId,
        MessageFlags flagsToAdd,
        MessageFlags flagsToRemove,
        CancellationToken ct)
    {
        DateTimeOffset createdAt = timeProvider.GetUtcNow();
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mail_commands (account_id, kind, folder_id, folder_uid_validity, uids, target_folder_id,
                                           flags_add, flags_remove, status, attempts, created_at, next_attempt_at,
                                           last_error)
                VALUES ($account_id, $kind, $folder_id, $folder_uid_validity, $uids, $target_folder_id,
                        $flags_add, $flags_remove, $status, 0, $created_at, NULL, NULL)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$account_id", SqliteValues.FromGuid(source.AccountId));
            command.Parameters.AddWithValue("$kind", (int)kind);
            command.Parameters.AddWithValue("$folder_id", source.Id);
            command.Parameters.AddWithValue("$folder_uid_validity", (long)source.UidValidity);
            command.Parameters.AddWithValue("$uids", FormatUids(uids));
            command.Parameters.AddWithValue(
                "$target_folder_id",
                targetFolderId is long targetId ? targetId : (object)DBNull.Value);
            command.Parameters.AddWithValue("$flags_add", (int)flagsToAdd);
            command.Parameters.AddWithValue("$flags_remove", (int)flagsToRemove);
            command.Parameters.AddWithValue("$status", (int)MailCommandStatus.Pending);
            command.Parameters.AddWithValue("$created_at", SqliteValues.FromDate(createdAt));
            object? storedId = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return new MailCommand(
                Id: (long)storedId!,
                AccountId: source.AccountId,
                Kind: kind,
                FolderId: source.Id,
                FolderUidValidity: source.UidValidity,
                Uids: uids,
                TargetFolderId: targetFolderId,
                FlagsToAdd: flagsToAdd,
                FlagsToRemove: flagsToRemove,
                Status: MailCommandStatus.Pending,
                Attempts: 0,
                CreatedAt: createdAt,
                NextAttemptAt: null,
                LastError: null);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Storing the mail command", exception);
        }
    }

    private async Task DeleteAsync(long commandId, string operation, CancellationToken ct)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = "DELETE FROM mail_commands WHERE id = $id;";
            command.Parameters.AddWithValue("$id", commandId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap(operation, exception);
        }
    }

    private static string FormatUids(IEnumerable<uint> uids) =>
        string.Join(',', uids.Select(uid => uid.ToString(CultureInfo.InvariantCulture)));

    private static async Task<IReadOnlyList<MailCommand>> ReadCommandsAsync(SqliteCommand command, CancellationToken ct)
    {
        var commands = new List<MailCommand>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            commands.Add(new MailCommand(
                Id: reader.GetInt64(0),
                AccountId: SqliteValues.ReadGuid(reader, 1),
                Kind: SqliteValues.ReadEnum<MailCommandKind>(reader, 2),
                FolderId: reader.GetInt64(3),
                FolderUidValidity: SqliteValues.ReadUInt32(reader, 4),
                Uids: ReadUids(reader, 5),
                TargetFolderId: reader.IsDBNull(6) ? null : reader.GetInt64(6),
                FlagsToAdd: ReadFlags(reader, 7),
                FlagsToRemove: ReadFlags(reader, 8),
                Status: SqliteValues.ReadEnum<MailCommandStatus>(reader, 9),
                Attempts: reader.GetInt32(10),
                CreatedAt: SqliteValues.ReadDate(reader, 11),
                NextAttemptAt: reader.IsDBNull(12) ? null : SqliteValues.ReadDate(reader, 12),
                LastError: SqliteValues.ReadNullableString(reader, 13)));
        }

        return commands;
    }

    // Comma-separated, ascending, no 0; anything else cannot have been written by this class.
    private static List<uint> ReadUids(SqliteDataReader reader, int ordinal)
    {
        string text = reader.GetString(ordinal);
        var uids = new List<uint>();
        foreach (string part in text.Split(','))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint uid)
                || uid == 0
                || (uids.Count > 0 && uid <= uids[^1]))
            {
                throw new StorageException($"Column '{reader.GetName(ordinal)}' contains an unexpected value '{text}'.");
            }

            uids.Add(uid);
        }

        return uids;
    }

    // Flags are a bit set; reject bits that MessageFlags does not define.
    private static MessageFlags ReadFlags(SqliteDataReader reader, int ordinal)
    {
        const long knownBits = (long)(MessageFlags.Seen | MessageFlags.Answered | MessageFlags.Flagged
            | MessageFlags.Deleted | MessageFlags.Draft);
        long value = reader.GetInt64(ordinal);
        if ((value & ~knownBits) != 0)
        {
            throw new StorageException(
                $"Column '{reader.GetName(ordinal)}' contains an unexpected value '{value.ToString(CultureInfo.InvariantCulture)}'.");
        }

        return (MessageFlags)value;
    }
}
