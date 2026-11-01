using MailWarden.Core.Classification;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// In-memory snapshot of the contacts, the addresses of all accounts and the allow/block lists. Contacts are compared
/// case-insensitively and exactly. An address entry wins over a domain entry; a domain entry applies when the sender's domain
/// equals it or is a sub-domain of it, so <c>mail.example.test</c> does not cover <c>example.test</c>. Among the domain entries
/// that apply to a sender, Blocked wins over Allowed, also when they name different levels of its domain.
/// </summary>
public sealed class ContactSenderDirectory : ISenderDirectory
{
    private readonly HashSet<string> knownAddresses;
    private readonly Dictionary<string, ListMembership> addressEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ListMembership> domainEntries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes the snapshot, with empty allow/block lists.</summary>
    /// <param name="contactAddresses">Addresses from the address book.</param>
    /// <param name="ownAddresses">Addresses of all accounts.</param>
    /// <exception cref="ArgumentNullException">A list is null.</exception>
    /// <exception cref="ArgumentException">A list contains a null entry.</exception>
    public ContactSenderDirectory(IEnumerable<string> contactAddresses, IEnumerable<string> ownAddresses)
        : this(contactAddresses, ownAddresses, [])
    {
    }

    /// <summary>Takes the snapshot.</summary>
    /// <param name="contactAddresses">Addresses from the address book.</param>
    /// <param name="ownAddresses">Addresses of all accounts.</param>
    /// <param name="lists">Entries of the allow/block lists.</param>
    /// <exception cref="ArgumentNullException">A list is null.</exception>
    /// <exception cref="ArgumentException">A list contains a null entry, or a list entry has no kind.</exception>
    public ContactSenderDirectory(
        IEnumerable<string> contactAddresses,
        IEnumerable<string> ownAddresses,
        IEnumerable<SenderListEntry> lists)
    {
        ArgumentNullException.ThrowIfNull(contactAddresses);
        ArgumentNullException.ThrowIfNull(ownAddresses);
        ArgumentNullException.ThrowIfNull(lists);

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

        foreach (SenderListEntry entry in lists)
        {
            if (entry?.Entry is null)
            {
                throw new ArgumentException("A list entry is null.", nameof(lists));
            }

            if (entry.Kind is not (ListMembership.Allowed or ListMembership.Blocked))
            {
                throw new ArgumentException("A list entry must be allowed or blocked.", nameof(lists));
            }

            AddListEntry(entry);
        }
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
    /// <returns>The list of the address entry, else of the domain entry; <see cref="ListMembership.None"/> for a blank or
    /// malformed address.</returns>
    public ListMembership GetListMembership(string emailAddress)
    {
        string? lookupForm = AddressRules.ToLookupForm(emailAddress);
        if (lookupForm is null)
        {
            return ListMembership.None;
        }

        if (addressEntries.TryGetValue(lookupForm, out ListMembership addressKind))
        {
            return addressKind;
        }

        return GetDomainMembership(DomainNames.GetDomain(lookupForm));
    }

    // Walks from the sender's domain up to its last label. Empty labels ("a..b") are dropped, as DomainNames does.
    private ListMembership GetDomainMembership(string senderDomain)
    {
        string[] labels = senderDomain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        ListMembership membership = ListMembership.None;
        for (int firstLabel = 0; firstLabel < labels.Length; firstLabel++)
        {
            ListMembership kind = domainEntries.GetValueOrDefault(string.Join('.', labels[firstLabel..]));
            if (kind == ListMembership.Blocked)
            {
                return ListMembership.Blocked;
            }

            if (kind == ListMembership.Allowed)
            {
                membership = ListMembership.Allowed;
            }
        }

        return membership;
    }

    private static void Merge(Dictionary<string, ListMembership> entries, string key, ListMembership kind)
    {
        // Two spellings can reach one key here (NOCASE in the store only knows ASCII letters).
        entries[key] = entries.GetValueOrDefault(key) == ListMembership.Blocked ? ListMembership.Blocked : kind;
    }

    private void AddListEntry(SenderListEntry entry)
    {
        if (entry.IsDomain)
        {
            // Same form as DomainNames gives for the sender. The comparer takes care of the case.
            Merge(domainEntries, entry.Entry.Trim().TrimEnd('.'), entry.Kind);
        }
        else if (AddressRules.ToLookupForm(entry.Entry) is string lookupForm)
        {
            Merge(addressEntries, lookupForm, entry.Kind);
        }
    }
}
