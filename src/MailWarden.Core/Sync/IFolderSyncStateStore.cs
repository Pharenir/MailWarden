using MailWarden.Core.Storage;

namespace MailWarden.Core.Sync;

/// <summary>
/// Persists the sync state per folder. Deleting a folder removes its state as well.
/// </summary>
public interface IFolderSyncStateStore
{
    /// <summary>Loads the state of a folder.</summary>
    /// <param name="folderId">Folder id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The state, or null when none is stored (or the folder is unknown).</returns>
    Task<FolderSyncState?> GetAsync(long folderId, CancellationToken ct = default);

    /// <summary>Inserts or replaces the state of <see cref="FolderSyncState.FolderId"/>.</summary>
    /// <param name="state">State to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the state is stored.</returns>
    /// <exception cref="StorageException">The folder does not exist.</exception>
    Task SaveAsync(FolderSyncState state, CancellationToken ct = default);

    /// <summary>Deletes the state of a folder. Missing state = no-op.</summary>
    /// <param name="folderId">Folder id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the state is gone.</returns>
    Task DeleteAsync(long folderId, CancellationToken ct = default);
}
