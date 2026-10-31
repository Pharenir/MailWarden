using MailWarden.Core.Sync;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>One recorded write-session call. <see cref="Text"/> is a compact form for ordered assertions.</summary>
internal sealed record CommandCall(string Kind, string Folder, IReadOnlyList<uint> Uids, MessageFlags Flags, string? Target)
{
    public const string Select = "Select";
    public const string AddFlags = "AddFlags";
    public const string RemoveFlags = "RemoveFlags";
    public const string Move = "Move";
    public const string Expunge = "Expunge";

    /// <summary>
    /// "Select:Junk", "AddFlags:INBOX:1,2:Seen", "RemoveFlags:INBOX:1:Seen", "Move:INBOX:1,2>Archive",
    /// "Expunge:Trash:5,6".
    /// </summary>
    public string Text => Kind switch
    {
        Select => $"Select:{Folder}",
        AddFlags or RemoveFlags => $"{Kind}:{Folder}:{string.Join(',', Uids)}:{Flags}",
        Move => $"Move:{Folder}:{string.Join(',', Uids)}>{Target}",
        _ => $"{Kind}:{Folder}:{string.Join(',', Uids)}",
    };

    public bool IsWrite => Kind != Select;
}

/// <summary>Scripting and recording of the write session; shares the folders of the read-only fake server.</summary>
internal sealed partial class FakeMailServer
{
    /// <summary>Every call on any command transport, in order.</summary>
    public List<CommandCall> CommandCalls { get; } = [];

    /// <summary>Number of <c>ConnectForCommandsAsync</c> calls (also failed ones).</summary>
    public int CommandConnectCalls { get; set; }

    public List<FakeCommandTransport> CommandTransports { get; } = [];

    /// <summary>Thrown by <c>ConnectForCommandsAsync</c> when set.</summary>
    public Exception? CommandConnectFailure { get; set; }

    /// <summary>
    /// Called with every command call before it takes effect; a returned exception is thrown instead (nothing changes
    /// on the server). A Network failure also drops the connection of that transport.
    /// </summary>
    public Func<CommandCall, Exception?>? CommandFailure { get; set; }

    /// <summary>When true, MoveAsync moves the messages but returns an empty COPYUID map.</summary>
    public bool OmitCopyUid { get; set; }

    /// <summary>Source UIDs for which MoveAsync leaves out the COPYUID mapping (the message is moved anyway).</summary>
    public HashSet<uint> OmitCopyUidFor { get; } = [];

    public IEnumerable<CommandCall> WriteCalls => CommandCalls.Where(call => call.IsWrite);

    public IEnumerable<string> CommandCallTexts => CommandCalls.Select(call => call.Text);

    /// <summary>All UIDs of a folder on the server.</summary>
    public uint[] ServerUids(string fullName) => Folder(fullName).Messages.Keys.ToArray();
}

internal sealed partial class FakeMailTransportFactory
{
    public Task<IMailCommandTransport> ConnectForCommandsAsync(Account account, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Server.CommandConnectCalls++;
        if (Server.CommandConnectFailure is not null)
        {
            throw Server.CommandConnectFailure;
        }

        var transport = new FakeCommandTransport(Server);
        Server.CommandTransports.Add(transport);
        return Task.FromResult<IMailCommandTransport>(transport);
    }
}

/// <summary>
/// Write session over the scripted server. It really applies flags, moves and expunges, so a following sync pass
/// sees the result. Expunge removes exactly the given UIDs.
/// </summary>
internal sealed class FakeCommandTransport(FakeMailServer server) : IMailCommandTransport
{
    private FakeMailFolder? selected;
    private bool connectionLost;

    public int DisposeCalls { get; private set; }

    public bool Disposed => DisposeCalls > 0;

    public bool IsConnected => !connectionLost && !Disposed;

    public Task<uint> SelectFolderAsync(string fullName, CancellationToken ct = default)
    {
        Begin(ct);
        Record(new CommandCall(CommandCall.Select, fullName, [], MessageFlags.None, null));
        selected = server.Folders.FirstOrDefault(folder => folder.FullName == fullName)
            ?? throw new MailTransportException(MailTransportFailure.Protocol, "Folder does not exist.");
        return Task.FromResult(selected.UidValidity);
    }

    public Task AddFlagsAsync(IReadOnlyList<uint> uids, MessageFlags flags, CancellationToken ct = default)
    {
        Begin(ct);
        FakeMailFolder folder = RequireSelected();
        Record(new CommandCall(CommandCall.AddFlags, folder.FullName, uids.ToArray(), flags, null));
        foreach (uint uid in uids.Where(folder.Messages.ContainsKey))
        {
            folder.SetFlags(uid, folder.Messages[uid].Flags | flags);
        }

        return Task.CompletedTask;
    }

    public Task RemoveFlagsAsync(IReadOnlyList<uint> uids, MessageFlags flags, CancellationToken ct = default)
    {
        Begin(ct);
        FakeMailFolder folder = RequireSelected();
        Record(new CommandCall(CommandCall.RemoveFlags, folder.FullName, uids.ToArray(), flags, null));
        foreach (uint uid in uids.Where(folder.Messages.ContainsKey))
        {
            folder.SetFlags(uid, folder.Messages[uid].Flags & ~flags);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<uint, uint>> MoveAsync(
        IReadOnlyList<uint> uids,
        string targetFullName,
        CancellationToken ct = default)
    {
        Begin(ct);
        FakeMailFolder source = RequireSelected();
        Record(new CommandCall(CommandCall.Move, source.FullName, uids.ToArray(), MessageFlags.None, targetFullName));
        FakeMailFolder target = server.Folders.FirstOrDefault(folder => folder.FullName == targetFullName)
            ?? throw new MailTransportException(MailTransportFailure.Protocol, "Target folder does not exist.");
        var map = new Dictionary<uint, uint>();
        foreach (uint uid in uids.Where(source.Messages.ContainsKey))
        {
            FakeMailMessage message = source.Messages[uid];
            uint newUid = target.AddMessage(message.Content, message.Flags, message.InternalDate, message.Size);
            source.RemoveMessage(uid);
            if (!server.OmitCopyUid && !server.OmitCopyUidFor.Contains(uid))
            {
                map[uid] = newUid;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<uint, uint>>(map);
    }

    public Task ExpungeAsync(IReadOnlyList<uint> uids, CancellationToken ct = default)
    {
        Begin(ct);
        FakeMailFolder folder = RequireSelected();
        Record(new CommandCall(CommandCall.Expunge, folder.FullName, uids.ToArray(), MessageFlags.None, null));
        foreach (uint uid in uids.Where(folder.Messages.ContainsKey).ToList())
        {
            folder.RemoveMessage(uid);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }

    private void Begin(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (connectionLost)
        {
            throw new MailTransportException(MailTransportFailure.Network, "Connection lost.");
        }
    }

    /// <summary>Records the call, then throws the injected failure (if any) before the call takes effect.</summary>
    private void Record(CommandCall call)
    {
        server.CommandCalls.Add(call);
        Exception? failure = server.CommandFailure?.Invoke(call);
        if (failure is null)
        {
            return;
        }

        if (failure is MailTransportException { Failure: MailTransportFailure.Network })
        {
            connectionLost = true;
        }

        throw failure;
    }

    private FakeMailFolder RequireSelected() =>
        selected ?? throw new InvalidOperationException("No folder is selected.");
}
