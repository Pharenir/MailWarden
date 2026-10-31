using MailWarden.Core.Model;

namespace MailWarden.Core.Accounts;

/// <summary>
/// Every sign-in or token failure surfaces as this type. The message is one English sentence for the user and never
/// contains a token, a secret or a full exception dump.
/// </summary>
public class AuthenticationFailedException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="provider">Provider whose sign-in failed.</param>
    /// <param name="reason">Provider-neutral failure reason.</param>
    /// <param name="message">One English sentence for the user.</param>
    /// <param name="providerErrorCode">Raw error code from MSAL/Google (e.g. "access_denied"), or null.</param>
    /// <param name="inner">Underlying exception, or null.</param>
    public AuthenticationFailedException(
        MailProvider provider,
        AuthFailureReason reason,
        string message,
        string? providerErrorCode = null,
        Exception? inner = null)
        : base(message, inner)
    {
        Provider = provider;
        Reason = reason;
        ProviderErrorCode = providerErrorCode;
    }

    /// <summary>Provider whose sign-in failed.</summary>
    public MailProvider Provider { get; }

    /// <summary>Provider-neutral failure reason.</summary>
    public AuthFailureReason Reason { get; }

    /// <summary>Raw error code from MSAL/Google (e.g. "access_denied"), or null.</summary>
    public string? ProviderErrorCode { get; }
}
