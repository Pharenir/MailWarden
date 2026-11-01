using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// <see cref="IContactStore"/> on the <c>contacts</c> table. The address column is unique with <c>NOCASE</c>, so two
/// spellings of one address are the same contact; the latest write wins for all its values.
/// </summary>
/// <param name="database">Initialised database.</param>
public sealed class SqliteContactStore(SqliteDatabase database) : IContactStore
{
    private readonly SqliteDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public async Task<IReadOnlyList<Contact>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                SELECT id, email_address, display_name, source
                FROM contacts
                ORDER BY email_address COLLATE NOCASE, id;
                """;
            var contacts = new List<Contact>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                contacts.Add(ReadContact(reader));
            }

            return contacts;
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Listing contacts", exception);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="contact"/> is null.</exception>
    public async Task<Contact> UpsertAsync(Contact contact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (string.IsNullOrWhiteSpace(contact.EmailAddress))
        {
            throw new ArgumentException("A contact needs an address.", nameof(contact));
        }

        try
        {
            await using var command = database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO contacts (email_address, display_name, source)
                VALUES ($email_address, $display_name, $source)
                ON CONFLICT (email_address) DO UPDATE SET
                    email_address = excluded.email_address,
                    display_name = excluded.display_name,
                    source = excluded.source
                RETURNING id, email_address, display_name, source;
                """;
            command.Parameters.AddWithValue("$email_address", contact.EmailAddress);
            command.Parameters.AddWithValue("$display_name", contact.DisplayName);
            command.Parameters.AddWithValue("$source", (int)contact.Source);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            return ReadContact(reader);
        }
        catch (SqliteException exception)
        {
            throw SqliteValues.Wrap("Storing the contact", exception);
        }
    }

    private static Contact ReadContact(SqliteDataReader reader)
    {
        return new Contact(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            SqliteValues.ReadEnum<ContactSource>(reader, 3));
    }
}
