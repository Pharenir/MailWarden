using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MailWarden.Core.Accounts;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Providers;
using MailFolder = MailWarden.Core.Model.MailFolder;

namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// <see cref="IMailFolderProvisioner"/> on a MailKit session of its own. Commands on the wire besides login and session
/// setup: <c>LIST</c>, for a missing folder <c>CREATE</c> (MailKit follows it with <c>LIST</c>) and <c>SUBSCRIBE</c>, and
/// <c>STATUS (UIDVALIDITY)</c>. No folder is ever selected, so nothing can be expunged; the session ends with <c>LOGOUT</c>.
/// An existing server folder is not changed.
/// </summary>
/// <remarks>
/// A folder that is stored locally with a known UIDVALIDITY and the right role is left alone. Its stored value must not be
/// overwritten here: the sync compares it with the server to detect a UIDVALIDITY reset and to drop stale UIDs, and an
/// overwritten value would hide that reset from it while old rows still point at other messages.
/// </remarks>
/// <param name="accountService">Supplies the access token of the account.</param>
/// <param name="folders">Local folder store.</param>
public sealed class MailKitFolderProvisioner(AccountService accountService, IFolderStore folders) : IMailFolderProvisioner
{
    private static readonly (string FullName, FolderRole Role)[] AppFolders =
    [
        (FolderRoleResolver.ScreenerName, FolderRole.Screener),
        (FolderRoleResolver.QuarantineName, FolderRole.Quarantine),
    ];

    private readonly AccountService accountService =
        accountService ?? throw new ArgumentNullException(nameof(accountService));

    private readonly IFolderStore folders = folders ?? throw new ArgumentNullException(nameof(folders));

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="account"/> is null.</exception>
    /// <exception cref="NotSupportedException">The account is a generic IMAP account (password login not supported).</exception>
    /// <exception cref="ArgumentException">The account is not stored.</exception>
    /// <exception cref="StorageException">Storing a folder failed.</exception>
    public async Task EnsureAppFoldersAsync(Account account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        (ImapClient client, ServerEndpoint endpoint) = await MailKitTransportFactory
            .ConnectClientAsync(accountService, account, ct)
            .ConfigureAwait(false);
        try
        {
            IReadOnlyList<MailFolder> storedFolders = await folders.ListAsync(account.Id, ct).ConfigureAwait(false);
            IReadOnlyList<IMailFolder> serverFolders = await MailKitTransport
                .RunAsync(endpoint, "folder listing", () => ImapSession.ListPersonalFoldersAsync(client, ct), ct)
                .ConfigureAwait(false);

            foreach ((string fullName, FolderRole role) in AppFolders)
            {
                MailFolder? storedFolder = storedFolders.FirstOrDefault(stored => stored.FullName == fullName);
                if (storedFolder is not null && storedFolder.Role == role && storedFolder.UidValidity != 0)
                {
                    continue;
                }

                uint serverUidValidity = await MailKitTransport
                    .RunAsync(
                        endpoint,
                        "app folder setup",
                        () => EnsureServerFolderAsync(client, endpoint, account.Provider, serverFolders, fullName, role, ct),
                        ct)
                    .ConfigureAwait(false);
                uint uidValidity = storedFolder is { UidValidity: not 0 } ? storedFolder.UidValidity : serverUidValidity;
                await folders
                    .UpsertAsync(new MailFolder(0, account.Id, fullName, role, uidValidity), ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await MailKitTransport.DisconnectQuietlyAsync(client).ConfigureAwait(false);
        }
    }

    // Reuses or creates the server folder and returns its UIDVALIDITY. The folder must resolve to the expected role, so that
    // the next sync keeps it and the local row does not contradict the resolver.
    private static async Task<uint> EnsureServerFolderAsync(
        ImapClient client,
        ServerEndpoint endpoint,
        MailProvider provider,
        IReadOnlyList<IMailFolder> serverFolders,
        string fullName,
        FolderRole role,
        CancellationToken ct)
    {
        // IMAP folder names are case-sensitive, like the rule of FolderRoleResolver for the app folders.
        IMailFolder serverFolder = serverFolders.FirstOrDefault(candidate => candidate.FullName == fullName)
            ?? await CreateTopLevelFolderAsync(client, endpoint, fullName, ct).ConfigureAwait(false);

        FolderClassification classification = FolderRoleResolver.Resolve(
            provider,
            serverFolder.FullName,
            serverFolder.DirectorySeparator,
            MailKitFolderFlags.From(serverFolder.Attributes));
        if (classification.Role != role)
        {
            throw ProtocolFailure(endpoint, $"has a folder that cannot serve as the app folder of role {role}");
        }

        // STATUS reads the value without selecting the folder.
        if (!client.Capabilities.HasFlag(ImapCapabilities.Status))
        {
            throw ProtocolFailure(endpoint, "does not support STATUS");
        }

        await serverFolder.StatusAsync(StatusItems.UidValidity, ct).ConfigureAwait(false);
        if (serverFolder.UidValidity == 0)
        {
            throw ProtocolFailure(endpoint, "reported no valid UIDVALIDITY for an app folder");
        }

        return serverFolder.UidValidity;
    }

    private static async Task<IMailFolder> CreateTopLevelFolderAsync(
        ImapClient client,
        ServerEndpoint endpoint,
        string fullName,
        CancellationToken ct)
    {
        if (client.PersonalNamespaces.Count == 0)
        {
            throw ProtocolFailure(endpoint, "reported no personal namespace");
        }

        IMailFolder namespaceRoot = client.GetFolder(client.PersonalNamespaces[0]);
        IMailFolder createdFolder = await namespaceRoot.CreateAsync(fullName, isMessageFolder: true, ct).ConfigureAwait(false)
            ?? throw ProtocolFailure(endpoint, "did not list an app folder after creating it");

        try
        {
            // So that other clients show the folder. Not needed by this app: it lists unsubscribed folders as well.
            await createdFolder.SubscribeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            // Ignored on purpose. A lost connection surfaces with the STATUS command that follows.
        }

        return createdFolder;
    }

    private static MailTransportException ProtocolFailure(ServerEndpoint endpoint, string problem)
    {
        return new MailTransportException(
            MailTransportFailure.Protocol,
            string.Create(CultureInfo.InvariantCulture, $"IMAP server {endpoint.Host}:{endpoint.Port} {problem}."));
    }
}
