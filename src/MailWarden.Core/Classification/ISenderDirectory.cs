namespace MailWarden.Core.Classification;

/// <summary>
/// Lookups backed by the address book and the allow/block lists (implemented in AP-06/AP-07b). Synchronous on
/// purpose: implementations keep an in-memory snapshot. Matching rules (address exact, case-insensitive; domain lists
/// match the sender's organisational domain) are the implementation's job; the classifier just asks.
/// </summary>
public interface ISenderDirectory
{
    /// <summary>Whether the address belongs to a contact in the address book.</summary>
    /// <param name="emailAddress">From address of the message as given by the caller.</param>
    /// <returns>True when the sender is a contact.</returns>
    bool IsContact(string emailAddress);

    /// <summary>Allow/block list entry that matches the address or its domain.</summary>
    /// <param name="emailAddress">From address of the message as given by the caller.</param>
    /// <returns>The matching list, or <see cref="ListMembership.None"/>.</returns>
    ListMembership GetListMembership(string emailAddress);
}
