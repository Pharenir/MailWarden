using MailWarden.Core.Accounts;
using MailWarden.Infrastructure.Auth;

namespace MailWarden.Infrastructure.Tests.Accounts;

[Trait("Category", "feature:accounts")]
public sealed class AuthErrorMappingTests
{
    // Case 17
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("authentication_canceled", AuthFailureReason.UserCancelled)]
    [InlineData("access_denied", AuthFailureReason.AccessDenied)]
    [InlineData("invalid_grant", AuthFailureReason.InteractionRequired)]
    [InlineData("interaction_required", AuthFailureReason.InteractionRequired)]
    [InlineData("user_null", AuthFailureReason.InteractionRequired)]
    [InlineData("no_tokens_found", AuthFailureReason.InteractionRequired)]
    [InlineData("no_account_for_login_hint", AuthFailureReason.InteractionRequired)]
    [InlineData("invalid_client", AuthFailureReason.Configuration)]
    [InlineData("unauthorized_client", AuthFailureReason.Configuration)]
    [InlineData("invalid_request", AuthFailureReason.Configuration)]
    [InlineData("request_timeout", AuthFailureReason.Network)]
    [InlineData("service_not_available", AuthFailureReason.Network)]
    [InlineData("something_else", AuthFailureReason.Unknown)]
    [InlineData("", AuthFailureReason.Unknown)]
    [InlineData(null, AuthFailureReason.Unknown)]
    public void Msal_codes_map_to_reasons(string? code, AuthFailureReason expected)
    {
        Assert.Equal(expected, AuthErrorMapping.FromMsalErrorCode(code));
    }

    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("access_denied", AuthFailureReason.AccessDenied)]
    [InlineData("invalid_grant", AuthFailureReason.InteractionRequired)]
    [InlineData("invalid_client", AuthFailureReason.Configuration)]
    [InlineData("unauthorized_client", AuthFailureReason.Configuration)]
    [InlineData("redirect_uri_mismatch", AuthFailureReason.Configuration)]
    [InlineData("user_cancelled", AuthFailureReason.UserCancelled)]
    [InlineData("canceled", AuthFailureReason.UserCancelled)]
    [InlineData("something_else", AuthFailureReason.Unknown)]
    [InlineData(null, AuthFailureReason.Unknown)]
    public void Google_errors_map_to_reasons(string? error, AuthFailureReason expected)
    {
        Assert.Equal(expected, AuthErrorMapping.FromGoogleError(error));
    }
}
