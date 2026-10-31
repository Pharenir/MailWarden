using System.Security.Cryptography;
using System.Text;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Json;
using MailWarden.Core.Accounts;
using MailWarden.Core.Model;

namespace MailWarden.Infrastructure.Auth;

/// <summary>
/// <see cref="ITokenProvider"/> for Google accounts: installed-app flow with PKCE, loopback redirect and the system
/// browser; scopes <c>https://mail.google.com/</c>, <c>openid</c>, <c>email</c>. The signed-in address is taken
/// from the validated ID token (signature, issuer, audience = client id, expiry, verified email), never from the
/// hint. The token response (incl. refresh token, without ID token) is stored in the credential store under
/// <c>google.&lt;lower-case address&gt;</c> (characters outside the key alphabet replaced by '_').
/// </summary>
public sealed class GoogleTokenProvider : ITokenProvider
{
    private const string CredentialKeyPrefix = "google.";
    private const string ConsentPrompt = "consent select_account";
    private const string PendingUserId = "pending-sign-in";
    private const string StoredGrantUnreadableCode = "stored_grant_unreadable";
    private const string InvalidIdTokenCode = "invalid_id_token";
    private const string EmailNotVerifiedCode = "email_not_verified";
    private const string MissingCodeError = "missing_authorization_code";

    private static readonly string[] Scopes = ["https://mail.google.com/", "openid", "email"];

    private readonly AuthConfiguration configuration;
    private readonly ICredentialStore credentialStore;

    /// <summary>Creates the provider. Configuration is checked on first use.</summary>
    /// <param name="configuration">Client configuration (Google client id and secret).</param>
    /// <param name="credentialStore">Store for the token responses.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public GoogleTokenProvider(AuthConfiguration configuration, ICredentialStore credentialStore)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credentialStore);
        this.configuration = configuration;
        this.credentialStore = credentialStore;
    }

    /// <inheritdoc />
    public MailProvider Provider => MailProvider.Google;

    /// <inheritdoc />
    public async Task<AccessToken> GetAccessTokenSilentAsync(string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        string emailAddress = username.Trim();
        string credentialKey = CredentialKeyFor(emailAddress);

        TokenResponse? storedToken = await ReadStoredTokenAsync(credentialKey, ct).ConfigureAwait(false);
        if (storedToken is null)
        {
            throw AuthFailures.Create(Provider, AuthFailureReason.InteractionRequired);
        }

        // Cached access token still good (not within the library's refresh window) → no network.
        if (!string.IsNullOrEmpty(storedToken.AccessToken) && !storedToken.IsStale)
        {
            return ToAccessToken(storedToken, emailAddress);
        }

        if (string.IsNullOrEmpty(storedToken.RefreshToken))
        {
            throw AuthFailures.Create(Provider, AuthFailureReason.InteractionRequired);
        }

        // Refresh with the stored refresh token; Google usually omits a new refresh token, so keep the old one.
        TokenResponse refreshedToken = await TranslateFailuresAsync(
            async () =>
            {
                using GoogleAuthorizationCodeFlow flow = CreateFlow(loginHint: null);
                return await flow
                    .RefreshTokenAsync(emailAddress, storedToken.RefreshToken, ct)
                    .ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        refreshedToken.RefreshToken ??= storedToken.RefreshToken;

        await WriteStoredTokenAsync(credentialKey, refreshedToken, ct).ConfigureAwait(false);
        return ToAccessToken(refreshedToken, emailAddress);
    }

    /// <inheritdoc />
    public async Task<AccessToken> SignInInteractiveAsync(string? loginHint, CancellationToken ct = default)
    {
        (string clientId, _) = configuration.RequireGoogleClient();
        return await TranslateFailuresAsync(
            async () =>
            {
                using PkceGoogleAuthorizationCodeFlow flow = CreateFlow(
                    string.IsNullOrWhiteSpace(loginHint) ? null : loginHint.Trim());

                // Browser → loopback listener → authorization code (+ PKCE verifier) → token response.
                var codeReceiver = new LocalServerCodeReceiver();
                string redirectUri = codeReceiver.RedirectUri;
                AuthorizationCodeRequestUrl authorizationRequest =
                    flow.CreateAuthorizationCodeRequest(redirectUri, out string codeVerifier);
                AuthorizationCodeResponseUrl authorizationResponse = await codeReceiver
                    .ReceiveCodeAsync(authorizationRequest, ct)
                    .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(authorizationResponse.Error))
                {
                    throw AuthFailures.Create(
                        Provider,
                        AuthErrorMapping.FromGoogleError(authorizationResponse.Error),
                        authorizationResponse.Error);
                }

                if (string.IsNullOrEmpty(authorizationResponse.Code))
                {
                    throw AuthFailures.Create(Provider, AuthFailureReason.Unknown, MissingCodeError);
                }

                TokenResponse token = await flow
                    .ExchangeCodeForTokenAsync(PendingUserId, authorizationResponse.Code, codeVerifier, redirectUri, ct)
                    .ConfigureAwait(false);

                // The account that actually signed in comes from the validated ID token.
                string emailAddress = await ValidateSignedInAddressAsync(token.IdToken, clientId).ConfigureAwait(false);
                string credentialKey = CredentialKeyFor(emailAddress);

                // Re-consent normally returns a refresh token (prompt=consent); keep the old one if it did not.
                if (string.IsNullOrEmpty(token.RefreshToken))
                {
                    TokenResponse? previousToken = await TryReadStoredTokenAsync(credentialKey, ct).ConfigureAwait(false);
                    token.RefreshToken = previousToken?.RefreshToken;
                }

                await WriteStoredTokenAsync(credentialKey, token, ct).ConfigureAwait(false);
                return ToAccessToken(token, emailAddress);
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SignOutAsync(string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        await credentialStore.DeleteAsync(CredentialKeyFor(username.Trim()), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Credential store key for an address: <c>google.</c> + lower-case address, characters outside
    /// <c>[A-Za-z0-9._-]</c> replaced by '_'.
    /// </summary>
    /// <param name="emailAddress">Account address.</param>
    /// <returns>The credential store key.</returns>
    internal static string CredentialKeyFor(string emailAddress)
    {
        var key = new StringBuilder(CredentialKeyPrefix, CredentialKeyPrefix.Length + emailAddress.Length);
        foreach (char character in emailAddress.ToLowerInvariant())
        {
            bool allowed = char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';
            key.Append(allowed ? character : '_');
        }

        return key.ToString();
    }

    private PkceGoogleAuthorizationCodeFlow CreateFlow(string? loginHint)
    {
        (string clientId, string clientSecret) = configuration.RequireGoogleClient();

        // No DataStore: persistence is done explicitly through the credential store.
        return new PkceGoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = Scopes,
            Prompt = ConsentPrompt,
            LoginHint = loginHint,
        });
    }

    private async Task<string> ValidateSignedInAddressAsync(string? idToken, string clientId)
    {
        if (string.IsNullOrEmpty(idToken))
        {
            throw AuthFailures.Create(Provider, AuthFailureReason.Unknown, InvalidIdTokenCode);
        }

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature
                .ValidateAsync(idToken, new GoogleJsonWebSignature.ValidationSettings { Audience = [clientId] })
                .ConfigureAwait(false);
        }
        catch (InvalidJwtException exception)
        {
            throw AuthFailures.Create(Provider, AuthFailureReason.Unknown, InvalidIdTokenCode, exception);
        }

        if (string.IsNullOrWhiteSpace(payload.Email) || !payload.EmailVerified)
        {
            throw AuthFailures.Create(Provider, AuthFailureReason.Unknown, EmailNotVerifiedCode);
        }

        return payload.Email.Trim();
    }

    // Stored grant: decrypt + deserialize; unreadable data means the user has to sign in again.
    private async Task<TokenResponse?> ReadStoredTokenAsync(string credentialKey, CancellationToken ct)
    {
        byte[]? serializedToken;
        try
        {
            serializedToken = await credentialStore.ReadAsync(credentialKey, ct).ConfigureAwait(false);
        }
        catch (CredentialStoreException exception)
        {
            throw AuthFailures.Create(
                Provider,
                AuthFailureReason.InteractionRequired,
                StoredGrantUnreadableCode,
                exception);
        }

        if (serializedToken is null)
        {
            return null;
        }

        try
        {
            return NewtonsoftJsonSerializer.Instance.Deserialize<TokenResponse>(Encoding.UTF8.GetString(serializedToken))
                ?? throw AuthFailures.Create(Provider, AuthFailureReason.InteractionRequired, StoredGrantUnreadableCode);
        }
        catch (Exception exception) when (exception is not AuthenticationFailedException)
        {
            // No inner exception: JSON parser messages may quote parts of the token.
            throw AuthFailures.Create(Provider, AuthFailureReason.InteractionRequired, StoredGrantUnreadableCode);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serializedToken);
        }
    }

    private async Task<TokenResponse?> TryReadStoredTokenAsync(string credentialKey, CancellationToken ct)
    {
        try
        {
            return await ReadStoredTokenAsync(credentialKey, ct).ConfigureAwait(false);
        }
        catch (AuthenticationFailedException)
        {
            // An unreadable previous grant is replaced by the new one.
            return null;
        }
    }

    private async Task WriteStoredTokenAsync(string credentialKey, TokenResponse token, CancellationToken ct)
    {
        // The ID token was only needed to identify the user; it is not kept.
        token.IdToken = null;
        byte[] serializedToken = Encoding.UTF8.GetBytes(NewtonsoftJsonSerializer.Instance.Serialize(token));
        try
        {
            await credentialStore.WriteAsync(credentialKey, serializedToken, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serializedToken);
        }
    }

    private AccessToken ToAccessToken(TokenResponse token, string emailAddress)
    {
        if (string.IsNullOrEmpty(token.AccessToken))
        {
            throw AuthFailures.Create(Provider, AuthFailureReason.Unknown);
        }

        DateTime issuedUtc = DateTime.SpecifyKind(token.IssuedUtc, DateTimeKind.Utc);
        DateTimeOffset expiresOn = new DateTimeOffset(issuedUtc).AddSeconds(token.ExpiresInSeconds ?? 0);
        return new AccessToken(token.AccessToken, expiresOn, emailAddress);
    }

    // Translates Google and transport failures into AuthenticationFailedException; cancellation stays cancellation.
    private async Task<TResult> TranslateFailuresAsync<TResult>(Func<Task<TResult>> operation, CancellationToken ct)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (TokenResponseException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (TokenResponseException exception)
        {
            string? error = exception.Error?.Error;
            throw AuthFailures.Create(Provider, AuthErrorMapping.FromGoogleError(error), error, exception);
        }
        catch (HttpRequestException exception)
        {
            throw AuthFailures.Create(Provider, AuthFailureReason.Network, inner: exception);
        }
    }
}
