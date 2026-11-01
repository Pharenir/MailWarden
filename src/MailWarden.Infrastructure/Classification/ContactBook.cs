using MailWarden.Core.Classification;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// <see cref="IContactBook"/> over the two stores. Its only own rules are about a manual contact: the address is trimmed and
/// checked with <see cref="AddressRules"/>, and the name is cleaned like a collected one.
/// </summary>
/// <param name="contacts">Address book.</param>
/// <param name="senderLists">Allow and block lists.</param>
public sealed class ContactBook(IContactStore contacts, ISenderListStore senderLists) : IContactBook
{
    private readonly IContactStore contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
    private readonly ISenderListStore senderLists = senderLists ?? throw new ArgumentNullException(nameof(senderLists));

    /// <inheritdoc />
    public async Task<Contact> AddContactAsync(string address, string name, CancellationToken ct = default)
    {
        string? trimmedAddress = address?.Trim();
        if (!AddressRules.IsWellFormedAddress(trimmedAddress))
        {
            throw new ArgumentException("A contact needs an address with '@' and a domain.", nameof(address));
        }

        return await contacts
            .UpsertAsync(new Contact(0, trimmedAddress, ContactNames.Clean(name), ContactSource.Manual), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RemoveContactAsync(string address, CancellationToken ct = default)
    {
        return contacts.RemoveAsync(address, ct);
    }

    /// <inheritdoc />
    public Task AllowAsync(string entry, bool isDomain, CancellationToken ct = default)
    {
        return senderLists.SetAsync(new SenderListEntry(entry, isDomain, ListMembership.Allowed), ct);
    }

    /// <inheritdoc />
    public Task BlockAsync(string entry, bool isDomain, CancellationToken ct = default)
    {
        return senderLists.SetAsync(new SenderListEntry(entry, isDomain, ListMembership.Blocked), ct);
    }

    /// <inheritdoc />
    public Task UnlistAsync(string entry, bool isDomain, CancellationToken ct = default)
    {
        return senderLists.RemoveAsync(entry, isDomain, ct);
    }
}
