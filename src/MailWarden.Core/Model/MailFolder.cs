namespace MailWarden.Core.Model;

/// <summary>
/// A folder of an account. <paramref name="Id"/> == 0 means "not stored yet"; stores return a copy with the
/// database id.
/// </summary>
/// <param name="Id">Database id, 0 before the first store.</param>
/// <param name="AccountId">Owning account.</param>
/// <param name="FullName">Full IMAP folder name including hierarchy delimiters; unique per account.</param>
/// <param name="Role">Explicit folder role (never derived from <paramref name="FullName"/> by storage).</param>
/// <param name="UidValidity">IMAP UIDVALIDITY of the folder.</param>
public sealed record MailFolder(long Id, Guid AccountId, string FullName, FolderRole Role, uint UidValidity);
