using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;
using MailWarden.Infrastructure.Mime;
using MailWarden.Infrastructure.Sync;

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
    public void Raise(AccountSyncStatus status) => StatusChanged?.Invoke(this, status);

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
        string? emlPath = "eml/sample.eml")
    {
        lock (_gate)
        {
            long id = _nextMessageId++;
            MailMessage message = new(
                id,
                folder.AccountId,
                folder.Id,
                _nextUid++,
                null,
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

        return result.OrderByDescending(message => message.Date).ThenByDescending(message => message.Id).ToList();
    }
}

/// <summary>Builders for artificial message content. All values are synthetic.</summary>
internal static class TestContent
{
    public static MailContent Plain(string subject, string text, IReadOnlyList<MailAttachmentInfo>? attachments = null) =>
        new(
            subject,
            "Sender A <sender_a@example.test>",
            ["Recipient B <recipient_b@example.test>"],
            ["Copy D <copy_d@example.test>"],
            new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.FromHours(2)),
            null,
            text,
            new Dictionary<string, string>(),
            attachments ?? []);

    public static MailContent Html(string subject, string html, IReadOnlyList<MailAttachmentInfo>? attachments = null) =>
        Plain(subject, "plain text", attachments) with { Html = html };
}
