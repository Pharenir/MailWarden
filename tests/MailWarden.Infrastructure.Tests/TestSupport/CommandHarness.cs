using MailWarden.Core.Accounts;
using MailWarden.Core.Commands;
using MailWarden.Infrastructure.Commands;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>A folder present both on the fake server and in the local store.</summary>
internal sealed record SeededFolder(MailFolder Folder, FakeMailFolder Server);

/// <summary>
/// Real in-memory SQLite stores, real EML store, the scripted fake server and the real queue + executor. Folders and
/// messages are seeded on both sides with identical UIDs, as a sync pass would leave them. All data is artificial.
/// </summary>
internal sealed class CommandHarness : IAsyncDisposable
{
    private readonly SyncHarness sync;

    private CommandHarness(SyncHarness sync, int maxAttempts)
    {
        this.sync = sync;
        Messages = new FaultyMessageStore(sync.Storage.Messages);
        Queue = new SqliteMailCommandQueue(sync.Storage.Database, sync.Clock);
        Executor = CreateExecutor(maxAttempts);
    }

    public SyncHarness Sync => sync;

    public Account Account => sync.Account;

    public FakeMailServer Server => sync.Server;

    public FixedTimeProvider Clock => sync.Clock;

    public StorageFixture Storage => sync.Storage;

    /// <summary>The message store the executor sees; can inject write failures.</summary>
    public FaultyMessageStore Messages { get; }

    public FaultyEmlStore FaultyEml => sync.FaultyEml;

    public SqliteMailCommandQueue Queue { get; }

    public CommandExecutor Executor { get; private set; }

    /// <summary>Creates the harness with one stored account (provider Google).</summary>
    public static async Task<CommandHarness> CreateAsync(int maxAttempts = 5) =>
        new(await SyncHarness.CreateAsync(), maxAttempts);

    public CommandExecutor CreateExecutor(int maxAttempts) =>
        new(
            sync.Factory,
            sync.Storage.Accounts,
            sync.Storage.Folders,
            Messages,
            sync.FaultyEml,
            Queue,
            new CommandOptions(maxAttempts),
            sync.Clock);

    public void UseMaxAttempts(int maxAttempts) => Executor = CreateExecutor(maxAttempts);

    /// <summary>Adds a folder on the server and locally (same role, same UIDVALIDITY), with <paramref name="messageCount"/> messages.</summary>
    public async Task<SeededFolder> SeedFolderAsync(
        string fullName,
        FolderRole role,
        int messageCount = 0,
        uint uidValidity = 100)
    {
        FakeMailFolder serverFolder = Server.AddFolder(fullName, ServerFlagsFor(role), uidValidity);
        MailFolder folder = await sync.Storage.Folders.UpsertAsync(
            TestData.NewFolder(Account.Id, fullName, role, uidValidity));
        var seeded = new SeededFolder(folder, serverFolder);
        for (int index = 0; index < messageCount; index++)
        {
            await AddMessageAsync(seeded);
        }

        return seeded;
    }

    /// <summary>Adds one message (next server UID) on the server and in the local store including its EML file.</summary>
    public async Task<uint> AddMessageAsync(SeededFolder folder, MessageFlags flags = MessageFlags.None)
    {
        byte[] content = SyncTestEml.Plain($"subject_{folder.Folder.FullName}_{folder.Server.Messages.Count}", "command body");
        uint uid = folder.Server.AddMessage(content, flags);
        string emlPath = sync.FaultyEml.RelativePathFor(Account.Id, folder.Folder.Id, uid);
        await sync.FaultyEml.WriteAsync(emlPath, new MemoryStream(content));
        await sync.Storage.Messages.UpsertAsync(
            TestData.NewMessage(Account.Id, folder.Folder.Id, uid, flags: flags, emlPath: emlPath),
            bodyText: null);
        return uid;
    }

    public Task<CommandRunReport> ExecuteAsync(CancellationToken ct = default) =>
        Executor.ExecutePendingAsync(Account.Id, ct);

    public Task<IReadOnlyList<MailCommand>> ListAsync() => Queue.ListAsync(Account.Id);

    public async Task<MailCommand> SingleCommandAsync() => Assert.Single(await ListAsync());

    /// <summary>Local UIDs of a folder, ascending.</summary>
    public async Task<uint[]> LocalUidsAsync(MailFolder folder) =>
        (await Storage.Messages.GetUidFlagsAsync(folder.Id)).Keys.OrderBy(uid => uid).ToArray();

    /// <summary>Local EML paths of the given UIDs (null entries omitted), captured before a command runs.</summary>
    public async Task<string[]> EmlPathsAsync(MailFolder folder, params uint[] uids)
    {
        IReadOnlyDictionary<uint, string?> all = await Storage.Messages.GetUidEmlPathsAsync(folder.Id);
        return uids.Where(all.ContainsKey).Select(uid => all[uid]).OfType<string>().ToArray();
    }

    public bool EmlExists(string relativePath) => sync.Eml.Exists(relativePath);

    public Task<SyncReport> RunSyncAsync() => sync.SyncAsync();

    /// <summary>Sets the clock forward and returns the new instant.</summary>
    public DateTimeOffset Advance(TimeSpan span)
    {
        Clock.Now += span;
        return Clock.Now;
    }

    public ValueTask DisposeAsync() => sync.DisposeAsync();

    private static ServerFolderFlags ServerFlagsFor(FolderRole role) => role switch
    {
        FolderRole.Inbox => ServerFolderFlags.Inbox,
        FolderRole.Junk => ServerFolderFlags.Junk,
        FolderRole.Trash => ServerFolderFlags.Trash,
        FolderRole.Archive => ServerFolderFlags.Archive,
        FolderRole.Sent => ServerFolderFlags.Sent,
        _ => ServerFolderFlags.None,
    };

    /// <summary>
    /// Safety check for every run: each recorded Expunge call is in a folder of <paramref name="allowedByFolder"/>
    /// (the folder selected at call time) and only carries UIDs listed for that folder.
    /// </summary>
    public void AssertExpungesOnly(IReadOnlyDictionary<string, uint[]> allowedByFolder)
    {
        foreach (CommandCall call in Server.CommandCalls.Where(call => call.Kind == CommandCall.Expunge))
        {
            Assert.True(allowedByFolder.TryGetValue(call.Folder, out uint[]? allowed), $"Expunge in unexpected folder {call.Folder}");
            Assert.All(call.Uids, uid => Assert.Contains(uid, allowed!));
        }
    }
}

/// <summary>Storage-only fixture for queue tests: one account, folders per role, real queue, controllable clock.</summary>
internal sealed class CommandQueueFixture : IAsyncDisposable
{
    private CommandQueueFixture(StorageFixture storage, Account account, FixedTimeProvider clock)
    {
        Storage = storage;
        Account = account;
        Clock = clock;
        Queue = new SqliteMailCommandQueue(storage.Database, clock);
    }

    public StorageFixture Storage { get; }

    public Account Account { get; }

    public FixedTimeProvider Clock { get; }

    public SqliteMailCommandQueue Queue { get; }

    public MailFolder Inbox { get; private set; } = null!;

    public MailFolder Junk { get; private set; } = null!;

    public MailFolder Trash { get; private set; } = null!;

    public MailFolder Archive { get; private set; } = null!;

    /// <summary>Creates account and folders Inbox (local UIDs 1..9), Junk, Trash, Archive (UIDVALIDITY 1000 each).</summary>
    public static async Task<CommandQueueFixture> CreateAsync(bool withTrash = true)
    {
        StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        var fixture = new CommandQueueFixture(storage, account, new FixedTimeProvider(SyncHarness.FixedNow))
        {
            Inbox = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX"),
            Junk = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk"),
            Archive = await storage.AddFolderAsync(account.Id, FolderRole.Archive, "Archive"),
        };
        if (withTrash)
        {
            fixture.Trash = await storage.AddFolderAsync(account.Id, FolderRole.Trash, "Trash");
        }

        await fixture.SeedAsync(fixture.Inbox, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        return fixture;
    }

    /// <summary>Stores local messages with the given UIDs (enqueue requires locally stored UIDs).</summary>
    public async Task SeedAsync(MailFolder folder, params uint[] uids)
    {
        foreach (uint uid in uids)
        {
            await Storage.AddMessageAsync(folder, uid);
        }
    }

    public async Task<long> CommandRowCountAsync() => await Storage.CountRowsAsync("mail_commands");

    public ValueTask DisposeAsync() => Storage.DisposeAsync();
}
