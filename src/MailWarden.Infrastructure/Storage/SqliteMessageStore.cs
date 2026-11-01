using System.Globalization;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// <see cref="IMessageStore"/> on the <c>messages</c> table plus the FTS5 index <c>messages_fts</c>; row and index
/// entry are always changed in one transaction.
/// </summary>
/// <param name="database">Initialised database.</param>
public sealed class SqliteMessageStore(SqliteDatabase database) : IMessageStore
{
    private const string SelectColumns = """
        SELECT m.id, m.account_id, m.folder_id, m.uid, m.message_id, m.subject, m.from_address, m.from_name, m.date,
               m.flags, m.has_attachments, m.size, m.eml_path, m.text_preview
        FROM messages AS m
        """;

    // Dates are stored with their own offset, so text order is not time order; julianday() converts to UTC and is
    // served by the expression indexes ix_messages_folder_date / ix_messages_date (the expression must stay identical).
    // julianday() resolves milliseconds only: dates less than 1 ms apart tie and fall back to id order (irrelevant
    // for IMAP Date headers, which have second precision).
    private const string NewestFirst = " ORDER BY julianday(m.date) DESC, m.id DESC";

    private readonly SqliteDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public async Task<MailMessage> UpsertAsync(MailMessage message, string? bodyText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Uid == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(message), message.Uid, "MailMessage.Uid must be at least 1.");
        }

        try
        {
            SqliteConnection connection = database.Connection;
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(ct)
                .ConfigureAwait(false);

            // Row: key (folder_id, uid); RETURNING yields the id for both insert and update. account_id is part of
            // the update so that the composite folder FK also rejects a wrong account on an existing row.
            long storedId;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO messages (account_id, folder_id, uid, message_id, subject, from_address, from_name, date,
                                          flags, has_attachments, size, eml_path, text_preview)
                    VALUES ($account_id, $folder_id, $uid, $message_id, $subject, $from_address, $from_name, $date,
                            $flags, $has_attachments, $size, $eml_path, $text_preview)
                    ON CONFLICT (folder_id, uid) DO UPDATE SET
                        account_id = excluded.account_id,
                        message_id = excluded.message_id,
                        subject = excluded.subject,
                        from_address = excluded.from_address,
                        from_name = excluded.from_name,
                        date = excluded.date,
                        flags = excluded.flags,
                        has_attachments = excluded.has_attachments,
                        size = excluded.size,
                        eml_path = excluded.eml_path,
                        text_preview = excluded.text_preview
                    RETURNING id;
                    """;
                command.Parameters.AddWithValue("$account_id", SqliteValues.FromGuid(message.AccountId));
                command.Parameters.AddWithValue("$folder_id", message.FolderId);
                command.Parameters.AddWithValue("$uid", (long)message.Uid);
                command.Parameters.AddWithValue("$message_id", (object?)message.MessageId ?? DBNull.Value);
                command.Parameters.AddWithValue("$subject", message.Subject);
                command.Parameters.AddWithValue("$from_address", message.FromAddress);
                command.Parameters.AddWithValue("$from_name", message.FromName);
                command.Parameters.AddWithValue("$date", SqliteValues.FromDate(message.Date));
                command.Parameters.AddWithValue("$flags", (int)message.Flags);
                command.Parameters.AddWithValue("$has_attachments", message.HasAttachments ? 1 : 0);
                command.Parameters.AddWithValue("$size", message.Size);
                command.Parameters.AddWithValue("$eml_path", (object?)message.EmlPath ?? DBNull.Value);
                command.Parameters.AddWithValue("$text_preview", message.TextPreview);
                storedId = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            }

            // Index entry in the same transaction.
            await MessageSearchIndex.WriteAsync(connection, transaction, storedId, message, bodyText, ct)
                .ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return message with { Id = storedId };
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Storing the message", exception);
        }
    }

    /// <inheritdoc />
    public async Task<MailMessage?> GetAsync(long id, CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = SelectColumns + " WHERE m.id = $id;";
            command.Parameters.AddWithValue("$id", id);
            IReadOnlyList<MailMessage> messages = await ReadMessagesAsync(command, ct).ConfigureAwait(false);
            return messages.Count == 0 ? null : messages[0];
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Loading the message", exception);
        }
    }

    /// <inheritdoc />
    public async Task<MailMessage?> GetByUidAsync(long folderId, uint uid, CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = SelectColumns + " WHERE m.folder_id = $folder_id AND m.uid = $uid;";
            command.Parameters.AddWithValue("$folder_id", folderId);
            command.Parameters.AddWithValue("$uid", (long)uid);
            IReadOnlyList<MailMessage> messages = await ReadMessagesAsync(command, ct).ConfigureAwait(false);
            return messages.Count == 0 ? null : messages[0];
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Loading the message by UID", exception);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailMessage>> ListAsync(MessageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "MessageQuery.Limit must be greater than 0.");
        }

        if (query.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Offset, "MessageQuery.Offset must not be negative.");
        }

        try
        {
            await using var command = database.Connection.CreateCommand();
            string filterClause = ApplyViewFilter(command, query);
            command.CommandText = SelectColumns + filterClause + NewestFirst + " LIMIT $limit OFFSET $offset;";
            command.Parameters.AddWithValue("$limit", query.Limit);
            command.Parameters.AddWithValue("$offset", query.Offset);
            return await ReadMessagesAsync(command, ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Listing messages", exception);
        }
    }

    /// <inheritdoc />
    public async Task<int> CountUnreadAsync(MessageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        try
        {
            await using var command = database.Connection.CreateCommand();
            string filterClause = ApplyViewFilter(command, query);
            command.CommandText = "SELECT count(*) FROM messages AS m" + filterClause + " AND (m.flags & $seen) = 0;";
            command.Parameters.AddWithValue("$seen", (int)MessageFlags.Seen);
            object? count = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt32(count, CultureInfo.InvariantCulture);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Counting unread messages", exception);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailMessage>> SearchAsync(
        string text,
        int limit = 100,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!MessageSearchIndex.ContainsToken(text))
        {
            throw new ArgumentException(
                "Search text must contain a character other than whitespace or control characters.",
                nameof(text));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Input consisting only of punctuation cannot match any indexed term.
        string? matchExpression = MessageSearchIndex.BuildMatchExpression(text);
        if (matchExpression is null)
        {
            return [];
        }

        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = SelectColumns
                + " JOIN messages_fts ON messages_fts.rowid = m.id"
                + " WHERE messages_fts MATCH $match"
                + NewestFirst
                + " LIMIT $limit;";
            command.Parameters.AddWithValue("$match", matchExpression);
            command.Parameters.AddWithValue("$limit", limit);
            return await ReadMessagesAsync(command, ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Searching messages", exception);
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

            await MessageSearchIndex.DeleteForMessageAsync(connection, transaction, id, ct).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM messages WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Deleting the message", exception);
        }
    }

    /// <inheritdoc />
    public async Task DeleteByUidsAsync(
        long folderId,
        IReadOnlyCollection<uint> uids,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uids);
        if (uids.Count == 0)
        {
            return;
        }

        try
        {
            SqliteConnection connection = database.Connection;
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(ct)
                .ConfigureAwait(false);

            // Index entries first (they are found through the rows), then the rows; one prepared command each.
            await MessageSearchIndex.DeleteForUidsAsync(connection, transaction, folderId, uids, ct)
                .ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM messages WHERE folder_id = $folder_id AND uid = $uid;";
                command.Parameters.AddWithValue("$folder_id", folderId);
                SqliteParameter uidParameter = command.Parameters.Add("$uid", SqliteType.Integer);
                foreach (uint uid in uids)
                {
                    uidParameter.Value = (long)uid;
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Deleting messages by UID", exception);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<uint, MessageFlags>> GetUidFlagsAsync(
        long folderId,
        CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT uid, flags FROM messages WHERE folder_id = $folder_id;";
            command.Parameters.AddWithValue("$folder_id", folderId);

            var flagsByUid = new Dictionary<uint, MessageFlags>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                flagsByUid.Add(SqliteValues.ReadUInt32(reader, 0), ReadFlags(reader, 1));
            }

            return flagsByUid;
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Loading message flags", exception);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<uint, string?>> GetUidEmlPathsAsync(
        long folderId,
        CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT uid, eml_path FROM messages WHERE folder_id = $folder_id;";
            command.Parameters.AddWithValue("$folder_id", folderId);

            var pathsByUid = new Dictionary<uint, string?>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                pathsByUid.Add(SqliteValues.ReadUInt32(reader, 0), SqliteValues.ReadNullableString(reader, 1));
            }

            return pathsByUid;
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Loading message EML paths", exception);
        }
    }

    /// <summary>Loads UID and database id of all messages of one folder.</summary>
    /// <param name="folderId">Folder id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UID → <see cref="MailMessage.Id"/>; empty for an unknown folder.</returns>
    public async Task<IReadOnlyDictionary<uint, long>> GetUidIdsAsync(long folderId, CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT uid, id FROM messages WHERE folder_id = $folder_id;";
            command.Parameters.AddWithValue("$folder_id", folderId);

            var idsByUid = new Dictionary<uint, long>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                idsByUid.Add(SqliteValues.ReadUInt32(reader, 0), reader.GetInt64(1));
            }

            return idsByUid;
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Loading message ids", exception);
        }
    }

    /// <inheritdoc />
    public async Task UpdateFlagsAsync(
        long folderId,
        IReadOnlyDictionary<uint, MessageFlags> flags,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flags);
        if (flags.Count == 0)
        {
            return;
        }

        try
        {
            SqliteConnection connection = database.Connection;
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(ct)
                .ConfigureAwait(false);

            // One prepared command, executed once per UID; unknown UIDs update no row. The FTS index holds no flags.
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE messages SET flags = $flags WHERE folder_id = $folder_id AND uid = $uid;";
                command.Parameters.AddWithValue("$folder_id", folderId);
                SqliteParameter uidParameter = command.Parameters.Add("$uid", SqliteType.Integer);
                SqliteParameter flagsParameter = command.Parameters.Add("$flags", SqliteType.Integer);
                foreach ((uint uid, MessageFlags messageFlags) in flags)
                {
                    uidParameter.Value = (long)uid;
                    flagsParameter.Value = (int)messageFlags;
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Updating message flags", exception);
        }
    }

    // Filter of a view, shared by ListAsync and CountUnreadAsync: FolderId wins over Role; AccountId narrows either.
    // Returns the JOIN and WHERE text for messages AS m; only fixed SQL fragments are concatenated.
    private static string ApplyViewFilter(SqliteCommand command, MessageQuery query)
    {
        string joinClause = string.Empty;
        string whereClause = " WHERE 1 = 1";
        string accountColumn = "m.account_id";
        if (query.FolderId is long folderId)
        {
            whereClause += " AND m.folder_id = $folder_id";
            command.Parameters.AddWithValue("$folder_id", folderId);
        }
        else if (query.Role is FolderRole role)
        {
            joinClause = " JOIN folders AS f ON f.id = m.folder_id";
            whereClause += " AND f.role = $role";
            command.Parameters.AddWithValue("$role", (int)role);

            // The same rows, by the composite foreign key of messages. Through the folder the planner starts at the few folders of the
            // account; through the message it scans the whole table, even for an empty view. A very big folder pays for this: it is read
            // through its index, and its list is sorted instead of walking the date index (numbers in work\AP-04b\impl-notes.md).
            accountColumn = "f.account_id";
        }

        if (query.AccountId is Guid accountId)
        {
            whereClause += $" AND {accountColumn} = $account_id";
            command.Parameters.AddWithValue("$account_id", SqliteValues.FromGuid(accountId));
        }

        // One parameter, however many ids: an empty folder can hide thousands.
        if (query.ExcludedMessageIds is { Count: > 0 } excludedIds)
        {
            whereClause += " AND m.id NOT IN (SELECT value FROM json_each($excluded_ids))";
            command.Parameters.AddWithValue(
                "$excluded_ids",
                "[" + string.Join(',', excludedIds.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]");
        }

        return joinClause + whereClause;
    }

    private static async Task<IReadOnlyList<MailMessage>> ReadMessagesAsync(SqliteCommand command, CancellationToken ct)
    {
        var messages = new List<MailMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            messages.Add(new MailMessage(
                Id: reader.GetInt64(0),
                AccountId: SqliteValues.ReadGuid(reader, 1),
                FolderId: reader.GetInt64(2),
                Uid: SqliteValues.ReadUInt32(reader, 3),
                MessageId: SqliteValues.ReadNullableString(reader, 4),
                Subject: reader.GetString(5),
                FromAddress: reader.GetString(6),
                FromName: reader.GetString(7),
                Date: SqliteValues.ReadDate(reader, 8),
                Flags: ReadFlags(reader, 9),
                HasAttachments: SqliteValues.ReadBoolean(reader, 10),
                Size: reader.GetInt64(11),
                EmlPath: SqliteValues.ReadNullableString(reader, 12),
                TextPreview: reader.GetString(13)));
        }

        return messages;
    }

    // Flags are a bit set; reject bits that MessageFlags does not define.
    private static MessageFlags ReadFlags(SqliteDataReader reader, int ordinal)
    {
        const long knownBits = (long)(MessageFlags.Seen | MessageFlags.Answered | MessageFlags.Flagged
            | MessageFlags.Deleted | MessageFlags.Draft);
        long value = reader.GetInt64(ordinal);
        if ((value & ~knownBits) != 0)
        {
            throw new StorageException($"Column 'flags' contains unknown bits {value}.");
        }

        return (MessageFlags)value;
    }
}
