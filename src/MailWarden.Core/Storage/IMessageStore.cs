using MailWarden.Core.Model;

namespace MailWarden.Core.Storage;

/// <summary>
/// Persists message headers and maintains the full-text index.
/// </summary>
public interface IMessageStore
{
    /// <summary>
    /// Inserts or updates the message by key (<see cref="MailMessage.FolderId"/>, <see cref="MailMessage.Uid"/>). The
    /// <see cref="MailMessage.Id"/> of the argument is ignored. <see cref="MailMessage.AccountId"/> must be the account
    /// of the folder.
    /// </summary>
    /// <param name="message">Message to store.</param>
    /// <param name="bodyText">
    /// Plain body text for full-text search only. Null keeps the existing indexed text on update and indexes an empty
    /// body on insert.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored copy with its database id.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="MailMessage.Uid"/> is 0 (IMAP UIDs start at 1).</exception>
    /// <exception cref="StorageException">
    /// Account or folder does not exist, or the folder belongs to another account.
    /// </exception>
    Task<MailMessage> UpsertAsync(MailMessage message, string? bodyText, CancellationToken ct = default);

    /// <summary>Loads one message.</summary>
    /// <param name="id">Message id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The message, or null when unknown.</returns>
    Task<MailMessage?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Loads one message by its IMAP UID.</summary>
    /// <param name="folderId">Folder id.</param>
    /// <param name="uid">IMAP UID within the folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The message, or null when unknown.</returns>
    Task<MailMessage?> GetByUidAsync(long folderId, uint uid, CancellationToken ct = default);

    /// <summary>Lists messages for a view.</summary>
    /// <param name="query">Filter and paging.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Messages newest first (<see cref="MailMessage.Date"/> desc, then id desc).</returns>
    Task<IReadOnlyList<MailMessage>> ListAsync(MessageQuery query, CancellationToken ct = default);

    /// <summary>
    /// Full-text search over subject, sender address, sender name and indexed body text; case-insensitive.
    /// Every word must match (prefix match); words are separated by whitespace and control characters; search syntax
    /// characters are treated as text.
    /// </summary>
    /// <param name="text">Search words.</param>
    /// <param name="limit">Maximum number of results, must be greater than 0.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching messages newest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="text"/> is empty or consists only of whitespace and control characters.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not greater than 0.</exception>
    Task<IReadOnlyList<MailMessage>> SearchAsync(string text, int limit = 100, CancellationToken ct = default);

    /// <summary>Deletes one message and its index entry. Unknown id = no-op.</summary>
    /// <param name="id">Message id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the message is deleted.</returns>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Deletes the messages with the given UIDs from one folder. Unknown UIDs and an empty set are no-ops.</summary>
    /// <param name="folderId">Folder id.</param>
    /// <param name="uids">IMAP UIDs to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the messages are deleted.</returns>
    Task DeleteByUidsAsync(long folderId, IReadOnlyCollection<uint> uids, CancellationToken ct = default);

    /// <summary>Loads UID and flags of all messages of one folder.</summary>
    /// <param name="folderId">Folder id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UID → flags; empty for an unknown folder.</returns>
    Task<IReadOnlyDictionary<uint, MessageFlags>> GetUidFlagsAsync(long folderId, CancellationToken ct = default);

    /// <summary>Loads UID and EML path of all messages of one folder.</summary>
    /// <param name="folderId">Folder id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UID → <see cref="MailMessage.EmlPath"/> (null when the row has none); empty for an unknown folder.</returns>
    Task<IReadOnlyDictionary<uint, string?>> GetUidEmlPathsAsync(long folderId, CancellationToken ct = default);

    /// <summary>
    /// Sets the flags of existing messages of one folder in one transaction. Unknown UIDs are ignored. The full-text
    /// index is not touched.
    /// </summary>
    /// <param name="folderId">Folder id.</param>
    /// <param name="flags">UID → new flags.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the flags are stored.</returns>
    Task UpdateFlagsAsync(long folderId, IReadOnlyDictionary<uint, MessageFlags> flags, CancellationToken ct = default);
}
