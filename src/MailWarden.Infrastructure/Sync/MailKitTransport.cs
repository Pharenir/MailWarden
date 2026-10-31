using System.Globalization;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Providers;
using IMailTransport = MailWarden.Core.Sync.IMailTransport;
using ImapMessageFlags = MailKit.MessageFlags;
using MessageFlags = MailWarden.Core.Model.MessageFlags;

namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// <see cref="IMailTransport"/> on an authenticated MailKit <see cref="ImapClient"/>. Folders are opened read-only
/// (EXAMINE) and messages are downloaded with BODY.PEEK[], so the server state never changes. Library exceptions are
/// mapped to <see cref="MailTransportException"/> with own wording (no server responses, no token).
/// </summary>
internal sealed class MailKitTransport : IMailTransport
{
    /// <summary>MailKit socket timeout per read/write operation.</summary>
    internal const int OperationTimeoutMilliseconds = 60_000;

    private const int DisconnectTimeoutMilliseconds = 5_000;

    // \Seen \Answered \Flagged \Deleted \Draft; keywords and \Recent are ignored.
    private static readonly (ImapMessageFlags ImapFlag, MessageFlags Flag)[] FlagMapping =
    [
        (ImapMessageFlags.Seen, MessageFlags.Seen),
        (ImapMessageFlags.Answered, MessageFlags.Answered),
        (ImapMessageFlags.Flagged, MessageFlags.Flagged),
        (ImapMessageFlags.Deleted, MessageFlags.Deleted),
        (ImapMessageFlags.Draft, MessageFlags.Draft),
    ];

    private readonly ImapClient client;
    private readonly ServerEndpoint endpoint;
    private IMailFolder? openFolder;
    private bool isDisposed;

    /// <summary>Wraps a connected and authenticated client; the transport owns and disposes it.</summary>
    /// <param name="client">Authenticated client.</param>
    /// <param name="endpoint">Endpoint the client is connected to (used in error messages only).</param>
    internal MailKitTransport(ImapClient client, ServerEndpoint endpoint)
    {
        this.client = client;
        this.endpoint = endpoint;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        return RunAsync<IReadOnlyList<RemoteFolder>>(
            endpoint,
            "folder listing",
            async () =>
            {
                // Flags come from the LIST response (Outlook.com sends them without advertising SPECIAL-USE, AP-00).
                var remoteFolders = new List<RemoteFolder>();
                foreach (FolderNamespace folderNamespace in client.PersonalNamespaces)
                {
                    IList<IMailFolder> serverFolders = await client
                        .GetFoldersAsync(folderNamespace, StatusItems.None, subscribedOnly: false, ct)
                        .ConfigureAwait(false);
                    foreach (IMailFolder serverFolder in serverFolders)
                    {
                        // The namespace root itself has an empty name and is no folder.
                        if (string.IsNullOrEmpty(serverFolder.FullName))
                        {
                            continue;
                        }

                        remoteFolders.Add(new RemoteFolder(
                            serverFolder.FullName,
                            serverFolder.DirectorySeparator,
                            MailKitFolderFlags.From(serverFolder.Attributes)));
                    }
                }

                return remoteFolders;
            },
            ct);
    }

    /// <inheritdoc />
    public Task<RemoteFolderState> OpenFolderAsync(string fullName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullName);
        ObjectDisposedException.ThrowIf(isDisposed, this);
        return RunAsync(
            endpoint,
            "folder open",
            async () =>
            {
                // EXAMINE of the next folder implicitly closes the previous one (no expunge on a read-only folder).
                openFolder = null;
                IMailFolder folder = await client.GetFolderAsync(fullName, ct).ConfigureAwait(false);
                await folder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
                openFolder = folder;

                bool hasCondStore = client.Capabilities.HasFlag(ImapCapabilities.CondStore);
                return new RemoteFolderState(
                    folder.UidValidity,
                    folder.UidNext?.Id ?? 0,
                    hasCondStore ? folder.HighestModSeq : 0,
                    folder.Count);
            },
            ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<uint>> SearchUidsAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        IMailFolder folder = RequireOpenFolder();
        return RunAsync<IReadOnlyList<uint>>(
            endpoint,
            "search",
            async () =>
            {
                if (folder.Count == 0)
                {
                    return [];
                }

                // IMAP SINCE compares the calendar day only; the day of the value in its own offset is meant.
                SearchQuery query = since is DateTimeOffset sinceDay
                    ? SearchQuery.DeliveredAfter(sinceDay.Date)
                    : SearchQuery.All;
                IList<UniqueId> uniqueIds = await folder.SearchAsync(query, ct).ConfigureAwait(false);
                return uniqueIds.Select(uniqueId => uniqueId.Id).Order().ToList();
            },
            ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteFlags>> FetchFlagsAsync(ulong changedSinceModSeq, CancellationToken ct = default)
    {
        IMailFolder folder = RequireOpenFolder();
        return RunAsync<IReadOnlyList<RemoteFlags>>(
            endpoint,
            "flag fetch",
            async () =>
            {
                if (folder.Count == 0)
                {
                    return [];
                }

                var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);
                if (changedSinceModSeq > 0)
                {
                    request.ChangedSince = changedSinceModSeq;
                }

                IList<IMessageSummary> summaries = await folder.FetchAsync(0, -1, request, ct).ConfigureAwait(false);
                return summaries
                    .Select(summary => new RemoteFlags(summary.UniqueId.Id, MapFlags(summary.Flags)))
                    .ToList();
            },
            ct);
    }

    /// <inheritdoc />
    public Task<RemoteMessage?> FetchMessageAsync(uint uid, CancellationToken ct = default)
    {
        IMailFolder folder = RequireOpenFolder();
        return RunAsync(
            endpoint,
            "message fetch",
            async () =>
            {
                // Metadata first: an empty answer means the UID no longer exists.
                var uniqueId = new UniqueId(folder.UidValidity, uid);
                var request = new FetchRequest(
                    MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate
                    | MessageSummaryItems.Size);
                IList<IMessageSummary> summaries = await folder
                    .FetchAsync([uniqueId], request, ct)
                    .ConfigureAwait(false);
                IMessageSummary? summary = summaries.FirstOrDefault(candidate => candidate.UniqueId.Id == uid);
                if (summary is null)
                {
                    return null;
                }

                if (summary.InternalDate is not DateTimeOffset internalDate)
                {
                    throw new MailTransportException(
                        MailTransportFailure.Protocol,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"IMAP server {endpoint.Host}:{endpoint.Port} sent a message without INTERNALDATE."));
                }

                // BODY.PEEK[]: the complete raw message without setting \Seen (MailKit always peeks).
                Stream content;
                try
                {
                    content = await folder.GetStreamAsync(uniqueId, ct).ConfigureAwait(false);
                }
                catch (MessageNotFoundException)
                {
                    // Expunged between the two commands.
                    return (RemoteMessage?)null;
                }

                long size = summary.Size is uint reportedSize ? reportedSize : content.Length;
                return new RemoteMessage(uid, MapFlags(summary.Flags), internalDate, size, content);
            },
            ct);
    }

    /// <summary>Logs out (best effort, short timeout) and releases the connection. Safe to call more than once.</summary>
    /// <returns>A task that completes when the client is disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        openFolder = null;
        try
        {
            if (client.IsConnected)
            {
                using var disconnectTimeout = new CancellationTokenSource(DisconnectTimeoutMilliseconds);
                await client.DisconnectAsync(quit: true, disconnectTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ignored on purpose: the session is over either way; disposing below releases the socket.
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Runs one MailKit operation and maps its failures: socket/IO/TLS/timeouts and a lost connection → Network,
    /// rejected login → Authentication, rejected command, protocol error or unknown folder → Protocol. Cancellation
    /// by the caller and all other exceptions pass through unchanged.
    /// </summary>
    /// <typeparam name="TResult">Result type of the operation.</typeparam>
    /// <param name="endpoint">Server endpoint (for the message).</param>
    /// <param name="operation">Short English name of the operation, e.g. "folder open".</param>
    /// <param name="action">The MailKit calls.</param>
    /// <param name="ct">Caller's cancellation token.</param>
    /// <returns>The result of <paramref name="action"/>.</returns>
    internal static async Task<TResult> RunAsync<TResult>(
        ServerEndpoint endpoint,
        string operation,
        Func<Task<TResult>> action,
        CancellationToken ct)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (Classify(exception, ct) is MailTransportFailure failure)
        {
            throw new MailTransportException(failure, Describe(endpoint, operation, failure), exception);
        }
    }

    private static MailTransportFailure? Classify(Exception exception, CancellationToken ct)
    {
        return exception switch
        {
            OperationCanceledException when ct.IsCancellationRequested => null,
            MailTransportException => null,
            AuthenticationException => MailTransportFailure.Authentication,
            ImapCommandException or ImapProtocolException or FolderNotFoundException => MailTransportFailure.Protocol,
            SslHandshakeException or SocketException or IOException or TimeoutException
                or ServiceNotConnectedException or OperationCanceledException => MailTransportFailure.Network,
            _ => null,
        };
    }

    // Own wording only: server responses and library messages are not passed through.
    private static string Describe(ServerEndpoint endpoint, string operation, MailTransportFailure failure)
    {
        string target = string.Create(CultureInfo.InvariantCulture, $"{endpoint.Host}:{endpoint.Port}");
        return failure switch
        {
            MailTransportFailure.Authentication => $"IMAP login at {target} was rejected (access token not accepted).",
            MailTransportFailure.Protocol => $"IMAP {operation} at {target} was rejected by the server.",
            _ => $"IMAP {operation} at {target} failed because the connection broke off or timed out.",
        };
    }

    private static MessageFlags MapFlags(ImapMessageFlags? imapFlags)
    {
        MessageFlags flags = MessageFlags.None;
        if (imapFlags is not ImapMessageFlags presentFlags)
        {
            return flags;
        }

        foreach ((ImapMessageFlags imapFlag, MessageFlags flag) in FlagMapping)
        {
            if ((presentFlags & imapFlag) != 0)
            {
                flags |= flag;
            }
        }

        return flags;
    }

    private IMailFolder RequireOpenFolder()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        return openFolder ?? throw new InvalidOperationException("No folder is open; call OpenFolderAsync first.");
    }
}
