using System.Globalization;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
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

    // Metadata of a message fetch; shared by the single and the batch fetch so both deliver the same fields.
    private const MessageSummaryItems MessageMetadataItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate
        | MessageSummaryItems.Size;

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
    private IImapFolder? openFolder;
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
    /// <remarks>MailKit disconnects the client on network errors and on <see cref="ImapProtocolException"/>.</remarks>
    public bool IsConnected => !isDisposed && client.IsConnected && client.IsAuthenticated;

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        return RunAsync<IReadOnlyList<RemoteFolder>>(
            endpoint,
            "folder listing",
            async () =>
            {
                // Shared listing with ConnectionProbe (personal namespaces, namespace root skipped).
                IReadOnlyList<IMailFolder> serverFolders = await ImapSession
                    .ListPersonalFoldersAsync(client, ct)
                    .ConfigureAwait(false);
                return serverFolders
                    .Select(serverFolder => new RemoteFolder(
                        serverFolder.FullName,
                        serverFolder.DirectorySeparator,
                        MailKitFolderFlags.From(serverFolder.Attributes)))
                    .ToList();
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
                var folder = (IImapFolder)await client.GetFolderAsync(fullName, ct).ConfigureAwait(false);
                await folder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

                // UIDVALIDITY 0 is not a valid IMAP value; stored UIDs could never be validated against it.
                if (folder.UidValidity == 0)
                {
                    throw new MailTransportException(
                        MailTransportFailure.Protocol,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"IMAP server {endpoint.Host}:{endpoint.Port} reported UIDVALIDITY 0 for the folder."));
                }

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
                // Metadata first: an empty answer means the server returned nothing for the UID (gone or not delivered).
                var uniqueId = new UniqueId(folder.UidValidity, uid);
                var request = new FetchRequest(MessageMetadataItems);
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
                    throw MissingInternalDate();
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

    /// <inheritdoc />
    /// <remarks>
    /// Two IMAP commands per call: <c>UID FETCH set (UID FLAGS INTERNALDATE RFC822.SIZE)</c> and
    /// <c>UID FETCH set (BODY.PEEK[])</c> via <see cref="IImapFolder.GetStreamsAsync(IList{UniqueId}, ImapFetchStreamAsyncCallback, CancellationToken, ITransferProgress)"/>
    /// (MailKit splits only an over-long UID set). MailKit buffers each body in memory, invokes the callback while it
    /// reads the response and disposes the stream after the callback returned. Bodies are only requested for UIDs with
    /// metadata. A metadata answer without INTERNALDATE, or bodies arriving out of UID order, raise
    /// <see cref="MailTransportFailure.Protocol"/> (bodies out of order: after all in-order bodies were delivered);
    /// the connection stays usable then. A UID that vanished between the two commands gets no body (no callback); a
    /// <see cref="MessageNotFoundException"/> (not raised by MailKit 4.18 for this call, mapped defensively) is
    /// Protocol as well. An exception of <paramref name="onMessage"/> makes MailKit drop the connection.
    /// </remarks>
    public async Task FetchMessagesAsync(
        IReadOnlyList<uint> uids,
        Func<RemoteMessage, CancellationToken, Task> onMessage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uids);
        ArgumentNullException.ThrowIfNull(onMessage);
        IImapFolder folder = RequireOpenFolder();
        if (uids.Count == 0)
        {
            return;
        }

        // Kept apart from transport failures: an exception of the callback propagates unchanged.
        ExceptionDispatchInfo? callbackFailure = null;
        try
        {
            await RunAsync(
                    endpoint,
                    "message fetch",
                    async () =>
                    {
                        // Metadata of all UIDs in one command; UIDs the server does not answer for are skipped.
                        List<UniqueId> requestedIds = [.. uids.Select(uid => new UniqueId(folder.UidValidity, uid))];
                        var request = new FetchRequest(MessageMetadataItems);
                        IList<IMessageSummary> summaries = await folder
                            .FetchAsync(requestedIds, request, ct)
                            .ConfigureAwait(false);

                        var requestedUids = new HashSet<uint>(uids);
                        var summariesByUid = new Dictionary<uint, IMessageSummary>();
                        foreach (IMessageSummary summary in summaries)
                        {
                            uint uid = summary.UniqueId.Id;
                            if (requestedUids.Contains(uid) && !summariesByUid.ContainsKey(uid))
                            {
                                if (summary.InternalDate is null)
                                {
                                    throw MissingInternalDate();
                                }

                                summariesByUid.Add(uid, summary);
                            }
                        }

                        if (summariesByUid.Count == 0)
                        {
                            return true;
                        }

                        // Bodies of all answered UIDs in one command (BODY.PEEK[], never sets \Seen). Bodies are
                        // delivered in ascending UID order; a body below the last delivered UID is left out and
                        // reported afterwards, so the caller can fetch it singly.
                        List<UniqueId> bodyIds =
                        [
                            .. summariesByUid.Keys.Order().Select(uid => new UniqueId(folder.UidValidity, uid)),
                        ];
                        var deliveredUids = new HashSet<uint>();
                        uint lastDeliveredUid = 0;
                        bool outOfOrder = false;
                        await folder
                            .GetStreamsAsync(
                                bodyIds,
                                async (_, _, uniqueId, stream, callbackCt) =>
                                {
                                    // Unrequested and repeated bodies are ignored.
                                    uint uid = uniqueId.Id;
                                    if (!summariesByUid.TryGetValue(uid, out IMessageSummary? summary)
                                        || deliveredUids.Contains(uid))
                                    {
                                        return;
                                    }

                                    if (uid < lastDeliveredUid)
                                    {
                                        outOfOrder = true;
                                        return;
                                    }

                                    deliveredUids.Add(uid);
                                    lastDeliveredUid = uid;
                                    long size = summary.Size is uint reportedSize ? reportedSize : stream.Length;
                                    var message = new RemoteMessage(
                                        uid,
                                        MapFlags(summary.Flags),
                                        summary.InternalDate!.Value,
                                        size,
                                        stream);
                                    try
                                    {
                                        await onMessage(message, callbackCt).ConfigureAwait(false);
                                    }
                                    catch (Exception exception)
                                    {
                                        callbackFailure = ExceptionDispatchInfo.Capture(exception);
                                        throw;
                                    }
                                },
                                ct)
                            .ConfigureAwait(false);

                        if (outOfOrder)
                        {
                            throw new MailTransportException(
                                MailTransportFailure.Protocol,
                                string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"IMAP server {endpoint.Host}:{endpoint.Port} sent message bodies out of UID order."));
                        }

                        return true;
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception) when (callbackFailure is not null)
        {
            callbackFailure.Throw();
        }
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
        await DisconnectQuietlyAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Logs out (best effort, at most <see cref="DisconnectTimeoutMilliseconds"/>) and disposes the client. Failures of
    /// the logout are ignored: the session is over either way and disposing releases the socket. Shared by every MailKit
    /// session class (sync, commands, IDLE watcher).
    /// </summary>
    /// <param name="client">Client to close; disposed on every path.</param>
    /// <returns>A task that completes when the client is disposed.</returns>
    internal static async Task DisconnectQuietlyAsync(ImapClient client)
    {
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
    /// rejected login → Authentication, rejected command, protocol error, unknown folder or message → Protocol.
    /// Cancellation by the caller and all other exceptions pass through unchanged.
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
            ImapCommandException or ImapProtocolException or FolderNotFoundException or MessageNotFoundException =>
                MailTransportFailure.Protocol,
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

    private MailTransportException MissingInternalDate()
    {
        return new MailTransportException(
            MailTransportFailure.Protocol,
            string.Create(
                CultureInfo.InvariantCulture,
                $"IMAP server {endpoint.Host}:{endpoint.Port} sent a message without INTERNALDATE."));
    }

    private IImapFolder RequireOpenFolder()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        return openFolder ?? throw new InvalidOperationException("No folder is open; call OpenFolderAsync first.");
    }
}
