using MailWarden.Core.Commands;
using MailWarden.Infrastructure.Composition;

namespace MailWarden.Presentation.Tests.TestSupport;

/// <summary>One recorded call of <see cref="FakeMailboxActions"/>.</summary>
internal sealed record ActionCall(string Name, IReadOnlyList<long> MessageIds, long? FolderId = null, bool? Value = null);

/// <summary>
/// AP-04b actions fake. Records every call, keeps its own list of pending commands (so that
/// <see cref="ListPendingAsync"/> reports what was enqueued until a test completes, fails or discards it) and can
/// apply the "server truth" to the <see cref="FakeMailboxReadModel"/> when a test completes a command.
/// </summary>
internal sealed class FakeMailboxActions(FakeMailboxReadModel readModel) : IMailboxActions
{
    private readonly object _gate = new();
    private readonly List<ActionCall> _calls = [];
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<long, int> _countOverrides = [];
    private long _nextCommandId = 1;
    private int _listPendingCalls;

    /// <summary>Awaited at the start of every mutating call (blocks a call to test races).</summary>
    public Func<Task>? Gate { get; set; }

    /// <summary>Thrown by the next mutating calls while set (a queue rejection or a failure).</summary>
    public Exception? Failure { get; set; }

    /// <summary>Thrown by <see cref="ListPendingAsync"/> while set.</summary>
    public Exception? ListPendingFailure { get; set; }

    public IReadOnlyList<ActionCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public int ListPendingCalls
    {
        get
        {
            lock (_gate)
            {
                return _listPendingCalls;
            }
        }
    }

    /// <summary>Calls with the given name.</summary>
    public IReadOnlyList<ActionCall> CallsNamed(string name) => [.. Calls.Where(call => call.Name == name)];

    /// <summary>Makes <see cref="CountForEmptyAsync"/> answer a fixed number for a folder (otherwise the stored message count).</summary>
    public void OverrideEmptyCount(long folderId, int count)
    {
        lock (_gate)
        {
            _countOverrides[folderId] = count;
        }
    }

    /// <summary>Ids of all commands currently stored (pending or failed), oldest first.</summary>
    public IReadOnlyList<long> CommandIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Select(entry => entry.Action.CommandId)];
            }
        }
    }

    /// <summary>Marks a command as failed, as the executor does after the last attempt.</summary>
    public void Fail(long commandId, string lastError = "marker_server_text")
    {
        lock (_gate)
        {
            Entry entry = _entries.Single(candidate => candidate.Action.CommandId == commandId);
            entry.Action = entry.Action with { Status = MailCommandStatus.Failed, LastError = lastError };
        }
    }

    /// <summary>Server confirmed the command: it leaves the queue and the read model shows the new truth.</summary>
    public void Complete(long commandId)
    {
        Entry entry;
        lock (_gate)
        {
            entry = _entries.Single(candidate => candidate.Action.CommandId == commandId);
            _entries.Remove(entry);
        }

        entry.Apply?.Invoke();
    }

    public Task SetSeenAsync(IReadOnlyCollection<MailMessage> messages, bool seen, CancellationToken ct = default) =>
        RecordAsync("SetSeen", messages, null, seen, MailCommandKind.SetFlags, message =>
            readModel.SetFlags(message.Id, seen ? message.Flags | MessageFlags.Seen : message.Flags & ~MessageFlags.Seen));

    public Task SetFlaggedAsync(IReadOnlyCollection<MailMessage> messages, bool flagged, CancellationToken ct = default) =>
        RecordAsync("SetFlagged", messages, null, flagged, MailCommandKind.SetFlags, message =>
            readModel.SetFlags(message.Id, flagged ? message.Flags | MessageFlags.Flagged : message.Flags & ~MessageFlags.Flagged));

    public Task MoveToTrashAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken ct = default) =>
        RecordAsync("MoveToTrash", messages, null, null, MailCommandKind.MoveToTrash, message =>
        {
            MailFolder trash = readModel.ListFoldersAsync(message.AccountId).GetAwaiter().GetResult().Single(folder => folder.Role == FolderRole.Trash);
            readModel.MoveMessage(message.Id, trash);
        });

    public Task DeletePermanentlyAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken ct = default) =>
        RecordAsync("DeletePermanently", messages, null, null, MailCommandKind.DeletePermanently, message => readModel.RemoveMessage(message.Id));

    public Task MoveAsync(IReadOnlyCollection<MailMessage> messages, long targetFolderId, CancellationToken ct = default) =>
        RecordAsync("Move", messages, targetFolderId, null, MailCommandKind.Move, message =>
        {
            MailFolder target = readModel.ListFoldersAsync(message.AccountId).GetAwaiter().GetResult().Single(folder => folder.Id == targetFolderId);
            readModel.MoveMessage(message.Id, target);
        });

    /// <summary>When set, <see cref="EmptyFolderAsync"/> refuses with it regardless of the content (a forced refusal).</summary>
    public MailActionException? EmptyRefusal { get; set; }

    /// <summary>UIDVALIDITY the fake reports in every preview.</summary>
    public const uint PreviewUidValidity = 1000;

    public async Task<EmptyFolderPreview> PreviewEmptyAsync(long folderId, CancellationToken ct = default)
    {
        IReadOnlyList<MailMessage> stored = await readModel.ListMessagesAsync(new MessageQuery(FolderId: folderId, Limit: int.MaxValue), ct);
        int count = stored.Count;
        lock (_gate)
        {
            _previewCalls++;
            if (_countOverrides.TryGetValue(folderId, out int overridden))
            {
                count = overridden;
            }
        }

        // A count override adds synthetic UIDs above every real one so that the set still contains the stored UIDs.
        HashSet<uint> uids = [.. stored.Select(message => message.Uid)];
        for (uint extra = 0; uids.Count < count; extra++)
        {
            uids.Add(uint.MaxValue - extra);
        }

        return new EmptyFolderPreview(PreviewUidValidity, uids);
    }

    /// <summary>Number of <see cref="PreviewEmptyAsync"/> calls.</summary>
    public int PreviewCalls
    {
        get
        {
            lock (_gate)
            {
                return _previewCalls;
            }
        }
    }

    private int _previewCalls;

    /// <summary>Previews passed to <see cref="EmptyFolderAsync"/>, in call order (also refused ones).</summary>
    public IReadOnlyList<EmptyFolderPreview> ConfirmedPreviews
    {
        get
        {
            lock (_gate)
            {
                return [.. _confirmed];
            }
        }
    }

    private readonly List<EmptyFolderPreview> _confirmed = [];

    /// <summary>Like the product: refuses with <see cref="MailActionException"/> when a local UID is not among the confirmed ones.</summary>
    public async Task EmptyFolderAsync(long folderId, EmptyFolderPreview confirmed, CancellationToken ct = default)
    {
        IReadOnlyList<MailMessage> messages = await readModel.ListMessagesAsync(new MessageQuery(FolderId: folderId, Limit: int.MaxValue), ct);
        lock (_gate)
        {
            _confirmed.Add(confirmed);
        }

        if (EmptyRefusal is not null)
        {
            throw EmptyRefusal;
        }

        if (confirmed.UidValidity != PreviewUidValidity || messages.Any(message => !confirmed.Uids.Contains(message.Uid)))
        {
            throw new MailActionException("fixed refusal: a new mail arrived");
        }

        await RecordAsync("EmptyFolder", messages, folderId, null, MailCommandKind.DeletePermanently, message => readModel.RemoveMessage(message.Id));
    }

    public Task<IReadOnlyList<PendingAction>> ListPendingAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _listPendingCalls++;
            if (ListPendingFailure is not null)
            {
                throw ListPendingFailure;
            }

            return Task.FromResult<IReadOnlyList<PendingAction>>([.. _entries.Select(entry => entry.Action)]);
        }
    }

    public Task DiscardAsync(long commandId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _calls.Add(new ActionCall("Discard", [commandId]));
            _entries.RemoveAll(entry => entry.Action.CommandId == commandId);
        }

        return Task.CompletedTask;
    }

    private async Task RecordAsync(
        string name,
        IReadOnlyCollection<MailMessage> messages,
        long? folderId,
        bool? value,
        MailCommandKind kind,
        Action<MailMessage> apply)
    {
        Func<Task>? gate = Gate;
        if (gate is not null)
        {
            await gate();
        }

        lock (_gate)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            _calls.Add(new ActionCall(name, [.. messages.Select(message => message.Id)], folderId, value));
            foreach (IGrouping<(Guid AccountId, long FolderId), MailMessage> group in messages.GroupBy(message => (message.AccountId, message.FolderId)))
            {
                MailMessage[] groupMessages = [.. group];
                var action = new PendingAction(_nextCommandId++, group.Key.AccountId, kind, MailCommandStatus.Pending, [.. groupMessages.Select(message => message.Id)], null);
                _entries.Add(new Entry(action, () =>
                {
                    foreach (MailMessage message in groupMessages)
                    {
                        // Re-read: an earlier command may have changed the stored copy.
                        apply(readModel.Find(message.Id) ?? message);
                    }
                }));
            }
        }
    }

    private sealed class Entry(PendingAction action, Action? apply)
    {
        public PendingAction Action { get; set; } = action;

        public Action? Apply { get; } = apply;
    }
}
