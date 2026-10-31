using MailWarden.Core.Model;

namespace MailWarden.Core.Commands;

/// <summary>
/// Queue of server changes for the UI and the classifier. Every Enqueue validates, stores one command and returns it;
/// nothing is changed locally or on the server at this point (the executor does that later).
/// </summary>
/// <remarks>
/// Common validation of every Enqueue (nothing is stored when it fails):
/// <list type="bullet">
/// <item>unknown source folder → <see cref="ArgumentException"/>;</item>
/// <item>UIDs null → <see cref="ArgumentNullException"/>; empty → <see cref="ArgumentException"/>; a UID 0 or more
/// than 1,000 distinct UIDs → <see cref="ArgumentOutOfRangeException"/>; duplicates are removed and the stored list is
/// ascending;</item>
/// <item>the source folder was never synced (stored UIDVALIDITY 0) → <see cref="ArgumentException"/>; otherwise the
/// stored UIDVALIDITY becomes <see cref="MailCommand.FolderUidValidity"/>.</item>
/// </list>
/// </remarks>
public interface IMailCommandQueue
{
    /// <summary>Adds and/or removes Seen, Flagged, Answered, Draft. Deleted is never allowed here (use the delete commands).</summary>
    /// <param name="folderId">Source folder.</param>
    /// <param name="uids">UIDs in the source folder.</param>
    /// <param name="flagsToAdd">Flags to add.</param>
    /// <param name="flagsToRemove">Flags to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored command (Pending).</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A flag outside Seen | Flagged | Answered | Draft (including Deleted) is given; or see the common validation.
    /// </exception>
    /// <exception cref="ArgumentException">Both are None or add and remove overlap; or see the common validation.</exception>
    Task<MailCommand> EnqueueSetFlagsAsync(
        long folderId,
        IReadOnlyCollection<uint> uids,
        MessageFlags flagsToAdd,
        MessageFlags flagsToRemove,
        CancellationToken ct = default);

    /// <summary>Moves messages to another folder of the same account.</summary>
    /// <param name="folderId">Source folder.</param>
    /// <param name="uids">UIDs in the source folder.</param>
    /// <param name="targetFolderId">Target folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored command (Pending).</returns>
    /// <exception cref="ArgumentException">
    /// The target is unknown, belongs to another account or is the source; or see the common validation.
    /// </exception>
    Task<MailCommand> EnqueueMoveAsync(
        long folderId,
        IReadOnlyCollection<uint> uids,
        long targetFolderId,
        CancellationToken ct = default);

    /// <summary>
    /// Del key: moves messages to the account's folder with role Trash (resolved now and stored as
    /// <see cref="MailCommand.TargetFolderId"/>).
    /// </summary>
    /// <param name="folderId">Source folder.</param>
    /// <param name="uids">UIDs in the source folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored command (Pending).</returns>
    /// <exception cref="InvalidOperationException">The account has no folder with role Trash.</exception>
    /// <exception cref="ArgumentException">
    /// The source is the Trash folder itself (the UI asks and uses <see cref="EnqueueDeletePermanentlyAsync"/>); or see
    /// the common validation.
    /// </exception>
    Task<MailCommand> EnqueueMoveToTrashAsync(
        long folderId,
        IReadOnlyCollection<uint> uids,
        CancellationToken ct = default);

    /// <summary>Shift+Del: deletes messages permanently (from any folder).</summary>
    /// <param name="folderId">Source folder.</param>
    /// <param name="uids">UIDs in the source folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored command (Pending).</returns>
    /// <exception cref="ArgumentException">See the common validation.</exception>
    Task<MailCommand> EnqueueDeletePermanentlyAsync(
        long folderId,
        IReadOnlyCollection<uint> uids,
        CancellationToken ct = default);

    /// <summary>
    /// "Ordner leeren": only for role Trash or Junk. Takes the UIDs stored locally for the folder now and enqueues
    /// <see cref="MailCommandKind.DeletePermanently"/> for exactly these; messages that arrive later are never touched.
    /// </summary>
    /// <param name="folderId">Folder to empty.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored command, or null when the folder has no local messages.</returns>
    /// <exception cref="ArgumentException">
    /// The folder is unknown, its role is neither Trash nor Junk, or it was never synced (UIDVALIDITY 0).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The folder holds more than 1,000 local messages.</exception>
    Task<MailCommand?> EnqueueEmptyFolderAsync(long folderId, CancellationToken ct = default);

    /// <summary>Lists the pending and failed commands of the account, oldest first.</summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Commands in queue order; empty for an unknown account.</returns>
    Task<IReadOnlyList<MailCommand>> ListAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Removes a command (pending or failed) without executing it. Unknown id = no-op.</summary>
    /// <param name="commandId">Command id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the command is gone.</returns>
    Task DiscardAsync(long commandId, CancellationToken ct = default);
}
