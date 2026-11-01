namespace MailWarden.Core.Classification;

/// <summary>
/// How far a folder has been handled by the classification pass. UIDs up to the marker are never classified.
/// </summary>
/// <param name="FolderId">Folder id.</param>
/// <param name="UidValidity">UIDVALIDITY the marker belongs to; another value on the folder invalidates it.</param>
/// <param name="ClassifiedUpToUid">Highest handled UID; 0 when the folder was empty.</param>
public sealed record ClassificationMarker(long FolderId, uint UidValidity, uint ClassifiedUpToUid);
