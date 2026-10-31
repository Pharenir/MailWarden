using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MailWarden.Core.Accounts;
using MailWarden.Core.Model;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Providers;

namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// Opens <see cref="IMailIdleWatcher"/> sessions with MailKit: the same token, connect and XOAUTH2 login as
/// <see cref="MailKitTransportFactory"/>, then the folder with role <see cref="FolderRole.Inbox"/> (resolved from the
/// folder list with <see cref="FolderRoleResolver"/>, like the sync) is opened read-only (EXAMINE). Never opens a
/// browser; the token never appears in exception messages.
/// </summary>
/// <param name="accountService">Supplies the access token of the account.</param>
public sealed class MailKitIdleWatcherFactory(AccountService accountService) : IMailIdleWatcherFactory
{
    private readonly AccountService accountService =
        accountService ?? throw new ArgumentNullException(nameof(accountService));

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="account"/> is null.</exception>
    /// <exception cref="ArgumentException">The account is not stored.</exception>
    public async Task<IMailIdleWatcher> ConnectAsync(Account account, CancellationToken ct = default)
    {
        (ImapClient client, ServerEndpoint endpoint) = await MailKitTransportFactory
            .ConnectClientAsync(accountService, account, ct)
            .ConfigureAwait(false);
        try
        {
            IMailFolder inbox = await MailKitTransport
                .RunAsync(endpoint, "inbox open", () => OpenInboxAsync(client, account.Provider, endpoint, ct), ct)
                .ConfigureAwait(false);
            return new MailKitIdleWatcher(client, inbox, endpoint);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // Folder list → first folder with role Inbox → EXAMINE. No inbox in the list is a protocol failure, as in the sync.
    private static async Task<IMailFolder> OpenInboxAsync(
        ImapClient client,
        MailProvider provider,
        ServerEndpoint endpoint,
        CancellationToken ct)
    {
        IReadOnlyList<IMailFolder> serverFolders = await ImapSession
            .ListPersonalFoldersAsync(client, ct)
            .ConfigureAwait(false);
        IMailFolder inbox = serverFolders.FirstOrDefault(serverFolder => FolderRoleResolver
                .Resolve(
                    provider,
                    serverFolder.FullName,
                    serverFolder.DirectorySeparator,
                    MailKitFolderFlags.From(serverFolder.Attributes))
                .Role == FolderRole.Inbox)
            ?? throw new MailTransportException(
                MailTransportFailure.Protocol,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"IMAP server {endpoint.Host}:{endpoint.Port} reported no inbox folder."));
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        return inbox;
    }
}
