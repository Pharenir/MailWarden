using MailWarden.Core.Model;

namespace MailWarden.Core.Accounts;

/// <summary>
/// Obtains OAuth access tokens for one provider. All failures surface as <see cref="AuthenticationFailedException"/>;
/// cancellation surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public interface ITokenProvider
{
    /// <summary>Provider this token provider serves.</summary>
    MailProvider Provider { get; }

    /// <summary>Returns a token from the cached grant without user interaction (refreshing it when needed).</summary>
    /// <param name="username">Account address the grant was obtained for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A valid access token.</returns>
    /// <exception cref="AuthenticationFailedException">
    /// No cached grant (<see cref="AuthFailureReason.InteractionRequired"/>) or any other token failure.
    /// </exception>
    Task<AccessToken> GetAccessTokenSilentAsync(string username, CancellationToken ct = default);

    /// <summary>Opens the system browser for an interactive sign-in and caches the new grant.</summary>
    /// <param name="loginHint">Address pre-filled in the sign-in page, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A valid access token; its <see cref="AccessToken.Username"/> is the account actually signed in (not the hint).
    /// </returns>
    /// <exception cref="AuthenticationFailedException">Sign-in cancelled, denied or failed.</exception>
    Task<AccessToken> SignInInteractiveAsync(string? loginHint, CancellationToken ct = default);

    /// <summary>Removes every cached grant of this user. An unknown user is a no-op.</summary>
    /// <param name="username">Account address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the grants are removed.</returns>
    Task SignOutAsync(string username, CancellationToken ct = default);
}
