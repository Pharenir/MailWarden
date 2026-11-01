using MailWarden.Core.Commands;
using MailWarden.Infrastructure.Commands;
using MailWarden.Infrastructure.Composition;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>
/// AP-04b D1: real file-based SQLite (two connections: one for seeding and inspection, one owned by the actions),
/// the real command queue, and a recording trigger callback. The single place that knows how <see cref="MailboxActions"/>
/// is constructed (assumed: database, trigger callback, clock; see test-plan question Q1). All data is artificial.
/// </summary>
internal sealed class ActionsHarness : IAsyncDisposable
{
    private readonly TempDirectory temp;
    private readonly SqliteDatabase actionsDatabase;
    private readonly object gate = new();
    private readonly List<Guid> triggered = [];

    private ActionsHarness(TempDirectory temp, SqliteDatabase seedDatabase, SqliteDatabase actionsDatabase)
    {
        this.temp = temp;
        SeedDatabase = seedDatabase;
        this.actionsDatabase = actionsDatabase;
        Accounts = new SqliteAccountStore(seedDatabase);
        Folders = new SqliteFolderStore(seedDatabase);
        Messages = new SqliteMessageStore(seedDatabase);
        Clock = new FixedTimeProvider(SyncHarness.FixedNow);
        Queue = new SqliteMailCommandQueue(seedDatabase, Clock);
        Actions = new MailboxActions(actionsDatabase, RecordTrigger, Clock);
    }

    public SqliteDatabase SeedDatabase { get; }

    public SqliteAccountStore Accounts { get; }

    public SqliteFolderStore Folders { get; }

    public SqliteMessageStore Messages { get; }

    public FixedTimeProvider Clock { get; }

    /// <summary>Queue on the seeding connection, used to inspect and to mark commands as failed.</summary>
    public SqliteMailCommandQueue Queue { get; }

    public IMailboxActions Actions { get; }

    /// <summary>Accounts passed to the trigger callback, in call order.</summary>
    public IReadOnlyList<Guid> Triggered
    {
        get
        {
            lock (gate)
            {
                return [.. triggered];
            }
        }
    }

    public static async Task<ActionsHarness> CreateAsync()
    {
        var temp = new TempDirectory();
        string path = Path.Combine(temp.RootPath, "actions-test.db");
        SqliteDatabase seed = SqliteDatabase.ForFile(path);
        await seed.InitializeAsync();
        SqliteDatabase owned = SqliteDatabase.ForFile(path);
        await owned.InitializeAsync();
        return new ActionsHarness(temp, seed, owned);
    }

    public async Task<Account> AddAccountAsync(string displayName = "Account_A")
    {
        Account account = TestData.NewAccount(displayName);
        await Accounts.UpsertAsync(account);
        return account;
    }

    public Task<MailFolder> AddFolderAsync(Account account, FolderRole role, string? fullName = null, uint uidValidity = 1000) =>
        Folders.UpsertAsync(TestData.NewFolder(account.Id, fullName ?? $"folder_{role}", role, uidValidity));

    /// <summary>Stores <paramref name="count"/> messages with UIDs <paramref name="firstUid"/>.. and returns them as stored.</summary>
    public async Task<IReadOnlyList<MailMessage>> AddMessagesAsync(MailFolder folder, int count, uint firstUid = 1, MessageFlags flags = MessageFlags.None)
    {
        List<MailMessage> stored = [];
        for (uint uid = firstUid; uid < firstUid + (uint)count; uid++)
        {
            stored.Add(await Messages.UpsertAsync(TestData.NewMessage(folder.AccountId, folder.Id, uid, $"subject_{folder.Id}_{uid}", flags: flags), null));
        }

        return stored;
    }

    public Task<IReadOnlyList<MailCommand>> CommandsAsync(Account account) => Queue.ListAsync(account.Id);

    public async Task<long> CommandCountAsync(params Account[] accounts)
    {
        long total = 0;
        foreach (Account account in accounts)
        {
            total += (await Queue.ListAsync(account.Id)).Count;
        }

        return total;
    }

    public async ValueTask DisposeAsync()
    {
        await actionsDatabase.DisposeAsync();
        await SeedDatabase.DisposeAsync();
        temp.Dispose();
    }

    private void RecordTrigger(Guid accountId)
    {
        lock (gate)
        {
            triggered.Add(accountId);
        }
    }
}
