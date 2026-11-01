namespace MailWarden.Core.Classification;

/// <summary>
/// Persists the decisions of the classification pass and the marker per folder. One decision per
/// (account, message key): a key that is in the log is never decided again.
/// </summary>
public interface IClassificationLog
{
    /// <summary>Loads the decision for a message key.</summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="messageKey">Key from <see cref="MessageKey.For"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored decision, or null when the key is unknown.</returns>
    Task<ClassificationLogEntry?> GetAsync(Guid accountId, string messageKey, CancellationToken ct = default);

    /// <summary>Loads the marker of a folder.</summary>
    /// <param name="folderId">Folder id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The marker of the folder, or null when it has none.</returns>
    Task<ClassificationMarker?> GetMarkerAsync(long folderId, CancellationToken ct = default);

    /// <summary>
    /// One transaction: inserts the entries (an existing (AccountId, MessageKey) stays unchanged) and sets the marker.
    /// </summary>
    /// <param name="entries">Decisions to store; may be empty.</param>
    /// <param name="marker">New marker of the folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the transaction is committed.</returns>
    Task CommitAsync(
        IReadOnlyCollection<ClassificationLogEntry> entries,
        ClassificationMarker marker,
        CancellationToken ct = default);
}
