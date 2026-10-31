using MailWarden.Core.Model;
using MailWarden.Core.Storage;

namespace MailWarden.Core.Accounts;

/// <summary>
/// Orchestrates the account lifecycle (add, token, re-authenticate, remove) on top of <see cref="IAccountStore"/> and
/// the token providers. Credentials never pass through this class into storage; the token providers own them.
/// </summary>
public sealed class AccountService
{
    private const string OtherAccountMessage =
        "The browser was signed in as other account; sign in with the address of this account.";

    private readonly IAccountStore accountStore;
    private readonly Dictionary<MailProvider, ITokenProvider> tokenProviders;

    /// <summary>Creates the service.</summary>
    /// <param name="accountStore">Account persistence.</param>
    /// <param name="tokenProviders">Token providers, at most one per <see cref="MailProvider"/>.</param>
    /// <exception cref="ArgumentNullException">A parameter or one of the token providers is null.</exception>
    /// <exception cref="ArgumentException">Two token providers serve the same provider.</exception>
    public AccountService(IAccountStore accountStore, IEnumerable<ITokenProvider> tokenProviders)
    {
        ArgumentNullException.ThrowIfNull(accountStore);
        ArgumentNullException.ThrowIfNull(tokenProviders);

        this.accountStore = accountStore;
        this.tokenProviders = [];
        foreach (ITokenProvider tokenProvider in tokenProviders)
        {
            ArgumentNullException.ThrowIfNull(tokenProvider, nameof(tokenProviders));
            if (!this.tokenProviders.TryAdd(tokenProvider.Provider, tokenProvider))
            {
                throw new ArgumentException(
                    $"More than one token provider for {tokenProvider.Provider}.",
                    nameof(tokenProviders));
            }
        }
    }

    /// <summary>
    /// Signs in interactively and creates the account (new id, display name and address = signed-in username). If an
    /// account with that address already exists (case-insensitive), it is returned unchanged (re-consent).
    /// </summary>
    /// <param name="provider">Provider to sign in with.</param>
    /// <param name="loginHint">Address pre-filled in the sign-in page, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new or existing account.</returns>
    /// <exception cref="NotSupportedException">
    /// <see cref="MailProvider.GenericImap"/> or no token provider registered for the provider.
    /// </exception>
    /// <exception cref="AuthenticationFailedException">The sign-in failed.</exception>
    public async Task<Account> AddAccountAsync(
        MailProvider provider,
        string? loginHint,
        CancellationToken ct = default)
    {
        ITokenProvider tokenProvider = RequireTokenProvider(provider);
        AccessToken token = await tokenProvider.SignInInteractiveAsync(loginHint, ct).ConfigureAwait(false);
        string signedInAddress = RequireUsername(token, provider);

        // Re-consent for a known address keeps the existing account (id, folders, messages).
        Account? existingAccount = await FindByAddressAsync(signedInAddress, ct).ConfigureAwait(false);
        if (existingAccount is not null)
        {
            return existingAccount;
        }

        var account = new Account(Guid.NewGuid(), signedInAddress, signedInAddress, provider);
        await accountStore.UpsertAsync(account, ct).ConfigureAwait(false);
        return account;
    }

    /// <summary>Returns a token for a stored account without user interaction.</summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A valid access token.</returns>
    /// <exception cref="ArgumentException">The account is unknown.</exception>
    /// <exception cref="NotSupportedException">No token provider registered for the account's provider.</exception>
    /// <exception cref="AuthenticationFailedException">
    /// No cached grant (<see cref="AuthFailureReason.InteractionRequired"/>) or any other token failure.
    /// </exception>
    public async Task<AccessToken> GetAccessTokenAsync(Guid accountId, CancellationToken ct = default)
    {
        Account account = await RequireAccountAsync(accountId, ct).ConfigureAwait(false);
        ITokenProvider tokenProvider = RequireTokenProvider(account.Provider);
        return await tokenProvider.GetAccessTokenSilentAsync(account.EmailAddress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs in interactively again for an existing account. The signed-in username must equal the account address
    /// (case-insensitive); otherwise the new grant is signed out again and the call fails with
    /// <see cref="AuthFailureReason.AccessDenied"/>. The account itself is never changed.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A valid access token for the account.</returns>
    /// <exception cref="ArgumentException">The account is unknown.</exception>
    /// <exception cref="NotSupportedException">No token provider registered for the account's provider.</exception>
    /// <exception cref="AuthenticationFailedException">Sign-in failed or a different account signed in.</exception>
    public async Task<AccessToken> ReauthenticateAsync(Guid accountId, CancellationToken ct = default)
    {
        Account account = await RequireAccountAsync(accountId, ct).ConfigureAwait(false);
        ITokenProvider tokenProvider = RequireTokenProvider(account.Provider);
        AccessToken token = await tokenProvider
            .SignInInteractiveAsync(account.EmailAddress, ct)
            .ConfigureAwait(false);
        string signedInAddress = RequireUsername(token, account.Provider);

        if (string.Equals(signedInAddress, account.EmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            return token;
        }

        // Discard the grant of the wrong account, unless that account is itself managed by MailWarden: its fresh
        // grant is legitimate and signing it out would force a needless re-login there.
        Account? otherStoredAccount = await FindByAddressAsync(signedInAddress, ct).ConfigureAwait(false);
        if (otherStoredAccount is null)
        {
            await tokenProvider.SignOutAsync(signedInAddress, ct).ConfigureAwait(false);
        }

        throw new AuthenticationFailedException(account.Provider, AuthFailureReason.AccessDenied, OtherAccountMessage);
    }

    /// <summary>
    /// Signs the account out at its token provider, then deletes it (with its folders and messages) from the store.
    /// An unknown id is a no-op. Accounts whose provider has no registered token provider are only deleted.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the account is removed.</returns>
    public async Task RemoveAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        Account? account = await accountStore.GetAsync(accountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return;
        }

        if (tokenProviders.TryGetValue(account.Provider, out ITokenProvider? tokenProvider))
        {
            await tokenProvider.SignOutAsync(account.EmailAddress, ct).ConfigureAwait(false);
        }

        await accountStore.DeleteAsync(account.Id, ct).ConfigureAwait(false);
    }

    private ITokenProvider RequireTokenProvider(MailProvider provider)
    {
        if (provider == MailProvider.GenericImap)
        {
            throw new NotSupportedException("Generic IMAP accounts are not supported yet.");
        }

        if (!tokenProviders.TryGetValue(provider, out ITokenProvider? tokenProvider))
        {
            throw new NotSupportedException($"No token provider is registered for {provider}.");
        }

        return tokenProvider;
    }

    private async Task<Account> RequireAccountAsync(Guid accountId, CancellationToken ct)
    {
        Account? account = await accountStore.GetAsync(accountId, ct).ConfigureAwait(false);
        return account ?? throw new ArgumentException($"Unknown account id {accountId}.", nameof(accountId));
    }

    private async Task<Account?> FindByAddressAsync(string emailAddress, CancellationToken ct)
    {
        IReadOnlyList<Account> accounts = await accountStore.ListAsync(ct).ConfigureAwait(false);
        return accounts.FirstOrDefault(
            account => string.Equals(account.EmailAddress, emailAddress, StringComparison.OrdinalIgnoreCase));
    }

    // A provider returning no username is a defect of that provider; fail before anything is stored.
    private static string RequireUsername(AccessToken token, MailProvider provider)
    {
        if (token is null || string.IsNullOrWhiteSpace(token.Username))
        {
            throw new AuthenticationFailedException(
                provider,
                AuthFailureReason.Unknown,
                "The sign-in did not report which account signed in.");
        }

        return token.Username;
    }
}
