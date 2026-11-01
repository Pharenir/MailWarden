namespace MailWarden.Core.Classification;

/// <summary>
/// How far the contact harvest has read a Sent folder. UIDs up to the marker are not read again.
/// </summary>
/// <param name="FolderId">Folder id.</param>
/// <param name="UidValidity">UIDVALIDITY the marker belongs to; another value on the folder invalidates it.</param>
/// <param name="HarvestedUpToUid">Highest handled UID.</param>
public sealed record ContactHarvestMarker(long FolderId, uint UidValidity, uint HarvestedUpToUid);
