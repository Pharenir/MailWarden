namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// Outcome of one sync pass over an account.
/// </summary>
/// <param name="AccountId">Synced account.</param>
/// <param name="FoldersAdded">Folders stored for the first time.</param>
/// <param name="FoldersRemoved">Local folders removed because they vanished on the server or became hidden.</param>
/// <param name="Folders">One result per synced folder, in sync order.</param>
public sealed record SyncReport(Guid AccountId, int FoldersAdded, int FoldersRemoved, IReadOnlyList<FolderSyncResult> Folders);
