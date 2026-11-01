using MailWarden.Core.Commands;
using MailWarden.Core.Model;

namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// A queued command as the user interface sees it.
/// </summary>
/// <param name="CommandId">Id of the command in the queue.</param>
/// <param name="AccountId">Account of the command.</param>
/// <param name="Kind">What the command does on the server.</param>
/// <param name="Status">Pending or permanently failed.</param>
/// <param name="MessageIds">Stored messages of the command; a UID that is no longer stored is skipped.</param>
/// <param name="LastError">Type name or fixed English text of this app; the user interface only shows fixed texts.</param>
public sealed record PendingAction(
    long CommandId,
    Guid AccountId,
    MailCommandKind Kind,
    MailCommandStatus Status,
    IReadOnlyList<long> MessageIds,
    string? LastError);

/// <summary>
/// What the user interface may change. Every member only stores commands in the queue and schedules a cycle; the executor
/// changes server and local data later. Access to the connection behind it is serialised, and no call blocks the caller's
/// thread for the query.
/// </summary>
/// <remarks>
/// Messages are grouped per folder, at most 1,000 UIDs per command, UIDVALIDITY as stored now. A rejection stops the call
/// with a <see cref="MailActionException"/>; commands stored before it stay and their accounts are still triggered.
/// </remarks>
public interface IMailboxActions
{
    /// <summary>Adds or removes the Seen flag.</summary>
    /// <param name="messages">Messages to change.</param>
    /// <param name="seen">True marks as read, false as unread.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the commands are stored.</returns>
    /// <exception cref="MailActionException">The queue rejected a group.</exception>
    Task SetSeenAsync(IReadOnlyCollection<MailMessage> messages, bool seen, CancellationToken ct = default);

    /// <summary>Adds or removes the Flagged flag.</summary>
    /// <param name="messages">Messages to change.</param>
    /// <param name="flagged">True sets the flag, false removes it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the commands are stored.</returns>
    /// <exception cref="MailActionException">The queue rejected a group.</exception>
    Task SetFlaggedAsync(IReadOnlyCollection<MailMessage> messages, bool flagged, CancellationToken ct = default);

    /// <summary>
    /// Del: moves messages to the Trash of their account. A mail that is already in the Trash is refused: the user interface
    /// asks and uses <see cref="DeletePermanentlyAsync"/>.
    /// </summary>
    /// <param name="messages">Messages to move.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the commands are stored.</returns>
    /// <exception cref="MailActionException">The queue rejected a group.</exception>
    Task MoveToTrashAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken ct = default);

    /// <summary>Shift+Del: deletes messages permanently. The caller has asked the user before.</summary>
    /// <param name="messages">Messages to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the commands are stored.</returns>
    /// <exception cref="MailActionException">The queue rejected a group.</exception>
    Task DeletePermanentlyAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken ct = default);

    /// <summary>Moves messages to another folder of the same account.</summary>
    /// <param name="messages">Messages to move.</param>
    /// <param name="targetFolderId">Target folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the commands are stored.</returns>
    /// <exception cref="MailActionException">The queue rejected a group, for instance a target of another account.</exception>
    Task MoveAsync(IReadOnlyCollection<MailMessage> messages, long targetFolderId, CancellationToken ct = default);

    /// <summary>Number of local mails right now; the user interface shows it in the confirmation.</summary>
    /// <param name="folderId">Folder with role Trash or Junk.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of local mails of the folder.</returns>
    /// <exception cref="MailActionException">The folder is unknown or cannot be emptied.</exception>
    Task<int> CountForEmptyAsync(long folderId, CancellationToken ct = default);

    /// <summary>"Ordner leeren": one command with the UIDs stored now. A folder without local mails stores nothing.</summary>
    /// <param name="folderId">Folder with role Trash or Junk.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the command is stored.</returns>
    /// <exception cref="MailActionException">The folder is unknown or cannot be emptied.</exception>
    Task EmptyFolderAsync(long folderId, CancellationToken ct = default);

    /// <summary>Lists the pending and failed commands of all accounts, per account oldest first.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The commands with their messages resolved from folder and UIDs.</returns>
    Task<IReadOnlyList<PendingAction>> ListPendingAsync(CancellationToken ct = default);

    /// <summary>Removes a command (pending or failed) without executing it. Unknown id = no-op.</summary>
    /// <param name="commandId">Command id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the command is gone.</returns>
    Task DiscardAsync(long commandId, CancellationToken ct = default);
}
