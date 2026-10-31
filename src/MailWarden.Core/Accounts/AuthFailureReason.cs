namespace MailWarden.Core.Accounts;

/// <summary>
/// Provider-neutral reason of a failed sign-in or token request.
/// </summary>
public enum AuthFailureReason
{
    /// <summary>The user cancelled the sign-in.</summary>
    UserCancelled,

    /// <summary>The user or the provider denied access (consent refused, app not published, wrong account).</summary>
    AccessDenied,

    /// <summary>No usable cached grant; an interactive sign-in is required.</summary>
    InteractionRequired,

    /// <summary>The client configuration (client id/secret, app registration) is missing or invalid.</summary>
    Configuration,

    /// <summary>The sign-in service could not be reached.</summary>
    Network,

    /// <summary>Any other failure.</summary>
    Unknown,
}
