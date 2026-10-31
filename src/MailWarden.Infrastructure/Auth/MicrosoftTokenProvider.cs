using System.Security.Cryptography;
using MailWarden.Core.Accounts;
using MailWarden.Core.Model;
using Microsoft.Identity.Client;

namespace MailWarden.Infrastructure.Auth;

/// <summary>
/// <see cref="ITokenProvider"/> for Microsoft consumer accounts (Outlook.com / Hotmail): MSAL public client with
/// authority <c>consumers</c>, redirect <c>http://localhost</c> (system browser) and the IMAP/SMTP scopes. The MSAL
/// token cache of all Microsoft accounts is serialised into the credential store under the key <c>msal.cache</c>;
/// nothing is written to disk in plain text. An interactive sign-in that does not complete within the sign-in timeout
/// (default 5 minutes) fails with <see cref="AuthFailureReason.Unknown"/> and provider error code
/// <c>sign_in_timeout</c>.
/// </summary>
/// <remarks>
/// If the stored cache cannot be decrypted or is no valid MSAL cache, silent requests fail with
/// <see cref="AuthFailureReason.InteractionRequired"/> and the next interactive sign-in replaces the unreadable cache.
/// </remarks>
public sealed class MicrosoftTokenProvider : ITokenProvider
{
    // Credential store key of the serialised MSAL token cache (all Microsoft accounts).
    private const string CacheKey = "msal.cache";
    private const string Authority = "https://login.microsoftonline.com/consumers";
    private const string RedirectUri = "http://localhost";
    private const string CacheUnreadableCode = "token_cache_unreadable";

    private static readonly string[] Scopes =
    [
        "https://outlook.office.com/IMAP.AccessAsUser.All",
        "https://outlook.office.com/SMTP.Send",
    ];

    private readonly AuthConfiguration configuration;
    private readonly ICredentialStore credentialStore;
    private readonly TimeSpan interactiveSignInTimeout;
    private readonly Lock applicationLock = new();
    private IPublicClientApplication? application;
    private volatile bool storedCacheUnreadable;

    /// <summary>Creates the provider. The MSAL client is built on first use, so missing configuration fails late.</summary>
    /// <param name="configuration">Client configuration (Microsoft client id).</param>
    /// <param name="credentialStore">Store for the serialised token cache.</param>
    /// <param name="interactiveSignInTimeout">
    /// Time limit of <see cref="SignInInteractiveAsync"/>; null = 5 minutes. When it expires the sign-in fails with
    /// <see cref="AuthFailureReason.Unknown"/> and provider error code <c>sign_in_timeout</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive or too large.</exception>
    public MicrosoftTokenProvider(
        AuthConfiguration configuration,
        ICredentialStore credentialStore,
        TimeSpan? interactiveSignInTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credentialStore);
        this.configuration = configuration;
        this.credentialStore = credentialStore;
        this.interactiveSignInTimeout = InteractiveSignInTimeout.Resolve(
            interactiveSignInTimeout,
            nameof(interactiveSignInTimeout));
    }

    /// <inheritdoc />
    public MailProvider Provider => MailProvider.Microsoft;

    /// <inheritdoc />
    public async Task<AccessToken> GetAccessTokenSilentAsync(string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        IPublicClientApplication client = GetApplication();
        return await TranslateFailuresAsync(
            async () =>
            {
                IAccount? account = await FindAccountAsync(client, username).ConfigureAwait(false);
                if (storedCacheUnreadable)
                {
                    throw AuthFailures.Create(Provider, AuthFailureReason.InteractionRequired, CacheUnreadableCode);
                }

                if (account is null)
                {
                    throw AuthFailures.Create(Provider, AuthFailureReason.InteractionRequired);
                }

                AuthenticationResult result = await client
                    .AcquireTokenSilent(Scopes, account)
                    .ExecuteAsync(ct)
                    .ConfigureAwait(false);
                return ToAccessToken(result);
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AccessToken> SignInInteractiveAsync(string? loginHint, CancellationToken ct = default)
    {
        IPublicClientApplication client = GetApplication();

        // The browser wait is bounded: MSAL's loopback listener would otherwise wait until the caller cancels.
        return await InteractiveSignInTimeout.RunAsync(
            Provider,
            interactiveSignInTimeout,
            signInCt => TranslateFailuresAsync(
                async () =>
                {
                    // Account picker every time, so a different account can be chosen deliberately.
                    AcquireTokenInteractiveParameterBuilder request = client
                        .AcquireTokenInteractive(Scopes)
                        .WithPrompt(Prompt.SelectAccount);
                    if (!string.IsNullOrWhiteSpace(loginHint))
                    {
                        request = request.WithLoginHint(loginHint.Trim());
                    }

                    AuthenticationResult result = await request.ExecuteAsync(signInCt).ConfigureAwait(false);
                    return ToAccessToken(result);
                },
                signInCt),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SignOutAsync(string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        IPublicClientApplication client = GetApplication();
        await TranslateFailuresAsync(
            async () =>
            {
                // An unreadable cache yields no accounts: its grants are unusable anyway, so this is a no-op.
                IEnumerable<IAccount> accounts = await client.GetAccountsAsync().ConfigureAwait(false);
                foreach (IAccount account in accounts.Where(account => IsSameUser(account, username)))
                {
                    ct.ThrowIfCancellationRequested();
                    await client.RemoveAsync(account).ConfigureAwait(false);
                }

                return true;
            },
            ct).ConfigureAwait(false);
    }

    // Builds the MSAL client once; configuration errors surface on every call until fixed.
    private IPublicClientApplication GetApplication()
    {
        lock (applicationLock)
        {
            if (application is not null)
            {
                return application;
            }

            string clientId = configuration.RequireMicrosoftClientId();
            IPublicClientApplication client;
            try
            {
                client = PublicClientApplicationBuilder
                    .Create(clientId)
                    .WithAuthority(Authority)
                    .WithRedirectUri(RedirectUri)
                    .Build();
            }
            catch (MsalException exception)
            {
                throw AuthFailures.Create(Provider, AuthFailureReason.Configuration, exception.ErrorCode, exception);
            }

            client.UserTokenCache.SetBeforeAccessAsync(LoadCacheAsync);
            client.UserTokenCache.SetAfterAccessAsync(SaveCacheAsync);
            application = client;
            return client;
        }
    }

    // Token cache: credential store → MSAL before every cache access.
    private async Task LoadCacheAsync(TokenCacheNotificationArgs notification)
    {
        byte[]? serializedCache;
        try
        {
            serializedCache = await credentialStore
                .ReadAsync(CacheKey, notification.CancellationToken)
                .ConfigureAwait(false);
            storedCacheUnreadable = false;
        }
        catch (CredentialStoreException)
        {
            storedCacheUnreadable = true;
            serializedCache = null;
        }

        try
        {
            notification.TokenCache.DeserializeMsalV3(serializedCache ?? [], shouldClearExistingCache: true);
        }
        catch (MsalClientException) when (serializedCache is not null)
        {
            // Decryptable but no valid MSAL cache (truncated or foreign content): treated like an undecryptable one.
            storedCacheUnreadable = true;
            notification.TokenCache.DeserializeMsalV3([], shouldClearExistingCache: true);
        }
        finally
        {
            if (serializedCache is not null)
            {
                CryptographicOperations.ZeroMemory(serializedCache);
            }
        }
    }

    // Token cache: MSAL → credential store after every change.
    private async Task SaveCacheAsync(TokenCacheNotificationArgs notification)
    {
        if (!notification.HasStateChanged)
        {
            return;
        }

        byte[] serializedCache = notification.TokenCache.SerializeMsalV3();
        try
        {
            await credentialStore
                .WriteAsync(CacheKey, serializedCache, notification.CancellationToken)
                .ConfigureAwait(false);
            storedCacheUnreadable = false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serializedCache);
        }
    }

    private static async Task<IAccount?> FindAccountAsync(IPublicClientApplication client, string username)
    {
        IEnumerable<IAccount> accounts = await client.GetAccountsAsync().ConfigureAwait(false);
        return accounts.FirstOrDefault(account => IsSameUser(account, username));
    }

    private static bool IsSameUser(IAccount account, string username)
    {
        return string.Equals(account.Username, username.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static AccessToken ToAccessToken(AuthenticationResult result)
    {
        return new AccessToken(result.AccessToken, result.ExpiresOn, result.Account.Username);
    }

    // Translates MSAL and transport failures into AuthenticationFailedException; cancellation stays cancellation.
    private async Task<TResult> TranslateFailuresAsync<TResult>(Func<Task<TResult>> operation, CancellationToken ct)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (MsalException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (MsalUiRequiredException exception)
        {
            throw AuthFailures.Create(Provider, AuthFailureReason.InteractionRequired, exception.ErrorCode, exception);
        }
        catch (MsalServiceException exception) when (exception.InnerException is HttpRequestException)
        {
            throw AuthFailures.Create(Provider, AuthFailureReason.Network, exception.ErrorCode, exception);
        }
        catch (MsalException exception)
        {
            AuthFailureReason reason = AuthErrorMapping.FromMsalErrorCode(exception.ErrorCode);
            throw AuthFailures.Create(Provider, reason, exception.ErrorCode, exception);
        }
        catch (HttpRequestException exception)
        {
            throw AuthFailures.Create(Provider, AuthFailureReason.Network, inner: exception);
        }
    }
}
