using System.Globalization;

namespace MailWarden.Core.Accounts;

/// <summary>
/// OAuth access token for IMAP/SMTP (XOAUTH2). <see cref="ToString"/> never contains <see cref="Value"/>.
/// </summary>
/// <param name="Value">Bearer token (secret).</param>
/// <param name="ExpiresOn">Point in time the token expires.</param>
/// <param name="Username">Account the token was issued for (login name / email address).</param>
public sealed record AccessToken(string Value, DateTimeOffset ExpiresOn, string Username)
{
    /// <summary>Describes the token without its value.</summary>
    /// <returns>Username and expiry only.</returns>
    public override string ToString()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"AccessToken {{ Username = {Username}, ExpiresOn = {ExpiresOn:O} }}");
    }
}
