using MailWarden.Core.Accounts;
using MailWarden.Core.Model;

namespace MailWarden.Infrastructure.Auth;

/// <summary>
/// Builds <see cref="AuthenticationFailedException"/>s with one fixed English sentence per provider and reason, so
/// that no provider message (which may echo request details) ever reaches the user-facing text.
/// </summary>
internal static class AuthFailures
{
    /// <summary>Creates the exception with the standard message for the reason.</summary>
    /// <param name="provider">Provider whose sign-in failed.</param>
    /// <param name="reason">Failure reason.</param>
    /// <param name="providerErrorCode">Raw provider error code, or null.</param>
    /// <param name="inner">Underlying exception, or null.</param>
    /// <returns>The exception to throw.</returns>
    public static AuthenticationFailedException Create(
        MailProvider provider,
        AuthFailureReason reason,
        string? providerErrorCode = null,
        Exception? inner = null)
    {
        return new AuthenticationFailedException(
            provider,
            reason,
            MessageFor(provider, reason),
            providerErrorCode,
            inner);
    }

    private static string MessageFor(MailProvider provider, AuthFailureReason reason)
    {
        string providerName = provider switch
        {
            MailProvider.Microsoft => "Microsoft",
            MailProvider.Google => "Google",
            _ => "The",
        };

        return reason switch
        {
            AuthFailureReason.UserCancelled => $"{providerName} sign-in was cancelled.",
            AuthFailureReason.AccessDenied =>
                $"{providerName} sign-in was denied, so MailWarden has no access to the mailbox.",
            AuthFailureReason.InteractionRequired =>
                $"{providerName} sign-in is required again because no valid stored sign-in exists for this account.",
            AuthFailureReason.Configuration =>
                $"{providerName} sign-in is not configured correctly (client id, client secret or app registration).",
            AuthFailureReason.Network => $"{providerName} sign-in failed because the sign-in service could not be reached.",
            _ => $"{providerName} sign-in failed for an unexpected reason.",
        };
    }
}
