using MailWarden.Core.Model;

namespace MailWarden.Core.Storage;

/// <summary>
/// Persists the address book.
/// </summary>
public interface IContactStore
{
    /// <summary>Lists all contacts.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All contacts ordered by address, case-insensitive for ASCII letters only (SQLite <c>NOCASE</c>).</returns>
    Task<IReadOnlyList<Contact>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Inserts the contact or updates the existing one with the same address (case-insensitive). The
    /// <see cref="Contact.Id"/> of the argument is ignored.
    /// </summary>
    /// <param name="contact">Contact to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored copy with its database id.</returns>
    /// <exception cref="ArgumentException">The address is empty or whitespace.</exception>
    Task<Contact> UpsertAsync(Contact contact, CancellationToken ct = default);
}
