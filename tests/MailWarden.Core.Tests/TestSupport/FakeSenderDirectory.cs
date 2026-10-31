namespace MailWarden.Core.Tests.TestSupport;

/// <summary>In-memory <see cref="ISenderDirectory"/>: exact, case-insensitive address matching.</summary>
internal sealed class FakeSenderDirectory : ISenderDirectory
{
    private readonly HashSet<string> contacts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);

    public FakeSenderDirectory AddContact(string emailAddress)
    {
        contacts.Add(emailAddress);
        return this;
    }

    public FakeSenderDirectory AddAllowed(string emailAddress)
    {
        allowed.Add(emailAddress);
        return this;
    }

    public FakeSenderDirectory AddBlocked(string emailAddress)
    {
        blocked.Add(emailAddress);
        return this;
    }

    public bool IsContact(string emailAddress) => contacts.Contains(emailAddress);

    public ListMembership GetListMembership(string emailAddress)
    {
        if (blocked.Contains(emailAddress))
        {
            return ListMembership.Blocked;
        }

        return allowed.Contains(emailAddress) ? ListMembership.Allowed : ListMembership.None;
    }
}
