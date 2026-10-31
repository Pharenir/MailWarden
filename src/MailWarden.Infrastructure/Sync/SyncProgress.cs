using MailWarden.Core.Model;

namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// Progress of a running pass. Carries no folder names, subjects or addresses; <see cref="FolderId"/> lets the UI look
/// the name up.
/// </summary>
/// <param name="AccountId">Account of the pass.</param>
/// <param name="FolderId">Local id of the folder being synced.</param>
/// <param name="Role">Role of that folder.</param>
/// <param name="FolderNumber">1-based position of the folder in sync order.</param>
/// <param name="FolderCount">Number of folders synced in this pass.</param>
/// <param name="Processed">Candidates handled so far in the folder (stored, skipped or failed).</param>
/// <param name="Candidates">Number of missing UIDs found for the folder; 0 until known.</param>
public sealed record SyncProgress(
    Guid AccountId,
    long FolderId,
    FolderRole Role,
    int FolderNumber,
    int FolderCount,
    int Processed,
    int Candidates);
