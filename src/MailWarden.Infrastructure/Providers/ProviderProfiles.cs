using MailWarden.Core.Model;

namespace MailWarden.Infrastructure.Providers;

/// <summary>
/// Built-in provider profiles (servers verified in AP-00) and provider detection from an email address.
/// </summary>
public static class ProviderProfiles
{
    // Domains whose first label identifies Microsoft regardless of the country suffix (hotmail.de, outlook.co.uk).
    private static readonly string[] MicrosoftDomainLabels = ["hotmail", "outlook", "live"];
    private static readonly string[] MicrosoftExactDomains = ["msn.com", "windowslive.com"];
    private static readonly string[] GoogleExactDomains = ["gmail.com", "googlemail.com"];

    /// <summary>Outlook.com / Hotmail: IMAP <c>outlook.office365.com:993</c> (TLS), SMTP <c>smtp-mail.outlook.com:587</c> (STARTTLS).</summary>
    public static ProviderProfile Microsoft { get; } = new(
        MailProvider.Microsoft,
        new ServerEndpoint("outlook.office365.com", 993, TransportSecurity.SslOnConnect),
        new ServerEndpoint("smtp-mail.outlook.com", 587, TransportSecurity.StartTls));

    /// <summary>Gmail: IMAP <c>imap.gmail.com:993</c> (TLS), SMTP <c>smtp.gmail.com:587</c> (STARTTLS).</summary>
    public static ProviderProfile Google { get; } = new(
        MailProvider.Google,
        new ServerEndpoint("imap.gmail.com", 993, TransportSecurity.SslOnConnect),
        new ServerEndpoint("smtp.gmail.com", 587, TransportSecurity.StartTls));

    /// <summary>Returns the profile of a provider.</summary>
    /// <param name="provider">Provider.</param>
    /// <returns>The built-in profile.</returns>
    /// <exception cref="NotSupportedException"><see cref="MailProvider.GenericImap"/> has no built-in profile.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined provider.</exception>
    public static ProviderProfile For(MailProvider provider)
    {
        return provider switch
        {
            MailProvider.Microsoft => Microsoft,
            MailProvider.Google => Google,
            MailProvider.GenericImap => throw new NotSupportedException(
                "Generic IMAP accounts have no built-in provider profile."),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown mail provider."),
        };
    }

    /// <summary>
    /// Detects the provider from the domain (case-insensitive): <c>hotmail.*</c>, <c>outlook.*</c>, <c>live.*</c>,
    /// <c>msn.com</c>, <c>windowslive.com</c> → Microsoft; <c>gmail.com</c>, <c>googlemail.com</c> → Google.
    /// </summary>
    /// <param name="emailAddress">Email address (<c>local@domain</c>).</param>
    /// <returns>The provider, or null for any other domain.</returns>
    /// <exception cref="ArgumentNullException">The address is null.</exception>
    /// <exception cref="ArgumentException">The address has no local part, no domain or contains whitespace.</exception>
    public static MailProvider? DetectFromAddress(string emailAddress)
    {
        ArgumentNullException.ThrowIfNull(emailAddress);
        string domain = ExtractDomain(emailAddress);

        if (MicrosoftExactDomains.Contains(domain, StringComparer.OrdinalIgnoreCase)
            || MicrosoftDomainLabels.Any(label => HasFirstLabel(domain, label)))
        {
            return MailProvider.Microsoft;
        }

        if (GoogleExactDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            return MailProvider.Google;
        }

        return null;
    }

    private static string ExtractDomain(string emailAddress)
    {
        // The domain follows the last '@' (a quoted local part may contain '@').
        int separatorIndex = emailAddress.LastIndexOf('@');
        bool isValid = separatorIndex > 0
            && separatorIndex < emailAddress.Length - 1
            && !emailAddress.Any(char.IsWhiteSpace);
        if (!isValid)
        {
            throw new ArgumentException($"Not a valid email address: '{emailAddress}'.", nameof(emailAddress));
        }

        return emailAddress[(separatorIndex + 1)..];
    }

    // "hotmail.de" has first label "hotmail" followed by at least one more label.
    private static bool HasFirstLabel(string domain, string label)
    {
        return domain.Length > label.Length + 1
            && domain.StartsWith(label + ".", StringComparison.OrdinalIgnoreCase);
    }
}
