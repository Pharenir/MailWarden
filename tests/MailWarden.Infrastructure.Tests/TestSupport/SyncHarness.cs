using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>Time provider with a fixed instant and UTC as local zone (deterministic "today" for the sync window).</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}

/// <summary>
/// Wires a <see cref="SyncEngine"/> to real in-memory SQLite stores, a real EML store in a temp directory and the
/// scripted <see cref="FakeMailServer"/>. No mocks for storage.
/// </summary>
internal sealed class SyncHarness : IAsyncDisposable
{
    /// <summary>Fixed "now" of all sync tests: 2026-10-31 12:00 UTC.</summary>
    public static readonly DateTimeOffset FixedNow = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory temp;

    private SyncHarness(StorageFixture storage, TempDirectory temp, Account account, int initialSyncDays)
    {
        Storage = storage;
        this.temp = temp;
        Account = account;
        Eml = new FileEmlStore(new DataPaths(temp.RootPath));
        SyncStates = new SqliteFolderSyncStateStore(storage.Database);
        Server = new FakeMailServer();
        Factory = new FakeMailTransportFactory(Server);
        Clock = new FixedTimeProvider(FixedNow);
        Engine = CreateEngine(new SyncOptions(initialSyncDays));
    }

    public StorageFixture Storage { get; }

    public Account Account { get; }

    public FileEmlStore Eml { get; }

    public SqliteFolderSyncStateStore SyncStates { get; }

    public FakeMailServer Server { get; }

    public FakeMailTransportFactory Factory { get; }

    public FixedTimeProvider Clock { get; }

    public SyncEngine Engine { get; private set; }

    public string EmlRoot => Path.Combine(temp.RootPath, "mail");

    /// <summary>Creates the harness with one stored account (provider Google by default).</summary>
    public static async Task<SyncHarness> CreateAsync(int initialSyncDays = 0, MailProvider provider = MailProvider.Google)
    {
        StorageFixture storage = await StorageFixture.CreateAsync();
        var temp = new TempDirectory();
        Account account = TestData.NewAccount("Account_A", provider: provider);
        await storage.Accounts.UpsertAsync(account);
        return new SyncHarness(storage, temp, account, initialSyncDays);
    }

    public SyncEngine CreateEngine(SyncOptions options) =>
        new(Factory, Storage.Accounts, Storage.Folders, Storage.Messages, Eml, SyncStates, options, Clock);

    /// <summary>Replaces <see cref="Engine"/> with one using other options (same stores and server).</summary>
    public void UseOptions(SyncOptions options) => Engine = CreateEngine(options);

    public Task<SyncReport> SyncAsync(CancellationToken ct = default) => Engine.SyncAccountAsync(Account.Id, ct);

    /// <summary>Runs a sync and clears the call log first, so assertions see only this run.</summary>
    public Task<SyncReport> SyncWithFreshLogAsync(CancellationToken ct = default)
    {
        Server.ClearLog();
        return SyncAsync(ct);
    }

    public async Task<MailFolder?> FindFolderAsync(string fullName) =>
        (await Storage.Folders.ListAsync(Account.Id)).SingleOrDefault(folder => folder.FullName == fullName);

    public async Task<MailFolder> GetFolderAsync(string fullName) =>
        await FindFolderAsync(fullName) ?? throw new InvalidOperationException($"Folder '{fullName}' is not stored.");

    /// <summary>All stored messages of a folder ordered by UID.</summary>
    public async Task<IReadOnlyList<MailMessage>> MessagesOfAsync(string fullName)
    {
        MailFolder folder = await GetFolderAsync(fullName);
        IReadOnlyList<MailMessage> listed = await Storage.Messages.ListAsync(
            new MessageQuery(FolderId: folder.Id, Limit: 10_000));
        return listed.OrderBy(message => message.Uid).ToList();
    }

    public async Task<uint[]> StoredUidsAsync(string fullName) =>
        (await MessagesOfAsync(fullName)).Select(message => message.Uid).ToArray();

    public async Task<FolderSyncState?> StateOfAsync(string fullName) =>
        await SyncStates.GetAsync((await GetFolderAsync(fullName)).Id);

    /// <summary>Reads the stored EML bytes of a message row.</summary>
    public async Task<byte[]> ReadEmlAsync(MailMessage message)
    {
        Assert.NotNull(message.EmlPath);
        await using Stream stream = await Eml.OpenReadAsync(message.EmlPath);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>Number of files below the mail root (0 when the root does not exist).</summary>
    public int EmlFileCount() =>
        Directory.Exists(EmlRoot) ? Directory.GetFiles(EmlRoot, "*", SearchOption.AllDirectories).Length : 0;

    public async ValueTask DisposeAsync()
    {
        await Storage.DisposeAsync();
        temp.Dispose();
    }
}
