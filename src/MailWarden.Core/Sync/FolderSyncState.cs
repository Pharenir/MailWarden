namespace MailWarden.Core.Sync;

/// <summary>
/// Sync progress of one folder.
/// </summary>
/// <param name="FolderId">Database id of the folder.</param>
/// <param name="HighestSyncedUid">
/// Every UID up to and including this value was either stored or deliberately skipped (initial window).
/// </param>
/// <param name="HighestModSeq">CONDSTORE value of the last completed flag sync (0 = none).</param>
/// <param name="LastSyncedAt">End of the last completed sync of the folder, or null.</param>
public sealed record FolderSyncState(long FolderId, uint HighestSyncedUid, ulong HighestModSeq, DateTimeOffset? LastSyncedAt);
