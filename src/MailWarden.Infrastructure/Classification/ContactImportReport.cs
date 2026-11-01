namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// Counts of one import; no address data. <paramref name="Read"/> is the sum of the other three.
/// </summary>
/// <param name="Read">Entries read: one per <c>EMAIL</c> line of a vCard and one for a card without any; one per CSV row.</param>
/// <param name="Added">New contacts.</param>
/// <param name="AlreadyKnown">Entries whose address is in the address book or came earlier in the file.</param>
/// <param name="Rejected">Malformed entries.</param>
public sealed record ContactImportReport(int Read, int Added, int AlreadyKnown, int Rejected);
