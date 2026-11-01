namespace MailWarden.Core.Classification;

/// <summary>
/// One entry of the manual allow/block lists.
/// </summary>
/// <param name="Entry">An address, or a domain when <paramref name="IsDomain"/> is true.</param>
/// <param name="IsDomain">True for a domain entry, which applies to that domain and its sub-domains.</param>
/// <param name="Kind">The list the entry is on.</param>
public sealed record SenderListEntry(string Entry, bool IsDomain, ListMembership Kind);
