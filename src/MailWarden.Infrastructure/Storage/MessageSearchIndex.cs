using System.Text;
using MailWarden.Core.Model;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// Maintains the FTS5 table <c>messages_fts</c> (rowid = <c>messages.id</c>) and builds safe MATCH expressions.
/// The only writer of <c>messages_fts</c> (the search query in <c>SqliteMessageStore</c> joins it read-only); every
/// caller passes the transaction of the row change so that row and index entry never diverge. Foreign-key cascades do not reach the
/// virtual table, hence the explicit account/folder/UID deletes before the rows go.
/// </summary>
internal static class MessageSearchIndex
{
    /// <summary>Writes (replaces) the index entry of a message.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="transaction">Transaction of the row change.</param>
    /// <param name="messageId">Stored message id (= FTS rowid).</param>
    /// <param name="message">Message whose subject and sender are indexed.</param>
    /// <param name="bodyText">New body text; null keeps the currently indexed body (empty if none).</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task WriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        MailMessage message,
        string? bodyText,
        CancellationToken ct)
    {
        string indexedBody = bodyText
            ?? await ReadIndexedBodyAsync(connection, transaction, messageId, ct).ConfigureAwait(false)
            ?? string.Empty;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM messages_fts WHERE rowid = $id;
            INSERT INTO messages_fts (rowid, subject, from_address, from_name, body_text)
            VALUES ($id, $subject, $from_address, $from_name, $body_text);
            """;
        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$subject", message.Subject);
        command.Parameters.AddWithValue("$from_address", message.FromAddress);
        command.Parameters.AddWithValue("$from_name", message.FromName);
        command.Parameters.AddWithValue("$body_text", indexedBody);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes the index entry of one message (no-op when absent).</summary>
    internal static Task DeleteForMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        CancellationToken ct) =>
        ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM messages_fts WHERE rowid = $value;",
            messageId,
            ct);

    /// <summary>
    /// Removes the index entries of the messages with the given UIDs in one folder; call before deleting the rows.
    /// Unknown UIDs are no-ops.
    /// </summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="transaction">Transaction of the row deletion.</param>
    /// <param name="folderId">Folder id.</param>
    /// <param name="uids">IMAP UIDs whose index entries are removed.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task DeleteForUidsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long folderId,
        IEnumerable<uint> uids,
        CancellationToken ct)
    {
        // One prepared command, executed once per UID.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM messages_fts
            WHERE rowid IN (SELECT id FROM messages WHERE folder_id = $folder_id AND uid = $uid);
            """;
        command.Parameters.AddWithValue("$folder_id", folderId);
        SqliteParameter uidParameter = command.Parameters.Add("$uid", SqliteType.Integer);
        foreach (uint uid in uids)
        {
            uidParameter.Value = (long)uid;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Removes the index entries of all messages of a folder; call before deleting the folder.</summary>
    internal static Task DeleteForFolderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long folderId,
        CancellationToken ct) =>
        ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM messages_fts WHERE rowid IN (SELECT id FROM messages WHERE folder_id = $value);",
            folderId,
            ct);

    /// <summary>Removes the index entries of all messages of an account; call before deleting the account.</summary>
    internal static Task DeleteForAccountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid accountId,
        CancellationToken ct) =>
        ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM messages_fts WHERE rowid IN (SELECT id FROM messages WHERE account_id = $value);",
            SqliteValues.FromGuid(accountId),
            ct);

    /// <summary>
    /// Characters that separate search tokens: whitespace and control characters. Control characters must never
    /// reach FTS5, which reads the MATCH argument as a C string and would cut it at <c>\0</c> (unterminated quote).
    /// </summary>
    /// <param name="character">Character of the search input.</param>
    /// <returns>True when the character separates tokens.</returns>
    internal static bool IsTokenSeparator(char character) => char.IsWhiteSpace(character) || char.IsControl(character);

    /// <summary>Checks whether the search input contains at least one token (see <see cref="IsTokenSeparator"/>).</summary>
    /// <param name="text">Raw search input.</param>
    /// <returns>False when the input is empty or consists only of separators.</returns>
    internal static bool ContainsToken(string text)
    {
        foreach (char character in text)
        {
            if (!IsTokenSeparator(character))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Turns user input into an FTS5 MATCH expression: every token (split at whitespace and control characters)
    /// becomes a quoted prefix phrase <c>"token"*</c> (embedded quotes doubled), tokens are AND-combined. Tokens
    /// without any letter or digit produce no index terms and are dropped.
    /// </summary>
    /// <param name="text">Raw search input.</param>
    /// <returns>The MATCH expression, or null when no searchable token remains.</returns>
    internal static string? BuildMatchExpression(string text)
    {
        var expression = new StringBuilder();
        foreach (string token in SplitTokens(text))
        {
            if (!ContainsLetterOrDigit(token))
            {
                continue;
            }

            if (expression.Length > 0)
            {
                expression.Append(' ');
            }

            expression.Append('"').Append(token.Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\"*");
        }

        return expression.Length == 0 ? null : expression.ToString();
    }

    // Separators are dropped here, so no token contains whitespace or a control character.
    private static IEnumerable<string> SplitTokens(string text)
    {
        int tokenStart = -1;
        for (int index = 0; index < text.Length; index++)
        {
            if (IsTokenSeparator(text[index]))
            {
                if (tokenStart >= 0)
                {
                    yield return text[tokenStart..index];
                    tokenStart = -1;
                }
            }
            else if (tokenStart < 0)
            {
                tokenStart = index;
            }
        }

        if (tokenStart >= 0)
        {
            yield return text[tokenStart..];
        }
    }

    private static bool ContainsLetterOrDigit(string token)
    {
        foreach (Rune character in token.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(character) || Rune.IsNumber(character))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string?> ReadIndexedBodyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT body_text FROM messages_fts WHERE rowid = $id;";
        command.Parameters.AddWithValue("$id", messageId);
        object? body = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return body as string;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        object value,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
