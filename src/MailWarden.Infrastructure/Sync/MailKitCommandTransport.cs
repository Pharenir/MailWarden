using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Providers;
using ImapMessageFlags = MailKit.MessageFlags;
using MessageFlags = MailWarden.Core.Model.MessageFlags;

namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// <see cref="IMailCommandTransport"/> on an authenticated MailKit <see cref="ImapClient"/>: the only code that changes
/// the server. Library exceptions are mapped like in <see cref="MailKitTransport"/> (own wording, no server text, no
/// token).
/// </summary>
/// <remarks>
/// Safety rules (AP-03c S1/S2, checked against the MailKit 4.18 source, see <c>work\AP-03c\impl-notes.md</c>):
/// <list type="bullet">
/// <item>No <c>CLOSE</c>: <see cref="IMailFolder.CloseAsync"/> is never called (MailKit sends CLOSE for
/// <c>expunge: true</c>). A folder is left by the SELECT of the next one or by LOGOUT on dispose; both leave without
/// expunge (RFC 3501 6.4.2).</item>
/// <item>No plain <c>EXPUNGE</c>: <see cref="IMailFolder.ExpungeAsync(CancellationToken)"/> is never called.
/// <see cref="IMailFolder.ExpungeAsync(IList{UniqueId}, CancellationToken)"/> falls back to a plain EXPUNGE when the
/// server lacks UIDPLUS, so UIDPLUS is required before anything is sent.</item>
/// <item><see cref="IMailFolder.MoveToAsync(IList{UniqueId}, IMailFolder, CancellationToken)"/> falls back to COPY +
/// STORE \Deleted + expunge without MOVE, and to a MOVE by sequence numbers without UIDPLUS; both MOVE and UIDPLUS are
/// required before anything is sent.</item>
/// <item><c>\Deleted</c> is set only inside <see cref="ExpungeAsync"/>, for exactly the UIDs that are expunged.</item>
/// </list>
/// </remarks>
internal sealed class MailKitCommandTransport : IMailCommandTransport
{
    private const int DisconnectTimeoutMilliseconds = 5_000;

    // Flags a command may add or remove; \Deleted is reserved for ExpungeAsync.
    private static readonly (MessageFlags Flag, ImapMessageFlags ImapFlag)[] SettableFlagMapping =
    [
        (MessageFlags.Seen, ImapMessageFlags.Seen),
        (MessageFlags.Answered, ImapMessageFlags.Answered),
        (MessageFlags.Flagged, ImapMessageFlags.Flagged),
        (MessageFlags.Draft, ImapMessageFlags.Draft),
    ];

    private static readonly MessageFlags SettableFlags =
        SettableFlagMapping.Aggregate(MessageFlags.None, (combined, mapping) => combined | mapping.Flag);

    private readonly ImapClient client;
    private readonly ServerEndpoint endpoint;
    private IImapFolder? selectedFolder;
    private bool isDisposed;

    /// <summary>Wraps a connected and authenticated client; the transport owns and disposes it.</summary>
    /// <param name="client">Authenticated client.</param>
    /// <param name="endpoint">Endpoint the client is connected to (used in error messages only).</param>
    internal MailKitCommandTransport(ImapClient client, ServerEndpoint endpoint)
    {
        this.client = client;
        this.endpoint = endpoint;
    }

    /// <inheritdoc />
    /// <remarks>MailKit disconnects the client on network errors and on <see cref="ImapProtocolException"/>.</remarks>
    public bool IsConnected => !isDisposed && client.IsConnected && client.IsAuthenticated;

    /// <inheritdoc />
    /// <remarks>
    /// Sends <c>SELECT</c> (MailKit has no shortcut for an already selected folder). A server that grants read-only
    /// access, or reports UIDVALIDITY 0, raises <see cref="MailTransportFailure.Protocol"/>; no folder is selected then.
    /// </remarks>
    public Task<uint> SelectFolderAsync(string fullName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullName);
        ObjectDisposedException.ThrowIf(isDisposed, this);
        return MailKitTransport.RunAsync(
            endpoint,
            "folder select",
            async () =>
            {
                // SELECT of the next folder implicitly leaves the previous one without expunge (RFC 3501 6.4.2).
                selectedFolder = null;
                var folder = (IImapFolder)await client.GetFolderAsync(fullName, ct).ConfigureAwait(false);
                FolderAccess access = await folder.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
                if (access != FolderAccess.ReadWrite)
                {
                    throw Rejected("granted only read-only access to the folder");
                }

                if (folder.UidValidity == 0)
                {
                    throw Rejected("reported UIDVALIDITY 0 for the folder");
                }

                selectedFolder = folder;
                return folder.UidValidity;
            },
            ct);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="flags"/> contains Deleted or an unknown bit.</exception>
    public Task AddFlagsAsync(IReadOnlyList<uint> uids, MessageFlags flags, CancellationToken ct = default)
    {
        return StoreFlagsAsync(uids, flags, StoreAction.Add, "flag change", ct);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="flags"/> contains Deleted or an unknown bit.</exception>
    public Task RemoveFlagsAsync(IReadOnlyList<uint> uids, MessageFlags flags, CancellationToken ct = default)
    {
        return StoreFlagsAsync(uids, flags, StoreAction.Remove, "flag change", ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Requires MOVE (RFC 6851) and UIDPLUS (RFC 4315); without them <see cref="MailTransportFailure.Protocol"/> is
    /// raised before anything is sent (no COPY + EXPUNGE fallback). Sends <c>UID MOVE set target</c>. The map is built
    /// from the COPYUID response code (MailKit pairs source and destination UIDs by position); entries for UIDs that
    /// were not requested are dropped.
    /// </remarks>
    public Task<IReadOnlyDictionary<uint, uint>> MoveAsync(
        IReadOnlyList<uint> uids,
        string targetFullName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uids);
        ArgumentException.ThrowIfNullOrEmpty(targetFullName);
        IImapFolder folder = RequireSelectedFolder();
        RequireCapability(ImapCapabilities.Move, "MOVE");
        RequireCapability(ImapCapabilities.UidPlus, "UIDPLUS");
        if (uids.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<uint, uint>>(new Dictionary<uint, uint>());
        }

        return MailKitTransport.RunAsync<IReadOnlyDictionary<uint, uint>>(
            endpoint,
            "move",
            async () =>
            {
                IMailFolder target = await client.GetFolderAsync(targetFullName, ct).ConfigureAwait(false);
                UniqueIdMap map = await folder
                    .MoveToAsync(ToUniqueIds(folder, uids), target, ct)
                    .ConfigureAwait(false);

                var requestedUids = new HashSet<uint>(uids);
                var newUidsBySource = new Dictionary<uint, uint>();
                int pairCount = Math.Min(map.Source.Count, map.Destination.Count);
                for (int index = 0; index < pairCount; index++)
                {
                    uint sourceUid = map.Source[index].Id;
                    if (requestedUids.Contains(sourceUid))
                    {
                        newUidsBySource.TryAdd(sourceUid, map.Destination[index].Id);
                    }
                }

                return newUidsBySource;
            },
            ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Requires UIDPLUS (MailKit would otherwise send a plain EXPUNGE) and a folder whose PERMANENTFLAGS allow
    /// <c>\Deleted</c>; otherwise <see cref="MailTransportFailure.Protocol"/> before anything is sent. Sends
    /// <c>UID STORE set +FLAGS.SILENT (\Deleted)</c>, then <c>UID EXPUNGE set</c> with the same set.
    /// </remarks>
    public Task ExpungeAsync(IReadOnlyList<uint> uids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uids);
        IImapFolder folder = RequireSelectedFolder();
        RequireCapability(ImapCapabilities.UidPlus, "UIDPLUS");
        if ((folder.PermanentFlags & ImapMessageFlags.Deleted) == 0)
        {
            throw Rejected("does not allow the \\Deleted flag to be stored in the folder");
        }

        if (uids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return MailKitTransport.RunAsync(
            endpoint,
            "expunge",
            async () =>
            {
                IList<UniqueId> uniqueIds = ToUniqueIds(folder, uids);
                var markDeleted = new StoreFlagsRequest(StoreAction.Add, ImapMessageFlags.Deleted) { Silent = true };
                await folder.StoreAsync(uniqueIds, markDeleted, ct).ConfigureAwait(false);

                // Re-checked right before the call: without UIDPLUS MailKit would expunge every \Deleted message.
                RequireCapability(ImapCapabilities.UidPlus, "UIDPLUS");
                await folder.ExpungeAsync(uniqueIds, ct).ConfigureAwait(false);
                return true;
            },
            ct);
    }

    /// <summary>Logs out (best effort, short timeout) and releases the connection. Safe to call more than once.</summary>
    /// <returns>A task that completes when the client is disposed.</returns>
    /// <remarks>LOGOUT leaves the selected folder without expunge (RFC 3501 6.4.2); no CLOSE is sent.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        selectedFolder = null;
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

    private Task StoreFlagsAsync(
        IReadOnlyList<uint> uids,
        MessageFlags flags,
        StoreAction action,
        string operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uids);
        if ((flags & ~SettableFlags) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(flags),
                flags,
                "Only Seen, Answered, Flagged and Draft can be changed; Deleted is reserved for ExpungeAsync.");
        }

        IImapFolder folder = RequireSelectedFolder();
        ImapMessageFlags imapFlags = ToImapFlags(flags);

        // MailKit silently drops flags the folder cannot store permanently; that would report a change that never
        // happened.
        if ((folder.PermanentFlags & imapFlags) != imapFlags)
        {
            throw Rejected("does not allow these flags to be stored in the folder");
        }

        if (uids.Count == 0 || imapFlags == ImapMessageFlags.None)
        {
            return Task.CompletedTask;
        }

        return MailKitTransport.RunAsync(
            endpoint,
            operation,
            async () =>
            {
                var request = new StoreFlagsRequest(action, imapFlags) { Silent = true };
                await folder.StoreAsync(ToUniqueIds(folder, uids), request, ct).ConfigureAwait(false);
                return true;
            },
            ct);
    }

    private static ImapMessageFlags ToImapFlags(MessageFlags flags)
    {
        ImapMessageFlags imapFlags = ImapMessageFlags.None;
        foreach ((MessageFlags flag, ImapMessageFlags imapFlag) in SettableFlagMapping)
        {
            if ((flags & flag) != 0)
            {
                imapFlags |= imapFlag;
            }
        }

        return imapFlags;
    }

    private static List<UniqueId> ToUniqueIds(IImapFolder folder, IReadOnlyList<uint> uids)
    {
        if (uids.Contains(0u))
        {
            throw new ArgumentOutOfRangeException(nameof(uids), "UID 0 is not a valid IMAP UID.");
        }

        return [.. uids.Distinct().Order().Select(uid => new UniqueId(folder.UidValidity, uid))];
    }

    private void RequireCapability(ImapCapabilities capability, string capabilityName)
    {
        if ((client.Capabilities & capability) == 0)
        {
            throw Rejected(string.Create(CultureInfo.InvariantCulture, $"does not support {capabilityName}"));
        }
    }

    // Own wording only (no server text): "IMAP server host:port <reason>."
    private MailTransportException Rejected(string reason)
    {
        return new MailTransportException(
            MailTransportFailure.Protocol,
            string.Create(CultureInfo.InvariantCulture, $"IMAP server {endpoint.Host}:{endpoint.Port} {reason}."));
    }

    private IImapFolder RequireSelectedFolder()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        return selectedFolder
            ?? throw new InvalidOperationException("No folder is selected; call SelectFolderAsync first.");
    }
}
