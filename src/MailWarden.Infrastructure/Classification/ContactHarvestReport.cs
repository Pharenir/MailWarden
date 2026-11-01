namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// Counts of one contact harvest; no message or address data.
/// </summary>
/// <param name="MailsRead">Mails whose headers were read. A skipped mail does not count here.</param>
/// <param name="ContactsAdded">New contacts.</param>
/// <param name="MailsSkipped">Mails with a missing or unreadable EML.</param>
public sealed record ContactHarvestReport(int MailsRead, int ContactsAdded, int MailsSkipped);
