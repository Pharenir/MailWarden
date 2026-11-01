using MailWarden.Core.Classification;
using MailWarden.Core.Model;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// What the user can do with the address book and the allow/block lists; for the screens of AP-08.
/// </summary>
public interface IContactBook
{
    /// <summary>Stores a manual contact; a contact with this address gets the new name and the source Manual.</summary>
    /// <param name="address">Address, trimmed before the check of <see cref="AddressRules.IsWellFormedAddress"/>.</param>
    /// <param name="name">Name, empty when unknown; cleaned and limited to 200 characters like a collected name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored contact.</returns>
    /// <exception cref="ArgumentException">The address is not well-formed.</exception>
    Task<Contact> AddContactAsync(string address, string name, CancellationToken ct = default);

    /// <summary>Removes a contact. Unknown address = no-op.</summary>
    /// <param name="address">Address, trimmed before the lookup.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the contact is gone.</returns>
    Task RemoveContactAsync(string address, CancellationToken ct = default);

    /// <summary>Puts the entry on the allow list; it leaves the block list if it was there.</summary>
    /// <param name="entry">Address or domain, see <see cref="ISenderListStore.SetAsync"/>.</param>
    /// <param name="isDomain">True for a domain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the entry is stored.</returns>
    /// <exception cref="ArgumentException">The entry is invalid.</exception>
    Task AllowAsync(string entry, bool isDomain, CancellationToken ct = default);

    /// <summary>Puts the entry on the block list; it leaves the allow list if it was there.</summary>
    /// <param name="entry">Address or domain, see <see cref="ISenderListStore.SetAsync"/>.</param>
    /// <param name="isDomain">True for a domain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the entry is stored.</returns>
    /// <exception cref="ArgumentException">The entry is invalid.</exception>
    Task BlockAsync(string entry, bool isDomain, CancellationToken ct = default);

    /// <summary>Takes the entry off its list. Unknown entry = no-op.</summary>
    /// <param name="entry">Address or domain.</param>
    /// <param name="isDomain">True for a domain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the entry is gone.</returns>
    Task UnlistAsync(string entry, bool isDomain, CancellationToken ct = default);
}
