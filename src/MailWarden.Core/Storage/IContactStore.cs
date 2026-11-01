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

    /// <summary>
    /// Inserts the contact unless its address exists (case-insensitive). An existing contact stays unchanged, whatever its
    /// source, so a harvest never overwrites a manual or imported name. The <see cref="Contact.Id"/> of the argument is ignored.
    /// </summary>
    /// <param name="contact">Contact to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored contact: the new one, or the existing one with its own values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contact"/> is null.</exception>
    /// <exception cref="ArgumentException">The address is empty or whitespace.</exception>
    Task<Contact> AddIfMissingAsync(Contact contact, CancellationToken ct = default);

    /// <summary>Removes the contact with the address (case-insensitive). Unknown address = no-op.</summary>
    /// <param name="emailAddress">Address of the contact.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the contact is gone.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="emailAddress"/> is null.</exception>
    Task RemoveAsync(string emailAddress, CancellationToken ct = default);
}
