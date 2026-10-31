namespace MailWarden.Core.Model;

/// <summary>
/// An address book entry. <paramref name="Id"/> == 0 means "not stored yet".
/// </summary>
/// <param name="Id">Database id, 0 before the first store.</param>
/// <param name="EmailAddress">Address; unique across contacts (case-insensitive).</param>
/// <param name="DisplayName">Display name (empty when unknown).</param>
/// <param name="Source">Where the entry came from.</param>
public sealed record Contact(long Id, string EmailAddress, string DisplayName, ContactSource Source);
