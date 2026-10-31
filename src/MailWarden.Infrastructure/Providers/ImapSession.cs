using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailWarden.Infrastructure.Providers;

/// <summary>
/// IMAP session steps shared by <see cref="ConnectionProbe"/> and the sync transport: connect with the endpoint's
/// transport security, XOAUTH2 login and listing the folders of the personal namespaces. Library exceptions pass
/// through unchanged; each caller maps them to its own error wording.
/// </summary>
internal static class ImapSession
{
    /// <summary>Creates a client, connects it to the endpoint and logs in with XOAUTH2.</summary>
    /// <param name="endpoint">IMAP endpoint of the provider.</param>
    /// <param name="userName">Account address used for the login.</param>
    /// <param name="accessToken">OAuth access token (never logged or put into a message).</param>
    /// <param name="timeoutMilliseconds">MailKit socket timeout per read/write operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The connected and authenticated client; the caller owns and disposes it.</returns>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    /// <remarks>On any failure the client is disposed and the MailKit/socket exception is rethrown unchanged.</remarks>
    internal static async Task<ImapClient> ConnectAsync(
        ServerEndpoint endpoint,
        string userName,
        string accessToken,
        int timeoutMilliseconds,
        CancellationToken ct)
    {
        var client = new ImapClient { Timeout = timeoutMilliseconds };
        try
        {
            await client
                .ConnectAsync(endpoint.Host, endpoint.Port, ConnectionProbe.ToSocketOptions(endpoint.Security), ct)
                .ConfigureAwait(false);
            await client
                .AuthenticateAsync(new SaslMechanismOAuth2(userName, accessToken), ct)
                .ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lists every folder of the personal namespaces in server order, including NoSelect/NonExistent containers. The
    /// namespace root itself (empty full name) is no folder and is skipped. Flags come from the LIST response
    /// (Outlook.com sends them without advertising SPECIAL-USE, AP-00).
    /// </summary>
    /// <param name="client">Connected and authenticated client.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The folders in server order.</returns>
    internal static async Task<IReadOnlyList<IMailFolder>> ListPersonalFoldersAsync(ImapClient client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var folders = new List<IMailFolder>();
        foreach (FolderNamespace folderNamespace in client.PersonalNamespaces)
        {
            IList<IMailFolder> serverFolders = await client
                .GetFoldersAsync(folderNamespace, StatusItems.None, subscribedOnly: false, ct)
                .ConfigureAwait(false);
            folders.AddRange(serverFolders.Where(serverFolder => !string.IsNullOrEmpty(serverFolder.FullName)));
        }

        return folders;
    }
}
