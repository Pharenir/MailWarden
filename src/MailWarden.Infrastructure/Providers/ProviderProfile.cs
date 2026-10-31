using MailWarden.Core.Model;

namespace MailWarden.Infrastructure.Providers;

/// <summary>
/// How the connection to a mail server is secured.
/// </summary>
public enum TransportSecurity
{
    /// <summary>TLS from the first byte (implicit TLS, e.g. IMAP on port 993).</summary>
    SslOnConnect,

    /// <summary>Plain connection upgraded with STARTTLS (e.g. SMTP submission on port 587).</summary>
    StartTls,
}

/// <summary>
/// One mail server endpoint.
/// </summary>
/// <param name="Host">Host name.</param>
/// <param name="Port">TCP port.</param>
/// <param name="Security">Transport security.</param>
public sealed record ServerEndpoint(string Host, int Port, TransportSecurity Security);

/// <summary>
/// Server endpoints of a provider.
/// </summary>
/// <param name="Provider">Provider the profile belongs to.</param>
/// <param name="Imap">IMAP endpoint.</param>
/// <param name="Smtp">SMTP submission endpoint.</param>
public sealed record ProviderProfile(MailProvider Provider, ServerEndpoint Imap, ServerEndpoint Smtp);
