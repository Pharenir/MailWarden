using MailWarden.Core.Accounts;
using MailWarden.Core.Classification;
using MailWarden.Core.Model;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Classification;
using MailWarden.Infrastructure.Commands;
using MailWarden.Infrastructure.Storage;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// Per-account composition for the <see cref="Background.MailboxService"/>: one own database connection per account cycle
/// and one per IDLE watcher. Token providers and credential store are shared (the credential store serialises its file
/// access; MSAL keeps one application per provider). Settings and policy are the ones the runtime loaded at start.
/// </summary>
internal sealed class AccountPipelines(
    DataPaths paths,
    RuntimeConnections connections,
    IReadOnlyList<ITokenProvider> tokenProviders,
    TimeProvider timeProvider,
    ClassifierSettings classifierSettings,
    ClassificationPolicy classificationPolicy)
    : IMailIdleWatcherFactory
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, AccountCycle> cycles = [];
    private readonly Dictionary<Guid, MailKitIdleWatcherFactory> watcherFactories = [];

    /// <summary>Runs the queued commands of an account.</summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The report of the run.</returns>
    public async Task<CommandRunReport> ExecuteCommandsAsync(Guid accountId, CancellationToken ct)
    {
        AccountCycle cycle = await GetCycleAsync(accountId, ct).ConfigureAwait(false);
        return await cycle.Executor.ExecutePendingAsync(accountId, ct).ConfigureAwait(false);
    }

    /// <summary>Runs one sync pass of an account.</summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="progress">Receives the progress of the pass.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The report of the pass.</returns>
    public async Task<SyncReport> SyncAccountAsync(Guid accountId, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        AccountCycle cycle = await GetCycleAsync(accountId, ct).ConfigureAwait(false);
        return await cycle.Engine.SyncAccountAsync(accountId, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Runs one classification pass of an account.</summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The report of the pass.</returns>
    public async Task<ClassificationPassReport> ClassifyAccountAsync(Guid accountId, CancellationToken ct)
    {
        AccountCycle cycle = await GetCycleAsync(accountId, ct).ConfigureAwait(false);
        return await cycle.Pass.RunAsync(accountId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IMailIdleWatcher> ConnectAsync(Account account, CancellationToken ct = default)
    {
        MailKitIdleWatcherFactory factory = await GetWatcherFactoryAsync(account.Id, ct).ConfigureAwait(false);
        return await factory.ConnectAsync(account, ct).ConfigureAwait(false);
    }

    private async Task<MailKitIdleWatcherFactory> GetWatcherFactoryAsync(Guid accountId, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (watcherFactories.TryGetValue(accountId, out MailKitIdleWatcherFactory? factory))
            {
                return factory;
            }

            SqliteDatabase database = await connections.OpenAsync(ct).ConfigureAwait(false);
            var accountService = new AccountService(new SqliteAccountStore(database), tokenProviders);
            factory = new MailKitIdleWatcherFactory(accountService);
            watcherFactories.Add(accountId, factory);
            return factory;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AccountCycle> GetCycleAsync(Guid accountId, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (cycles.TryGetValue(accountId, out AccountCycle? cycle))
            {
                return cycle;
            }

            SqliteDatabase database = await connections.OpenAsync(ct).ConfigureAwait(false);
            var accountStore = new SqliteAccountStore(database);
            var accountService = new AccountService(accountStore, tokenProviders);
            var transportFactory = new MailKitTransportFactory(accountService);
            var folders = new SqliteFolderStore(database);
            var messages = new SqliteMessageStore(database);
            var emlStore = new FileEmlStore(paths);
            var syncStates = new SqliteFolderSyncStateStore(database);
            var commandQueue = new SqliteMailCommandQueue(database, timeProvider);
            var engine = new SyncEngine(
                transportFactory,
                accountStore,
                folders,
                messages,
                emlStore,
                syncStates,
                new SyncOptions(),
                timeProvider);
            var executor = new CommandExecutor(
                transportFactory,
                accountStore,
                folders,
                messages,
                emlStore,
                commandQueue,
                new CommandOptions(),
                timeProvider);
            var pass = new ClassificationPass(
                accountStore,
                folders,
                syncStates,
                messages,
                emlStore,
                new SqliteContactStore(database),
                new SqliteClassificationLog(database),
                commandQueue,
                new MailKitFolderProvisioner(accountService, folders),
                classifierSettings,
                classificationPolicy,
                timeProvider);
            cycle = new AccountCycle(engine, executor, pass);
            cycles.Add(accountId, cycle);
            return cycle;
        }
        finally
        {
            gate.Release();
        }
    }

    // Everything of one account's cycle, on the one connection of that cycle. The account loop runs its steps in sequence.
    private sealed record AccountCycle(SyncEngine Engine, CommandExecutor Executor, ClassificationPass Pass);
}
