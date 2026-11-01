using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// What the user interface reads. It has no write member on purpose: changes go through <see cref="IMailboxActions"/>.
/// Access to the one database connection behind it is serialised, and no call blocks the caller's thread for the query.
/// </summary>
public interface IMailboxReadModel
{
    /// <summary>Lists all accounts.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The accounts ordered by display name.</returns>
    Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct = default);

    /// <summary>Lists the folders of one account.</summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The folders ordered by full name.</returns>
    Task<IReadOnlyList<MailFolder>> ListFoldersAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Reads the role of a folder.</summary>
    /// <param name="folderId">Folder id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The role; null for an unknown folder.</returns>
    Task<FolderRole?> GetFolderRoleAsync(long folderId, CancellationToken ct = default);

    /// <summary>Lists messages for a view.</summary>
    /// <param name="query">Filter and paging.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Messages newest first.</returns>
    Task<IReadOnlyList<MailMessage>> ListMessagesAsync(MessageQuery query, CancellationToken ct = default);

    /// <summary>Counts the messages without <see cref="MessageFlags.Seen"/>.</summary>
    /// <param name="query">Filter; its paging values are ignored.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of unread messages.</returns>
    Task<int> CountUnreadAsync(MessageQuery query, CancellationToken ct = default);

    /// <summary>
    /// Local full-text search over subject, sender and indexed body text; every word must match, search syntax characters
    /// are treated as text.
    /// </summary>
    /// <param name="text">Search words.</param>
    /// <param name="limit">Maximum number of results, greater than 0.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching messages newest first.</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> holds nothing but whitespace and control characters.</exception>
    Task<IReadOnlyList<MailMessage>> SearchAsync(string text, int limit, CancellationToken ct = default);

    /// <summary>
    /// Reads the content of a message. The EML is read completely into memory, so no reader stays open while the sync
    /// overwrites the file.
    /// </summary>
    /// <param name="message">Message to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The content; null when the message has no EML (EmlPath null or file missing).</returns>
    /// <exception cref="MailContentException">The EML cannot be parsed.</exception>
    Task<MailContent?> ReadContentAsync(MailMessage message, CancellationToken ct = default);
}
