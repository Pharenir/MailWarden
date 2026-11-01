using MailWarden.Core.Classification;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// In-memory snapshot of the contacts plus the addresses of all accounts. Addresses are compared case-insensitively and
/// exactly. Allow/block lists come with AP-06; until then <see cref="GetListMembership"/> returns
/// <see cref="ListMembership.None"/> for every address.
/// </summary>
public sealed class ContactSenderDirectory : ISenderDirectory
{
    private readonly HashSet<string> knownAddresses;

    /// <summary>Takes the snapshot.</summary>
    /// <param name="contactAddresses">Addresses from the address book.</param>
    /// <param name="ownAddresses">Addresses of all accounts.</param>
    /// <exception cref="ArgumentNullException">A list is null.</exception>
    /// <exception cref="ArgumentException">A list contains a null entry.</exception>
    public ContactSenderDirectory(IEnumerable<string> contactAddresses, IEnumerable<string> ownAddresses)
    {
        ArgumentNullException.ThrowIfNull(contactAddresses);
        ArgumentNullException.ThrowIfNull(ownAddresses);

        var contacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string contactAddress in contactAddresses)
        {
            contacts.Add(contactAddress ?? throw new ArgumentException("A contact address is null.", nameof(contactAddresses)));
        }

        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string ownAddress in ownAddresses)
        {
            own.Add(ownAddress ?? throw new ArgumentException("An account address is null.", nameof(ownAddresses)));
        }

        // An own address in the address book is not a contact: it must not lift the suspension of Act.
        ContactCount = contacts.Count(contact => !own.Contains(contact));
        contacts.UnionWith(own);
        knownAddresses = contacts;
    }

    /// <summary>Number of distinct contact addresses; own addresses do not count.</summary>
    public int ContactCount { get; }

    /// <inheritdoc />
    /// <returns>True for a contact and for the address of an account.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="emailAddress"/> is null.</exception>
    public bool IsContact(string emailAddress)
    {
        ArgumentNullException.ThrowIfNull(emailAddress);
        return knownAddresses.Contains(emailAddress);
    }

    /// <inheritdoc />
    /// <returns>Always <see cref="ListMembership.None"/> until AP-06 adds the lists.</returns>
    public ListMembership GetListMembership(string emailAddress) => ListMembership.None;
}
