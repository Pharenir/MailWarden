using MailWarden.Core.Accounts;

namespace MailWarden.Infrastructure.Auth;

/// <summary>
/// Maps raw error codes of MSAL and the Google OAuth endpoints to <see cref="AuthFailureReason"/>. Pure functions.
/// </summary>
public static class AuthErrorMapping
{
    // MSAL error codes (MsalError constants / OAuth error values returned by the Microsoft identity platform).
    private static readonly Dictionary<string, AuthFailureReason> MsalReasons = new(StringComparer.Ordinal)
    {
        ["authentication_canceled"] = AuthFailureReason.UserCancelled,
        ["access_denied"] = AuthFailureReason.AccessDenied,
        ["invalid_grant"] = AuthFailureReason.InteractionRequired,
        ["interaction_required"] = AuthFailureReason.InteractionRequired,
        ["user_null"] = AuthFailureReason.InteractionRequired,
        ["no_tokens_found"] = AuthFailureReason.InteractionRequired,
        ["no_account_for_login_hint"] = AuthFailureReason.InteractionRequired,
        ["invalid_client"] = AuthFailureReason.Configuration,
        ["unauthorized_client"] = AuthFailureReason.Configuration,
        ["invalid_request"] = AuthFailureReason.Configuration,
        ["request_timeout"] = AuthFailureReason.Network,
        ["service_not_available"] = AuthFailureReason.Network,
    };

    // OAuth "error" values of Google's authorization and token endpoints.
    private static readonly Dictionary<string, AuthFailureReason> GoogleReasons = new(StringComparer.Ordinal)
    {
        ["access_denied"] = AuthFailureReason.AccessDenied,
        ["invalid_grant"] = AuthFailureReason.InteractionRequired,
        ["invalid_client"] = AuthFailureReason.Configuration,
        ["unauthorized_client"] = AuthFailureReason.Configuration,
        ["redirect_uri_mismatch"] = AuthFailureReason.Configuration,
        ["user_cancelled"] = AuthFailureReason.UserCancelled,
        ["canceled"] = AuthFailureReason.UserCancelled,
    };

    /// <summary>
    /// Maps an MSAL error code: "authentication_canceled" → UserCancelled; "access_denied" → AccessDenied;
    /// "invalid_grant", "interaction_required", "user_null", "no_tokens_found", "no_account_for_login_hint" →
    /// InteractionRequired; "invalid_client", "unauthorized_client", "invalid_request" → Configuration;
    /// "request_timeout", "service_not_available" → Network; anything else → Unknown. (An MSAL service exception
    /// caused by an <see cref="HttpRequestException"/> is mapped to Network by the token provider itself.)
    /// </summary>
    /// <param name="errorCode">MSAL error code, or null.</param>
    /// <returns>The failure reason.</returns>
    public static AuthFailureReason FromMsalErrorCode(string? errorCode)
    {
        return Lookup(MsalReasons, errorCode);
    }

    /// <summary>
    /// Maps a Google OAuth error value: "access_denied" → AccessDenied (also the 403 of unpublished apps);
    /// "invalid_grant" → InteractionRequired; "invalid_client", "unauthorized_client", "redirect_uri_mismatch" →
    /// Configuration; "user_cancelled", "canceled" → UserCancelled; anything else → Unknown.
    /// </summary>
    /// <param name="error">Google OAuth error value, or null.</param>
    /// <returns>The failure reason.</returns>
    public static AuthFailureReason FromGoogleError(string? error)
    {
        return Lookup(GoogleReasons, error);
    }

    private static AuthFailureReason Lookup(Dictionary<string, AuthFailureReason> reasons, string? code)
    {
        return code is not null && reasons.TryGetValue(code, out AuthFailureReason reason)
            ? reason
            : AuthFailureReason.Unknown;
    }
}
