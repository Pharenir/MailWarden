using MailWarden.Core.Storage;

namespace MailWarden.Core.Classification;

/// <summary>
/// Persists the marker of the contact harvest per folder. Deleting a folder removes its marker as well.
/// </summary>
public interface IContactHarvestMarkerStore
{
    /// <summary>Loads the marker of a folder.</summary>
    /// <param name="folderId">Folder id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The marker, or null when none is stored (or the folder is unknown).</returns>
    Task<ContactHarvestMarker?> GetAsync(long folderId, CancellationToken ct = default);

    /// <summary>Inserts or replaces the marker of <see cref="ContactHarvestMarker.FolderId"/>.</summary>
    /// <param name="marker">Marker to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the marker is stored.</returns>
    /// <exception cref="StorageException">The folder does not exist.</exception>
    Task SaveAsync(ContactHarvestMarker marker, CancellationToken ct = default);
}
