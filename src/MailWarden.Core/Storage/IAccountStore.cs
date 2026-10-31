using MailWarden.Core.Model;

namespace MailWarden.Core.Storage;

/// <summary>
/// Persists accounts.
/// </summary>
public interface IAccountStore
{
    /// <summary>Lists all accounts.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All accounts ordered by <see cref="Account.DisplayName"/>.</returns>
    Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default);

    /// <summary>Loads one account.</summary>
    /// <param name="id">Account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The account, or null when unknown.</returns>
    Task<Account?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Inserts the account or updates the existing one with the same <see cref="Account.Id"/>.</summary>
    /// <param name="account">Account to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the account is stored.</returns>
    /// <exception cref="StorageException">The email address is already used by another account.</exception>
    Task UpsertAsync(Account account, CancellationToken ct = default);

    /// <summary>Deletes the account including its folders and messages. Unknown id = no-op.</summary>
    /// <param name="id">Account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the account is deleted.</returns>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
