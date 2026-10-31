using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailWarden.Core.Accounts;
using MailWarden.Core.Model;

namespace MailWarden.Infrastructure.Providers;

/// <summary>
/// One server folder seen by the connection probe.
/// </summary>
/// <param name="FullName">Full folder name as reported by the server.</param>
/// <param name="Flags">Server flags.</param>
/// <param name="Classification">Role and visibility from <see cref="FolderRoleResolver"/>.</param>
public sealed record FolderProbe(string FullName, ServerFolderFlags Flags, FolderClassification Classification);

/// <summary>
/// Result of a connection probe.
/// </summary>
/// <param name="ImapOk">IMAP connect + XOAUTH2 login + folder listing succeeded.</param>
/// <param name="SmtpOk">SMTP connect + XOAUTH2 login succeeded (no mail is sent).</param>
/// <param name="Folders">All IMAP folders, ordered by full name; empty when IMAP failed.</param>
/// <param name="Error">One English sentence describing the failures, or null when both checks succeeded.</param>
public sealed record ConnectionProbeResult(
    bool ImapOk,
    bool SmtpOk,
    IReadOnlyList<FolderProbe> Folders,
    string? Error);

/// <summary>
/// Checks an account against its provider's servers: XOAUTH2 login to IMAP (listing all folders with their flags and
/// classification) and to SMTP (authentication only, nothing is sent). Network and authentication errors are
/// returned in <see cref="ConnectionProbeResult.Error"/>, never thrown; cancellation is thrown. Error texts are built
/// here and never contain the token or server responses.
/// </summary>
public sealed class ConnectionProbe
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Runs the IMAP and the SMTP check (both always, independently).</summary>
    /// <param name="account">Account to check; its provider selects the servers.</param>
    /// <param name="token">Access token for the account (from <see cref="AccountService.GetAccessTokenAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Outcome of both checks.</returns>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="ArgumentException">The token was issued for a different user than the account address.</exception>
    /// <exception cref="NotSupportedException">The account's provider has no built-in profile.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public async Task<ConnectionProbeResult> RunAsync(Account account, AccessToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(token);
        if (!string.Equals(token.Username, account.EmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The access token belongs to a different user than the account.", nameof(token));
        }

        ProviderProfile profile = ProviderProfiles.For(account.Provider);

        // IMAP: login and folder list.
        IReadOnlyList<FolderProbe> folders = [];
        string? imapError = null;
        try
        {
            folders = await ListFoldersAsync(account, token, profile.Imap, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsCancellation(exception, ct))
        {
            imapError = Describe("IMAP", profile.Imap, exception);
        }

        // SMTP: login only.
        string? smtpError = null;
        try
        {
            await CheckSmtpLoginAsync(account, token, profile.Smtp, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsCancellation(exception, ct))
        {
            smtpError = Describe("SMTP", profile.Smtp, exception);
        }

        string? error = (imapError, smtpError) switch
        {
            (null, null) => null,
            (not null, null) => imapError + ".",
            (null, not null) => smtpError + ".",
            _ => $"{imapError}; {smtpError}.",
        };
        return new ConnectionProbeResult(imapError is null, smtpError is null, folders, error);
    }

    private static async Task<IReadOnlyList<FolderProbe>> ListFoldersAsync(
        Account account,
        AccessToken token,
        ServerEndpoint endpoint,
        CancellationToken ct)
    {
        using var client = new ImapClient { Timeout = (int)OperationTimeout.TotalMilliseconds };
        await client.ConnectAsync(endpoint.Host, endpoint.Port, ToSocketOptions(endpoint.Security), ct)
            .ConfigureAwait(false);
        await client.AuthenticateAsync(new SaslMechanismOAuth2(account.EmailAddress, token.Value), ct)
            .ConfigureAwait(false);

        // Flags come from the LIST response; Outlook.com sends them without advertising SPECIAL-USE (AP-00).
        var folders = new List<FolderProbe>();
        foreach (FolderNamespace folderNamespace in client.PersonalNamespaces)
        {
            IList<IMailFolder> serverFolders = await client
                .GetFoldersAsync(folderNamespace, StatusItems.None, subscribedOnly: false, ct)
                .ConfigureAwait(false);
            foreach (IMailFolder serverFolder in serverFolders)
            {
                ServerFolderFlags flags = MailKitFolderFlags.From(serverFolder.Attributes);
                FolderClassification classification = FolderRoleResolver.Resolve(
                    account.Provider,
                    serverFolder.FullName,
                    serverFolder.DirectorySeparator,
                    flags);
                folders.Add(new FolderProbe(serverFolder.FullName, flags, classification));
            }
        }

        await DisconnectIgnoringFailuresAsync(disconnectCt => client.DisconnectAsync(quit: true, disconnectCt), ct)
            .ConfigureAwait(false);
        folders.Sort((left, right) => string.CompareOrdinal(left.FullName, right.FullName));
        return folders;
    }

    private static async Task CheckSmtpLoginAsync(
        Account account,
        AccessToken token,
        ServerEndpoint endpoint,
        CancellationToken ct)
    {
        using var client = new SmtpClient { Timeout = (int)OperationTimeout.TotalMilliseconds };
        await client.ConnectAsync(endpoint.Host, endpoint.Port, ToSocketOptions(endpoint.Security), ct)
            .ConfigureAwait(false);
        await client.AuthenticateAsync(new SaslMechanismOAuth2(account.EmailAddress, token.Value), ct)
            .ConfigureAwait(false);
        await DisconnectIgnoringFailuresAsync(disconnectCt => client.DisconnectAsync(quit: true, disconnectCt), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the final disconnect after a successful check; its failure (connection dropped, QUIT rejected) does not
    /// change the outcome. Cancellation by the caller is still thrown.
    /// </summary>
    /// <param name="disconnect">The disconnect call.</param>
    /// <param name="ct">Caller's cancellation token.</param>
    /// <returns>A task that completes when the disconnect finished or failed.</returns>
    internal static async Task DisconnectIgnoringFailuresAsync(Func<CancellationToken, Task> disconnect, CancellationToken ct)
    {
        try
        {
            await disconnect(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsCancellation(exception, ct))
        {
            // Ignored on purpose: login (and folder listing) already succeeded; the client is disposed by the caller.
        }
    }

    /// <summary>Maps the profile's transport security to MailKit socket options (shared with the sync transport).</summary>
    /// <param name="security">Transport security of an endpoint.</param>
    /// <returns>The MailKit socket options.</returns>
    internal static SecureSocketOptions ToSocketOptions(TransportSecurity security)
    {
        return security switch
        {
            TransportSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            TransportSecurity.StartTls => SecureSocketOptions.StartTls,
            _ => throw new ArgumentOutOfRangeException(nameof(security), security, "Unknown transport security."),
        };
    }

    private static bool IsCancellation(Exception exception, CancellationToken ct)
    {
        return exception is OperationCanceledException && ct.IsCancellationRequested;
    }

    // Own wording only: server responses and library messages are not passed through.
    private static string Describe(string protocol, ServerEndpoint endpoint, Exception exception)
    {
        string target = $"{endpoint.Host}:{endpoint.Port}";
        return exception switch
        {
            AuthenticationException => $"{protocol} login at {target} was rejected (access token not accepted)",
            SslHandshakeException => $"{protocol} TLS handshake with {target} failed",
            SocketException socketException =>
                $"{protocol} connection to {target} failed (socket error {socketException.SocketErrorCode})",
            TimeoutException or OperationCanceledException => $"{protocol} connection to {target} timed out",
            ImapProtocolException or SmtpProtocolException or ImapCommandException or SmtpCommandException =>
                $"{protocol} server {target} answered with a protocol error",
            IOException => $"{protocol} connection to {target} was interrupted",
            _ => $"{protocol} check against {target} failed ({exception.GetType().Name})",
        };
    }
}
