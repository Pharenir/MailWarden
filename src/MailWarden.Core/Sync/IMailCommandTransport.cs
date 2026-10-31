using MailWarden.Core.Model;

namespace MailWarden.Core.Sync;

/// <summary>
/// One authenticated IMAP session that changes the server (command queue). Separate from the read-only
/// <see cref="IMailTransport"/> of the sync pass. Not thread-safe; one selected folder at a time. A selected folder is
/// left only by selecting another one, by UNSELECT or by logout; never by CLOSE, and a plain EXPUNGE is never sent.
/// Failures surface as <see cref="MailTransportException"/>; cancellation as <see cref="OperationCanceledException"/>.
/// </summary>
public interface IMailCommandTransport : IAsyncDisposable
{
    /// <summary>
    /// True while the session is connected and authenticated; false after a lost connection or after dispose.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>Selects the folder read-write (SELECT); the previously selected folder is left without expunge.</summary>
    /// <param name="fullName">Full folder name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UIDVALIDITY of the folder.</returns>
    Task<uint> SelectFolderAsync(string fullName, CancellationToken ct = default);

    /// <summary>Adds system flags to messages of the selected folder (UID STORE +FLAGS).</summary>
    /// <param name="uids">UIDs in the selected folder.</param>
    /// <param name="flags">Seen, Answered, Flagged and/or Draft; Deleted is not allowed (see <see cref="ExpungeAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the server confirmed the change.</returns>
    Task AddFlagsAsync(IReadOnlyList<uint> uids, MessageFlags flags, CancellationToken ct = default);

    /// <summary>Removes system flags from messages of the selected folder (UID STORE -FLAGS).</summary>
    /// <param name="uids">UIDs in the selected folder.</param>
    /// <param name="flags">Seen, Answered, Flagged and/or Draft; Deleted is not allowed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the server confirmed the change.</returns>
    Task RemoveFlagsAsync(IReadOnlyList<uint> uids, MessageFlags flags, CancellationToken ct = default);

    /// <summary>
    /// UID MOVE of messages of the selected folder to the target folder. UIDs that no longer exist are ignored by the
    /// server.
    /// </summary>
    /// <param name="uids">UIDs in the selected folder.</param>
    /// <param name="targetFullName">Full name of the target folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Source UID → new UID in the target plus the target's UIDVALIDITY (COPYUID); <see cref="CopyUidMap.Empty"/> when
    /// the server sent none or the COPYUID data is inconsistent (unequal counts, a repeated or zero destination UID, a
    /// source UID that was not requested, or mixed destination UIDVALIDITY values).
    /// </returns>
    Task<CopyUidMap> MoveAsync(
        IReadOnlyList<uint> uids,
        string targetFullName,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes exactly these messages of the selected folder permanently: STORE +FLAGS \Deleted for these UIDs, then
    /// UID EXPUNGE of these UIDs. Other messages marked \Deleted stay untouched. When the expunge fails after the
    /// \Deleted flag was stored, the flag is removed again for the same UIDs (best effort, its failure is ignored) and the
    /// original failure propagates.
    /// </summary>
    /// <param name="uids">UIDs in the selected folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the server confirmed the expunge.</returns>
    Task ExpungeAsync(IReadOnlyList<uint> uids, CancellationToken ct = default);
}
