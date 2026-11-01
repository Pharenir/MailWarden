using MailWarden.Core.Classification;
using MailWarden.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// <see cref="ISenderListStore"/> on the <c>sender_list</c> table (schema v6). The text column has <c>NOCASE</c>, so two
/// spellings of one entry are the same entry. Exception messages never quote an entry.
/// </summary>
/// <param name="database">Initialised database.</param>
public sealed class SqliteSenderListStore(SqliteDatabase database) : ISenderListStore
{
    private readonly SqliteDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public async Task<IReadOnlyList<SenderListEntry>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                SELECT entry, is_domain, kind
                FROM sender_list
                ORDER BY entry COLLATE NOCASE, is_domain;
                """;
            var entries = new List<SenderListEntry>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                entries.Add(ReadEntry(reader));
            }

            return entries;
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Listing the sender list", exception);
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(SenderListEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind is not (ListMembership.Allowed or ListMembership.Blocked))
        {
            throw new ArgumentException("A list entry must be allowed or blocked.", nameof(entry));
        }

        string? text = entry.Entry?.Trim();
        if (entry.IsDomain && !AddressRules.IsWellFormedDomain(text))
        {
            throw new ArgumentException("A domain entry needs at least one dot and no '@'.", nameof(entry));
        }

        if (entry.IsDomain && DomainNames.IsPublicSuffix(text!))
        {
            throw new ArgumentException("A domain entry must not be a public suffix.", nameof(entry));
        }

        if (!entry.IsDomain && !AddressRules.IsWellFormedAddress(text))
        {
            throw new ArgumentException("An address entry needs '@' and a domain.", nameof(entry));
        }

        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sender_list (entry, is_domain, kind)
                VALUES ($entry, $is_domain, $kind)
                ON CONFLICT (entry, is_domain) DO UPDATE SET
                    entry = excluded.entry,
                    kind = excluded.kind;
                """;
            command.Parameters.AddWithValue("$entry", text);
            command.Parameters.AddWithValue("$is_domain", entry.IsDomain ? 1 : 0);
            command.Parameters.AddWithValue("$kind", (int)entry.Kind);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Storing the list entry", exception);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string entry, bool isDomain, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = "DELETE FROM sender_list WHERE entry = $entry AND is_domain = $is_domain;";
            command.Parameters.AddWithValue("$entry", entry.Trim());
            command.Parameters.AddWithValue("$is_domain", isDomain ? 1 : 0);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Removing the list entry", exception);
        }
    }

    // A row without a kind cannot have been written by this store.
    private static SenderListEntry ReadEntry(SqliteDataReader reader)
    {
        ListMembership kind = SqliteValues.ReadEnum<ListMembership>(reader, 2);
        if (kind == ListMembership.None)
        {
            throw new StorageException("Column 'kind' of the sender list contains no list.");
        }

        return new SenderListEntry(reader.GetString(0), SqliteValues.ReadBoolean(reader, 1), kind);
    }
}
