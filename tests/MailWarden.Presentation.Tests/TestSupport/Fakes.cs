using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;
using MailWarden.Infrastructure.Mime;
using MailWarden.Infrastructure.Sync;
using MimeKit;

namespace MailWarden.Presentation.Tests.TestSupport;

/// <summary>
/// Dispatcher fake. By default posted actions run inline; with <see cref="Queueing"/> they wait for
/// <see cref="Pump"/>, which makes the marshalling observable.
/// </summary>
internal sealed class FakeUiDispatcher : IUiDispatcher
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();
    private int _postCount;

    /// <summary>When true, <see cref="Post"/> only queues the action.</summary>
    public bool Queueing { get; set; }

    /// <summary>When set, <see cref="Post"/> throws this exception instead of running the action.</summary>
    public Exception? FailureOnPost { get; set; }

    public int PostCount
    {
        get
        {
            lock (_gate)
            {
                return _postCount;
            }
        }
    }

    public void Post(Action action)
    {
        lock (_gate)
        {
            _postCount++;
        }

        if (FailureOnPost is not null)
        {
            throw FailureOnPost;
        }

        if (Queueing)
        {
            lock (_gate)
            {
                _queue.Enqueue(action);
            }

            return;
        }

        action();
    }

    /// <summary>Runs all queued actions on the calling thread.</summary>
    public void Pump()
    {
        while (true)
        {
            Action? next;
            lock (_gate)
            {
                next = _queue.Count > 0 ? _queue.Dequeue() : null;
            }

            if (next is null)
            {
                return;
            }

            next();
        }
    }
}

/// <summary>Records every critical-log line.</summary>
internal sealed class FakeCriticalLog : ICriticalLog
{
    private readonly object _gate = new();
    private readonly List<(string Level, string Source, string Message)> _entries = [];

    public IReadOnlyList<(string Level, string Source, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>All fields of all entries as one text, for "must never contain" checks.</summary>
    public string AllText
    {
        get
        {
            lock (_gate)
            {
                return string.Join("\n", _entries.Select(entry => $"{entry.Level}|{entry.Source}|{entry.Message}"));
            }
        }
    }

    public void Write(string level, string source, string message)
    {
        lock (_gate)
        {
            _entries.Add((level, source, message));
        }
    }
}

/// <summary>Settings store fake with injectable failures.</summary>
internal sealed class FakeUiSettingsStore : IUiSettingsStore
{
    public UiSettings Stored { get; set; } = UiSettings.Default;

    public Exception? LoadFailure { get; set; }

    public Exception? SaveFailure { get; set; }

    public List<UiSettings> Saved { get; } = [];

    public UiSettings Load() => LoadFailure is null ? Stored : throw LoadFailure;

    public void Save(UiSettings settings)
    {
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        Saved.Add(settings);
        Stored = settings;
    }
}

/// <summary>Controller fake. Records only the two commands the contract has; raises status events on demand.</summary>
internal sealed class FakeMailboxController : IMailboxController
{
    private readonly object _gate = new();
    private readonly List<(string Kind, Guid AccountId)> _calls = [];

    public event EventHandler<AccountSyncStatus>? StatusChanged;

    public event EventHandler<ClassificationPassSummary>? ClassificationPassCompleted;

    public IReadOnlyList<AccountSyncStatus> CurrentStatus { get; set; } = [];

    public IReadOnlyList<(string Kind, Guid AccountId)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public void TriggerAccount(Guid accountId)
    {
        lock (_gate)
        {
            _calls.Add(("Trigger", accountId));
        }
    }

    public void RetryAccount(Guid accountId)
    {
        lock (_gate)
        {
            _calls.Add(("Retry", accountId));
        }
    }

    /// <summary>Raises the event on the calling thread.</summary>
    public void Raise(AccountSyncStatus status)
    {
        // Like the real controller: CurrentStatus already shows the new status when the event fires.
        lock (_gate)
        {
            CurrentStatus = [.. CurrentStatus.Where(existing => existing.AccountId != status.AccountId), status];
        }

        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Raises the classification event on the calling thread.</summary>
    public void RaiseClassification(ClassificationPassSummary summary) => ClassificationPassCompleted?.Invoke(this, summary);

    /// <summary>Raises the classification event on a thread-pool thread, as the cycle of the real runtime does.</summary>
    public Task RaiseClassificationOnPoolAsync(ClassificationPassSummary summary) => Task.Run(() => RaiseClassification(summary));

    /// <summary>Raises the event on a thread-pool thread, as the real service does.</summary>
    public Task RaiseOnPoolAsync(AccountSyncStatus status) => Task.Run(() => Raise(status));

    public static AccountSyncStatus Status(
        Guid accountId, AccountSyncState state, SyncProgress? progress = null, string? lastErrorType = null) =>
        new(accountId, state, LastSuccessAt: null, lastErrorType, NextRunAt: null, progress);
}

/// <summary>
/// In-memory read model. Filtering follows <see cref="MessageQuery"/> (newest first, Limit/Offset) so that paging tests
/// run against realistic behaviour. Has no write member besides the seeding helpers used by tests.
/// </summary>
internal sealed class FakeMailboxReadModel : IMailboxReadModel
{
    private static readonly DateTimeOffset BaseDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _gate = new();
    private readonly List<Account> _accounts = [];
    private readonly List<MailFolder> _folders = [];
    private readonly List<MailMessage> _messages = [];
    private readonly Dictionary<long, MailContent?> _contents = [];
    private readonly List<MessageQuery> _listQueries = [];
    private long _nextFolderId = 1;
    private long _nextMessageId = 1;
    private uint _nextUid = 1;
    private int _listAccountsCalls;
    private int _countUnreadCalls;
    private int _readCalls;

    /// <summary>Awaited before <see cref="ListMessagesAsync"/> answers; arguments are the query and the 1-based call number.</summary>
    public Func<MessageQuery, int, Task>? ListGate { get; set; }

    /// <summary>Replaces <see cref="ReadContentAsync"/>; arguments are the message and the 1-based call number.</summary>
    public Func<MailMessage, int, Task<MailContent?>>? ReadBehavior { get; set; }

    /// <summary>When set, <see cref="GetFolderRoleAsync"/> throws it (read failure of the folder role).</summary>
    public Exception? FolderRoleFailure { get; set; }

    private readonly Dictionary<long, FolderRole?> _roleOverrides = [];

    /// <summary>Overrides the role the read model reports for a folder (null = unknown folder).</summary>
    public void SetFolderRole(long folderId, FolderRole? role)
    {
        lock (_gate)
        {
            _roleOverrides[folderId] = role;
        }
    }

    public int ListMessagesCalls
    {
        get
        {
            lock (_gate)
            {
                return _listQueries.Count;
            }
        }
    }

    public IReadOnlyList<MessageQuery> ListQueries
    {
        get
        {
            lock (_gate)
            {
                return [.. _listQueries];
            }
        }
    }

    public int ListAccountsCalls
    {
        get
        {
            lock (_gate)
            {
                return _listAccountsCalls;
            }
        }
    }

    public int CountUnreadCalls
    {
        get
        {
            lock (_gate)
            {
                return _countUnreadCalls;
            }
        }
    }

    public int ReadCalls
    {
        get
        {
            lock (_gate)
            {
                return _readCalls;
            }
        }
    }

    // ---- seeding -------------------------------------------------------------------------------------------

    public Account AddAccount(string displayName = "Account_A", string? emailAddress = null)
    {
        Account account = new(Guid.NewGuid(), displayName, emailAddress ?? $"user_{Guid.NewGuid():N}@example.test", MailProvider.GenericImap);
        lock (_gate)
        {
            _accounts.Add(account);
        }

        return account;
    }

    public MailFolder AddFolder(Account account, FolderRole role, string? fullName = null)
    {
        lock (_gate)
        {
            MailFolder folder = new(_nextFolderId++, account.Id, fullName ?? $"folder_{role}", role, 1000);
            _folders.Add(folder);
            return folder;
        }
    }

    public MailMessage AddMessage(
        MailFolder folder,
        string subject = "sample_subject",
        DateTimeOffset? date = null,
        MessageFlags flags = MessageFlags.None,
        string fromName = "Sender A",
        string fromAddress = "sender_a@example.test",
        string preview = "sample_preview",
        bool hasAttachments = false,
        string? emlPath = "eml/sample.eml",
        string? messageId = null)
    {
        lock (_gate)
        {
            long id = _nextMessageId++;
            MailMessage message = new(
                id,
                folder.AccountId,
                folder.Id,
                _nextUid++,
                messageId,
                subject,
                fromAddress,
                fromName,
                date ?? BaseDate.AddMinutes(id),
                flags,
                hasAttachments,
                1234,
                emlPath,
                preview);
            _messages.Add(message);
            return message;
        }
    }

    public void RemoveMessage(long messageId)
    {
        lock (_gate)
        {
            _messages.RemoveAll(message => message.Id == messageId);
        }
    }

    /// <summary>Server truth after a command: replaces the flags of a stored message.</summary>
    public void SetFlags(long messageId, MessageFlags flags)
    {
        lock (_gate)
        {
            int index = _messages.FindIndex(message => message.Id == messageId);
            if (index >= 0)
            {
                _messages[index] = _messages[index] with { Flags = flags };
            }
        }
    }

    /// <summary>Server truth after a move: the message now lives in <paramref name="target"/> (same id, new UID).</summary>
    public void MoveMessage(long messageId, MailFolder target)
    {
        lock (_gate)
        {
            int index = _messages.FindIndex(message => message.Id == messageId);
            if (index >= 0)
            {
                _messages[index] = _messages[index] with { FolderId = target.Id, AccountId = target.AccountId, Uid = _nextUid++ };
            }
        }
    }

    /// <summary>Current stored copy of a message (null when gone).</summary>
    public MailMessage? Find(long messageId)
    {
        lock (_gate)
        {
            return _messages.FirstOrDefault(message => message.Id == messageId);
        }
    }

    public void SetContent(long messageId, MailContent? content)
    {
        lock (_gate)
        {
            _contents[messageId] = content;
        }
    }

    // ---- IMailboxReadModel ---------------------------------------------------------------------------------

    public Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _listAccountsCalls++;
            return Task.FromResult<IReadOnlyList<Account>>([.. _accounts]);
        }
    }

    /// <summary>Awaited by <see cref="GetFolderRoleAsync"/> before it answers (blocks a view model inside an action).</summary>
    public Func<Task>? RoleGate { get; set; }

    /// <summary>Number of times <see cref="RoleGate"/> was entered.</summary>
    public int RoleGateEntered => Volatile.Read(ref _roleGateEntered);

    private int _roleGateEntered;

    public async Task<FolderRole?> GetFolderRoleAsync(long folderId, CancellationToken ct = default)
    {
        Func<Task>? roleGate = RoleGate;
        if (roleGate is not null)
        {
            Interlocked.Increment(ref _roleGateEntered);
            await roleGate();
        }

        if (FolderRoleFailure is not null)
        {
            throw FolderRoleFailure;
        }

        lock (_gate)
        {
            if (_roleOverrides.TryGetValue(folderId, out FolderRole? overridden))
            {
                return overridden;
            }

            // Known folders report the role they were added with (Inbox in most tests); unknown folders report null.
            MailFolder? folder = _folders.FirstOrDefault(candidate => candidate.Id == folderId);
            return folder?.Role;
        }
    }

    public Task<IReadOnlyList<MailFolder>> ListFoldersAsync(Guid accountId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<MailFolder>>([.. _folders.Where(folder => folder.AccountId == accountId)]);
        }
    }

    public async Task<IReadOnlyList<MailMessage>> ListMessagesAsync(MessageQuery query, CancellationToken ct = default)
    {
        int number;
        lock (_gate)
        {
            _listQueries.Add(query);
            number = _listQueries.Count;
        }

        Func<MessageQuery, int, Task>? gate = ListGate;
        if (gate is not null)
        {
            await gate(query, number);
        }

        lock (_gate)
        {
            return [.. Matching(query).Skip(query.Offset).Take(query.Limit)];
        }
    }

    public Task<int> CountUnreadAsync(MessageQuery query, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _countUnreadCalls++;
            return Task.FromResult(Matching(query).Count(message => (message.Flags & MessageFlags.Seen) == 0));
        }
    }

    public async Task<MailContent?> ReadContentAsync(MailMessage message, CancellationToken ct = default)
    {
        int number;
        Func<MailMessage, int, Task<MailContent?>>? behavior;
        MailContent? configured = null;
        bool hasConfigured;
        lock (_gate)
        {
            number = ++_readCalls;
            behavior = ReadBehavior;
            hasConfigured = _contents.TryGetValue(message.Id, out configured);
        }

        if (behavior is not null)
        {
            return await behavior(message, number);
        }

        if (hasConfigured)
        {
            return configured;
        }

        return message.EmlPath is null ? null : TestContent.Plain(message.Subject, $"body of {message.Subject}");
    }

    // ---- attachments (AP-04c) ------------------------------------------------------------------------------

    private readonly Dictionary<(long MessageId, int Index), byte[]> _attachmentBytes = [];
    private readonly List<(long MessageId, int Index)> _copyCalls = [];

    /// <summary>
    /// Replaces <see cref="CopyAttachmentAsync"/>; arguments are the message, the index, the destination and the token.
    /// The behaviour writes to the destination itself or throws.
    /// </summary>
    public Func<MailMessage, int, Stream, CancellationToken, Task>? CopyBehavior { get; set; }

    /// <summary>Bytes the fake writes for an attachment without configured content.</summary>
    public static byte[] DefaultAttachmentBytes(int index) => System.Text.Encoding.UTF8.GetBytes($"attachment_bytes_{index}");

    /// <summary>Sets the decoded content of one attachment.</summary>
    public void SetAttachmentBytes(long messageId, int index, byte[] bytes)
    {
        lock (_gate)
        {
            _attachmentBytes[(messageId, index)] = bytes;
        }
    }

    /// <summary>Message id and index of every <see cref="CopyAttachmentAsync"/> call, in order.</summary>
    public IReadOnlyList<(long MessageId, int Index)> CopyCalls
    {
        get
        {
            lock (_gate)
            {
                return [.. _copyCalls];
            }
        }
    }

    public async Task CopyAttachmentAsync(MailMessage message, int attachmentIndex, Stream destination, CancellationToken ct = default)
    {
        Func<MailMessage, int, Stream, CancellationToken, Task>? behavior;
        byte[]? configured;
        lock (_gate)
        {
            _copyCalls.Add((message.Id, attachmentIndex));
            behavior = CopyBehavior;
            _attachmentBytes.TryGetValue((message.Id, attachmentIndex), out configured);
        }

        if (behavior is not null)
        {
            await behavior(message, attachmentIndex, destination, ct);
            return;
        }

        await destination.WriteAsync(configured ?? DefaultAttachmentBytes(attachmentIndex), ct);
    }

    /// <summary>Awaited before <see cref="SearchAsync"/> answers; arguments are the text and the 1-based call number.</summary>
    public Func<string, int, Task>? SearchGate { get; set; }

    /// <summary>When set, <see cref="SearchAsync"/> throws it (stands for an invalid FTS expression).</summary>
    public Exception? SearchFailure { get; set; }

    /// <summary>Texts passed to <see cref="SearchAsync"/>.</summary>
    public IReadOnlyList<(string Text, int Limit)> SearchCalls
    {
        get
        {
            lock (_gate)
            {
                return [.. _searchCalls];
            }
        }
    }

    private readonly List<(string Text, int Limit)> _searchCalls = [];

    /// <summary>Case-insensitive substring search over subject, sender and preview, newest first, honouring the limit.</summary>
    public async Task<IReadOnlyList<MailMessage>> SearchAsync(string text, int limit, CancellationToken ct = default)
    {
        int number;
        lock (_gate)
        {
            _searchCalls.Add((text, limit));
            number = _searchCalls.Count;
        }

        Func<string, int, Task>? gate = SearchGate;
        if (gate is not null)
        {
            await gate(text, number);
        }

        if (SearchFailure is not null)
        {
            throw SearchFailure;
        }

        lock (_gate)
        {
            return
            [
                .. _messages
                    .Where(message => message.Subject.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || message.FromName.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || message.TextPreview.Contains(text, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(message => message.Date)
                    .ThenByDescending(message => message.Id)
                    .Take(limit),
            ];
        }
    }

    // Callers hold _gate.
    private IEnumerable<MailMessage> Matching(MessageQuery query)
    {
        IEnumerable<MailMessage> result = _messages;
        if (query.FolderId is { } folderId)
        {
            result = result.Where(message => message.FolderId == folderId);
        }
        else
        {
            if (query.Role is { } role)
            {
                HashSet<long> folderIds = [.. _folders.Where(folder => folder.Role == role).Select(folder => folder.Id)];
                result = result.Where(message => folderIds.Contains(message.FolderId));
            }

            if (query.AccountId is { } accountId)
            {
                result = result.Where(message => message.AccountId == accountId);
            }
        }

        if (query.ExcludedMessageIds is { Count: > 0 } excluded)
        {
            HashSet<long> excludedSet = [.. excluded];
            result = result.Where(message => !excludedSet.Contains(message.Id));
        }

        return result.OrderByDescending(message => message.Date).ThenByDescending(message => message.Id).ToList();
    }
}

/// <summary>
/// Builders for artificial message content. The content is produced by the real <see cref="MailContentReader"/> from a
/// generated message, so every member (including parsed sender mailboxes) is consistent with the product's own reader.
/// All values are synthetic.
/// </summary>
internal static class TestContent
{
    public static readonly (string Name, string Address)[] DefaultSender = [("Sender A", "sender_a@example.test")];

    public static MailContent Plain(
        string subject,
        string text,
        IReadOnlyList<MailAttachmentInfo>? attachments = null,
        (string Name, string Address)[]? from = null)
    {
        var message = new MimeMessage { Subject = "placeholder", Date = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.FromHours(2)) };
        foreach ((string name, string address) in from ?? DefaultSender)
        {
            message.From.Add(new MailboxAddress(name, address));
        }

        message.To.Add(new MailboxAddress("Recipient B", "recipient_b@example.test"));
        message.Cc.Add(new MailboxAddress("Copy D", "copy_d@example.test"));
        message.Body = new TextPart("plain") { Text = text };
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        MailContent read = MailContentReader.ReadAsync(stream).GetAwaiter().GetResult();

        // Subject and attachments are set directly: tests need hostile sizes and unclean texts the reader would not keep.
        return read with { Subject = subject, Attachments = attachments ?? [] };
    }

    public static MailContent Html(string subject, string html, IReadOnlyList<MailAttachmentInfo>? attachments = null) =>
        Plain(subject, "plain text", attachments) with { Html = html };
}

/// <summary>
/// Attachment host fake: the save dialog answers <see cref="SaveTarget"/> (null = cancelled), the launch answers
/// <see cref="OpenResult"/>. For every launch it keeps the path and a snapshot of the file content at call time.
/// </summary>
internal sealed class FakeAttachmentHost : IAttachmentHost
{
    private readonly object _gate = new();
    private readonly List<string> _proposedNames = [];
    private readonly List<string> _openedPaths = [];
    private readonly List<byte[]?> _openedContents = [];

    /// <summary>Path the dialog returns; null stands for a cancelled dialog.</summary>
    public string? SaveTarget { get; set; }

    /// <summary>When true the dialog creates an empty file at the target, as the Windows save picker does.</summary>
    public bool CreateEmptyTarget { get; set; } = true;

    /// <summary>Replaces the dialog answer; the argument is the proposed name.</summary>
    public Func<string, Task<string?>>? PickBehavior { get; set; }

    /// <summary>Answer of <see cref="OpenWithDefaultProgramAsync"/>.</summary>
    public bool OpenResult { get; set; } = true;

    public IReadOnlyList<string> ProposedNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _proposedNames];
            }
        }
    }

    public IReadOnlyList<string> OpenedPaths
    {
        get
        {
            lock (_gate)
            {
                return [.. _openedPaths];
            }
        }
    }

    /// <summary>File content at the moment of each launch (null when the file did not exist then).</summary>
    public IReadOnlyList<byte[]?> OpenedContents
    {
        get
        {
            lock (_gate)
            {
                return [.. _openedContents];
            }
        }
    }

    public async Task<string?> PickSaveTargetAsync(string proposedFileName, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _proposedNames.Add(proposedFileName);
        }

        string? target = PickBehavior is { } behavior ? await behavior(proposedFileName) : SaveTarget;
        if (target is not null && CreateEmptyTarget)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, [], ct);
        }

        return target;
    }

    public async Task<bool> OpenWithDefaultProgramAsync(string fullPath)
    {
        byte[]? content = File.Exists(fullPath) ? await File.ReadAllBytesAsync(fullPath) : null;
        lock (_gate)
        {
            _openedPaths.Add(fullPath);
            _openedContents.Add(content);
        }

        return OpenResult;
    }
}
