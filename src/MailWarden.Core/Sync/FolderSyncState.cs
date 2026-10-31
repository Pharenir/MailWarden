namespace MailWarden.Core.Sync;

/// <summary>
/// Sync state of one folder.
/// </summary>
/// <param name="FolderId">Database id of the folder.</param>
/// <param name="SkippedUpToUid">
/// Floor of the folder: UIDs up to and including this value were deliberately left out by the initial window of the
/// first sync (0 = nothing left out). Decided once at the first completed sync and never changed afterwards; every
/// server UID above it that is not stored locally is fetched on each pass (no high-water mark).
/// </param>
/// <param name="HighestModSeq">CONDSTORE value of the last completed flag sync (0 = none).</param>
/// <param name="LastSyncedAt">End of the last completed sync of the folder, or null.</param>
public sealed record FolderSyncState(long FolderId, uint SkippedUpToUid, ulong HighestModSeq, DateTimeOffset? LastSyncedAt);
