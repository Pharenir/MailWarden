using MailWarden.Core.Classification;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// <see cref="IContactBook"/> over the two stores. Its only own rule: a name is cleaned like a collected one.
/// </summary>
/// <param name="contacts">Address book.</param>
/// <param name="senderLists">Allow and block lists.</param>
public sealed class ContactBook(IContactStore contacts, ISenderListStore senderLists) : IContactBook
{
    private readonly IContactStore contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
    private readonly ISenderListStore senderLists = senderLists ?? throw new ArgumentNullException(nameof(senderLists));

    /// <inheritdoc />
    public Task<Contact> AddContactAsync(string address, string name, CancellationToken ct = default)
    {
        return contacts.UpsertAsync(new Contact(0, address, ContactNames.Clean(name), ContactSource.Manual), ct);
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
