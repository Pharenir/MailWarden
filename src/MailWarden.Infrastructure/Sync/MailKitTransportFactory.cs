using MailKit.Net.Imap;
using MailKit.Security;
using MailWarden.Core.Accounts;
using MailWarden.Core.Model;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Providers;

namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// Opens IMAP sessions with MailKit: endpoint from <see cref="ProviderProfiles"/>, XOAUTH2 with the silent token of
/// <see cref="AccountService.GetAccessTokenAsync"/>. Never opens a browser; the token never appears in exception
/// messages.
/// </summary>
/// <param name="accountService">Supplies the access token of the account.</param>
public sealed class MailKitTransportFactory(AccountService accountService) : IMailTransportFactory
{
    private readonly AccountService accountService =
        accountService ?? throw new ArgumentNullException(nameof(accountService));

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="account"/> is null.</exception>
    /// <exception cref="NotSupportedException">The account is a generic IMAP account (password login not supported).</exception>
    /// <exception cref="ArgumentException">The account is not stored.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public async Task<IMailTransport> ConnectAsync(Account account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Provider == MailProvider.GenericImap)
        {
            throw new NotSupportedException("Generic IMAP accounts are not supported by the sync transport yet.");
        }

        // Token first: a needed sign-in surfaces before any connection is opened.
        ServerEndpoint endpoint = ProviderProfiles.For(account.Provider).Imap;
        AccessToken token = await accountService.GetAccessTokenAsync(account.Id, ct).ConfigureAwait(false);

        var client = new ImapClient { Timeout = MailKitTransport.OperationTimeoutMilliseconds };
        try
        {
            await MailKitTransport
                .RunAsync(
                    endpoint,
                    "connect",
                    async () =>
                    {
                        await client
                            .ConnectAsync(endpoint.Host, endpoint.Port, ConnectionProbe.ToSocketOptions(endpoint.Security), ct)
                            .ConfigureAwait(false);
                        await client
                            .AuthenticateAsync(new SaslMechanismOAuth2(account.EmailAddress, token.Value), ct)
                            .ConfigureAwait(false);
                        return true;
                    },
                    ct)
                .ConfigureAwait(false);
            return new MailKitTransport(client, endpoint);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
