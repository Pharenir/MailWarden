using MailWarden.Core.Model;

namespace MailWarden.Core.Storage;

/// <summary>
/// Persists folders of accounts.
/// </summary>
public interface IFolderStore
{
    /// <summary>Lists the folders of one account.</summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Folders ordered by <see cref="MailFolder.FullName"/>, case-insensitive for ASCII letters only (SQLite
    /// <c>NOCASE</c>); empty for an unknown account.
    /// </returns>
    Task<IReadOnlyList<MailFolder>> ListAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Loads one folder.</summary>
    /// <param name="id">Folder id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The folder, or null when unknown.</returns>
    Task<MailFolder?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Inserts the folder or updates role and UIDVALIDITY of the existing folder with the same
    /// (<see cref="MailFolder.AccountId"/>, <see cref="MailFolder.FullName"/>). The <see cref="MailFolder.Id"/> of
    /// the argument is ignored.
    /// </summary>
    /// <param name="folder">Folder to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored copy with its database id.</returns>
    /// <exception cref="StorageException">The account does not exist.</exception>
    Task<MailFolder> UpsertAsync(MailFolder folder, CancellationToken ct = default);

    /// <summary>Deletes the folder including its messages. Unknown id = no-op.</summary>
    /// <param name="id">Folder id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the folder is deleted.</returns>
    Task DeleteAsync(long id, CancellationToken ct = default);
}
