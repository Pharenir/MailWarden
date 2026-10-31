using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>One scripted message of a <see cref="FakeMailFolder"/>.</summary>
internal sealed class FakeMailMessage(uint uid, byte[] content, MessageFlags flags, DateTimeOffset internalDate, long size)
{
    public uint Uid { get; } = uid;

    public byte[] Content { get; set; } = content;

    public MessageFlags Flags { get; set; } = flags;

    public DateTimeOffset InternalDate { get; } = internalDate;

    public long Size { get; } = size;

    /// <summary>CONDSTORE value of the last change; 0 on servers without CONDSTORE.</summary>
    public ulong ModSeq { get; set; }
}

/// <summary>
/// Scripted server-side folder. Mutating methods emulate server changes (new mail, flag change, expunge) and keep
/// the CONDSTORE counter consistent. All content is artificial.
/// </summary>
internal sealed class FakeMailFolder
{
    private uint nextUid = 1;

    public FakeMailFolder(string fullName, ServerFolderFlags flags, uint uidValidity = 100, bool condStore = false)
    {
        Info = new RemoteFolder(fullName, '/', flags);
        UidValidity = uidValidity;
        CondStore = condStore;
        HighestModSeq = condStore ? 9UL : 0UL;
    }

    public RemoteFolder Info { get; set; }

    public uint UidValidity { get; set; }

    public bool CondStore { get; }

    /// <summary>Server HighestModSeq; 0 means "no CONDSTORE". Can be set directly for exact scenarios.</summary>
    public ulong HighestModSeq { get; set; }

    public SortedDictionary<uint, FakeMailMessage> Messages { get; } = [];

    /// <summary>UIDs for which FetchMessageAsync returns null (message vanished between search and fetch).</summary>
    public HashSet<uint> NullFetchUids { get; } = [];

    /// <summary>UIDs that SearchUidsAsync leaves out of its result (incomplete SEARCH), although the message exists.</summary>
    public HashSet<uint> SearchHiddenUids { get; } = [];

    /// <summary>UIDs that only the windowed SEARCH (since != null) returns in addition (e.g. a bogus UID 0).</summary>
    public HashSet<uint> WindowSearchExtraUids { get; } = [];

    public string FullName => Info.FullName;

    public RemoteFolderState State => new(UidValidity, nextUid, HighestModSeq, Messages.Count);

    /// <summary>Adds a message with the next free UID and returns that UID.</summary>
    public uint AddMessage(
        byte[] content,
        MessageFlags flags = MessageFlags.None,
        DateTimeOffset? internalDate = null,
        long? size = null)
    {
        uint uid = nextUid++;
        Messages[uid] = new FakeMailMessage(
            uid,
            content,
            flags,
            internalDate ?? new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero),
            size ?? content.Length) { ModSeq = Bump() };
        return uid;
    }

    /// <summary>Replaces the flags of an existing message (server-side change).</summary>
    public void SetFlags(uint uid, MessageFlags flags)
    {
        FakeMailMessage message = Messages[uid];
        message.Flags = flags;
        message.ModSeq = Bump();
    }

    /// <summary>Expunges a message.</summary>
    public void RemoveMessage(uint uid)
    {
        Messages.Remove(uid);
        Bump();
    }

    /// <summary>Emulates a UIDVALIDITY reset: new validity, new messages start again at UID 1.</summary>
    public void ResetUidValidity(uint newUidValidity)
    {
        UidValidity = newUidValidity;
        Messages.Clear();
        nextUid = 1;
    }

    private ulong Bump()
    {
        if (!CondStore)
        {
            return 0;
        }

        HighestModSeq++;
        return HighestModSeq;
    }
}

/// <summary>
/// Scripted IMAP server shared by all transports a <see cref="FakeMailTransportFactory"/> creates. Records every call
/// (in order) and can inject failures and hooks. Offers no write operation by design (the engine is read-only).
/// </summary>
internal sealed partial class FakeMailServer
{
    public List<FakeMailFolder> Folders { get; } = [];

    /// <summary>Every transport call as text, in order: "List", "Open:&lt;folder&gt;", "Search", "FetchFlags:&lt;modseq&gt;", "Fetch:&lt;folder&gt;:&lt;uid&gt;", "FetchBatch:&lt;folder&gt;:&lt;count&gt;".</summary>
    public List<string> Operations { get; } = [];

    public List<string> OpenedFolders { get; } = [];

    /// <summary>
    /// Every UID a fetch was attempted for, single or batched, in order (also UIDs the server returns nothing for).
    /// </summary>
    public List<(string Folder, uint Uid)> Fetches { get; } = [];

    /// <summary>UIDs fetched through <c>FetchMessageAsync</c> (single fetch) only.</summary>
    public List<(string Folder, uint Uid)> SingleFetches { get; } = [];

    /// <summary>One entry per <c>FetchMessagesAsync</c> call: folder and the UIDs exactly as passed.</summary>
    public List<(string Folder, uint[] Uids)> BatchCalls { get; } = [];

    public List<ulong> FlagFetchArguments { get; } = [];

    public List<DateTimeOffset?> SearchSinceArguments { get; } = [];

    public int ConnectCalls { get; set; }

    public List<FakeMailTransport> Transports { get; } = [];

    /// <summary>Thrown by the factory's ConnectAsync when set.</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>Thrown by the factory's ConnectAsync on the given 1-based connect attempt (1 = first connect).</summary>
    public Dictionary<int, Exception> ConnectFailuresByAttempt { get; } = [];

    /// <summary>
    /// When true (default) a transport that throws <c>MailTransportException(Network)</c> reports
    /// <c>IsConnected == false</c> afterwards and fails every further call with Network (lost connection).
    /// </summary>
    public bool NetworkFailureDropsConnection { get; set; } = true;

    /// <summary>
    /// When true, every injected failure (also Protocol) drops the connection of the transport that throws it,
    /// like MailKit does after a protocol error.
    /// </summary>
    public bool AnyFailureDropsConnection { get; set; }

    /// <summary>Called right after the factory created a transport (already in <see cref="Transports"/>).</summary>
    public Action<FakeMailTransport>? OnTransportCreated { get; set; }

    /// <summary>Thrown at the start of every FetchMessagesAsync call for the given folder full name.</summary>
    public Dictionary<string, Exception> BatchFailures { get; } = [];

    public Exception? ListFailure { get; set; }

    /// <summary>Thrown by OpenFolderAsync for the given folder full name.</summary>
    public Dictionary<string, Exception> OpenFailures { get; } = [];

    /// <summary>Thrown by SearchUidsAsync for the given folder full name.</summary>
    public Dictionary<string, Exception> SearchFailures { get; } = [];

    /// <summary>
    /// Thrown by FetchMessageAsync for the given (folder, uid). A batch fetch delivers the UIDs before it and then
    /// throws at this UID.
    /// </summary>
    public Dictionary<(string Folder, uint Uid), Exception> FetchFailures { get; } = [];

    /// <summary>Called at the start of every FetchMessageAsync (folder, uid), before failures are applied.</summary>
    public Action<string, uint>? OnFetch { get; set; }

    /// <summary>Adds a folder and returns it for scripting.</summary>
    public FakeMailFolder AddFolder(
        string fullName,
        ServerFolderFlags flags = ServerFolderFlags.None,
        uint uidValidity = 100,
        bool condStore = false)
    {
        var folder = new FakeMailFolder(fullName, flags, uidValidity, condStore);
        Folders.Add(folder);
        return folder;
    }

    public FakeMailFolder Folder(string fullName) =>
        Folders.Single(folder => string.Equals(folder.FullName, fullName, StringComparison.Ordinal));

    public void RemoveFolder(string fullName) => Folders.Remove(Folder(fullName));

    /// <summary>Forgets the recorded calls (not the scripted state), e.g. between two sync runs.</summary>
    public void ClearLog()
    {
        Operations.Clear();
        OpenedFolders.Clear();
        Fetches.Clear();
        SingleFetches.Clear();
        BatchCalls.Clear();
        FlagFetchArguments.Clear();
        SearchSinceArguments.Clear();
    }
}

/// <summary>Factory over a <see cref="FakeMailServer"/>; each connect yields a fresh, tracked transport.</summary>
internal sealed partial class FakeMailTransportFactory(FakeMailServer server) : IMailTransportFactory
{
    public FakeMailServer Server { get; } = server;

    public Task<IMailTransport> ConnectAsync(Account account, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Server.ConnectCalls++;
        if (Server.ConnectFailuresByAttempt.TryGetValue(Server.ConnectCalls, out Exception? attemptFailure))
        {
            throw attemptFailure;
        }

        if (Server.ConnectFailure is not null)
        {
            throw Server.ConnectFailure;
        }

        var transport = new FakeMailTransport(Server);
        Server.Transports.Add(transport);
        Server.OnTransportCreated?.Invoke(transport);
        return Task.FromResult<IMailTransport>(transport);
    }
}

/// <summary>Transport over the scripted server. Enforces "one open folder at a time" like the real contract.</summary>
internal sealed class FakeMailTransport(FakeMailServer server) : IMailTransport
{
    private FakeMailFolder? openFolder;
    private bool connectionLost;

    public int DisposeCalls { get; private set; }

    /// <summary>Folders opened on this transport instance, in order.</summary>
    public List<string> OpenedFolders { get; } = [];

    /// <summary>False after a lost connection (see <see cref="FakeMailServer.NetworkFailureDropsConnection"/>) or dispose.</summary>
    public bool IsConnected =>
        !connectionLost && !Disposed && (DisconnectAfterOpens is null || OpenedFolders.Count < DisconnectAfterOpens);

    /// <summary>
    /// When set, <see cref="IsConnected"/> turns false once this many folders were opened, without any failure
    /// (silent connection loss between two folders). Calls on the transport keep working.
    /// </summary>
    public int? DisconnectAfterOpens { get; set; }

    public bool Disposed => DisposeCalls > 0;

    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfConnectionLost();
        server.Operations.Add("List");
        if (server.ListFailure is not null)
        {
            Fail(server.ListFailure);
        }

        return Task.FromResult<IReadOnlyList<RemoteFolder>>(server.Folders.Select(folder => folder.Info).ToList());
    }

    public Task<RemoteFolderState> OpenFolderAsync(string fullName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfConnectionLost();
        server.Operations.Add($"Open:{fullName}");
        server.OpenedFolders.Add(fullName);
        OpenedFolders.Add(fullName);
        if (server.OpenFailures.TryGetValue(fullName, out Exception? failure))
        {
            Fail(failure);
        }

        FakeMailFolder folder = server.Folders.FirstOrDefault(candidate => candidate.FullName == fullName)
            ?? throw new MailTransportException(MailTransportFailure.Protocol, "Folder does not exist.");
        openFolder = folder;
        return Task.FromResult(folder.State);
    }

    public Task<IReadOnlyList<uint>> SearchUidsAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfConnectionLost();
        FakeMailFolder folder = RequireOpenFolder();
        server.Operations.Add("Search");
        server.SearchSinceArguments.Add(since);
        if (server.SearchFailures.TryGetValue(folder.FullName, out Exception? failure))
        {
            Fail(failure);
        }

        IEnumerable<FakeMailMessage> matches =
            folder.Messages.Values.Where(message => !folder.SearchHiddenUids.Contains(message.Uid));
        if (since is not null)
        {
            DateTime sinceDay = since.Value.Date;
            matches = matches.Where(message => message.InternalDate.Date >= sinceDay);
        }

        IEnumerable<uint> uids = matches.Select(message => message.Uid);
        if (since is not null)
        {
            uids = uids.Concat(folder.WindowSearchExtraUids);
        }

        return Task.FromResult<IReadOnlyList<uint>>(uids.OrderBy(uid => uid).ToList());
    }

    public Task<IReadOnlyList<RemoteFlags>> FetchFlagsAsync(ulong changedSinceModSeq, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfConnectionLost();
        FakeMailFolder folder = RequireOpenFolder();
        server.Operations.Add($"FetchFlags:{changedSinceModSeq}");
        server.FlagFetchArguments.Add(changedSinceModSeq);

        IEnumerable<FakeMailMessage> matches = folder.Messages.Values;
        if (changedSinceModSeq > 0)
        {
            matches = matches.Where(message => message.ModSeq > changedSinceModSeq);
        }

        return Task.FromResult<IReadOnlyList<RemoteFlags>>(
            matches.Select(message => new RemoteFlags(message.Uid, message.Flags)).ToList());
    }

    public Task<RemoteMessage?> FetchMessageAsync(uint uid, CancellationToken ct = default)
    {
        ThrowIfConnectionLost();
        FakeMailFolder folder = RequireOpenFolder();
        server.Operations.Add($"Fetch:{folder.FullName}:{uid}");
        server.Fetches.Add((folder.FullName, uid));
        server.SingleFetches.Add((folder.FullName, uid));
        server.OnFetch?.Invoke(folder.FullName, uid);
        ct.ThrowIfCancellationRequested();
        if (server.FetchFailures.TryGetValue((folder.FullName, uid), out Exception? failure))
        {
            Fail(failure);
        }

        if (folder.NullFetchUids.Contains(uid) || !folder.Messages.TryGetValue(uid, out FakeMailMessage? message))
        {
            return Task.FromResult<RemoteMessage?>(null);
        }

        return Task.FromResult<RemoteMessage?>(
            new RemoteMessage(uid, message.Flags, message.InternalDate, message.Size, new MemoryStream(message.Content)));
    }

    public async Task FetchMessagesAsync(
        IReadOnlyList<uint> uids,
        Func<RemoteMessage, CancellationToken, Task> onMessage,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfConnectionLost();
        FakeMailFolder folder = RequireOpenFolder();
        server.Operations.Add($"FetchBatch:{folder.FullName}:{uids.Count}");
        server.BatchCalls.Add((folder.FullName, uids.ToArray()));
        if (server.BatchFailures.TryGetValue(folder.FullName, out Exception? batchFailure))
        {
            Fail(batchFailure);
        }

        foreach (uint uid in uids)
        {
            server.Fetches.Add((folder.FullName, uid));
            server.OnFetch?.Invoke(folder.FullName, uid);
            ct.ThrowIfCancellationRequested();
            if (server.FetchFailures.TryGetValue((folder.FullName, uid), out Exception? failure))
            {
                Fail(failure);
            }

            if (folder.NullFetchUids.Contains(uid) || !folder.Messages.TryGetValue(uid, out FakeMailMessage? message))
            {
                continue;
            }

            await onMessage(
                new RemoteMessage(uid, message.Flags, message.InternalDate, message.Size, new MemoryStream(message.Content)),
                ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }

    /// <summary>Throws the injected failure; a Network failure also drops the connection of this transport.</summary>
    private void Fail(Exception failure)
    {
        bool networkLoss = failure is MailTransportException { Failure: MailTransportFailure.Network }
            && server.NetworkFailureDropsConnection;
        if (networkLoss || server.AnyFailureDropsConnection)
        {
            connectionLost = true;
        }

        throw failure;
    }

    private void ThrowIfConnectionLost()
    {
        if (connectionLost)
        {
            throw new MailTransportException(MailTransportFailure.Network, "Connection lost.");
        }
    }

    private FakeMailFolder RequireOpenFolder() =>
        openFolder ?? throw new InvalidOperationException("No folder is open.");
}
