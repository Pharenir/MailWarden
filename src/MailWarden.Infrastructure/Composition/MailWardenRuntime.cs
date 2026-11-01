using MailWarden.Core.Accounts;
using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Auth;
using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Classification;
using MailWarden.Infrastructure.Logging;
using MailWarden.Infrastructure.Security;
using MailWarden.Infrastructure.Storage;

namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// Composition root of one opened data folder: holds the data-folder lock, the database connections (one per flow), one
/// credential store with the token providers on it, the background service and the read model of the user interface.
/// </summary>
public sealed class MailWardenRuntime : IAsyncDisposable
{
    /// <summary>
    /// Key in <see cref="Exception.Data"/> of the <see cref="InvalidDataException"/> of <see cref="OpenAsync"/>: holds the
    /// name of the invalid file as a string, for a start error without the exception message.
    /// </summary>
    public const string InvalidFileNameDataKey = "MailWarden.InvalidFileName";

    private const string AuthConfigFileName = "auth-config.json";
    private const string CredentialsDirectoryName = "credentials";

    private readonly DataFolderLock folderLock;
    private readonly RuntimeConnections connections;
    private readonly MailboxReadModel readModel;
    private readonly ICriticalLog log;
    private readonly Lock disposeGate = new();
    private Task? disposeTask;

    private MailWardenRuntime(
        DataPaths paths,
        DataFolderLock folderLock,
        RuntimeConnections connections,
        MailboxService service,
        MailboxReadModel readModel,
        ICriticalLog log)
    {
        Paths = paths;
        this.folderLock = folderLock;
        this.connections = connections;
        Service = service;
        this.readModel = readModel;
        this.log = log;
    }

    /// <summary>
    /// Raised on the thread of the account's cycle after every successful classification pass; never after
    /// <see cref="DisposeAsync"/> was called.
    /// </summary>
    /// <remarks>
    /// An exception thrown by a subscriber is logged, per subscriber: it can neither fail the cycle nor keep other subscribers
    /// from being called. Do not block on <see cref="DisposeAsync"/> inside a handler (it waits for the cycle).
    /// </remarks>
    public event EventHandler<ClassificationPassSummary>? ClassificationPassCompleted;

    /// <summary>Paths of the opened data folder.</summary>
    public DataPaths Paths { get; }

    /// <summary>The background service; not started yet after <see cref="OpenAsync"/>.</summary>
    public MailboxService Service { get; }

    /// <summary>Read access for the user interface.</summary>
    public IMailboxReadModel ReadModel => readModel;

    /// <summary>
    /// Takes the data-folder lock BEFORE opening or migrating the database, loads the classification policy and the
    /// classifier settings once from the data root, loads the auth configuration from <c>&lt;root&gt;\auth-config.json</c> +
    /// environment, creates one credential store in <c>&lt;root&gt;\credentials</c>, one provider of each kind, and does not
    /// start the service. Failures after the lock release it again.
    /// </summary>
    /// <param name="paths">Data folder to open.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="log">Critical log of the account cycles; null means no logging.</param>
    /// <returns>The opened runtime.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <exception cref="DataFolderInUseException">The lock is held; the database file is then untouched.</exception>
    /// <exception cref="InvalidDataException">
    /// The policy file or the settings file is invalid; the database file is then untouched and
    /// <see cref="InvalidFileNameDataKey"/> names the file.
    /// </exception>
    /// <exception cref="MailWarden.Core.Storage.SchemaVersionException">The database schema is newer than this build.</exception>
    /// <exception cref="MailWarden.Core.Storage.StorageException">The database cannot be opened or migrated.</exception>
    /// <exception cref="AuthenticationFailedException">The auth configuration file is invalid.</exception>
    public static Task<MailWardenRuntime> OpenAsync(DataPaths paths, CancellationToken ct = default, ICriticalLog? log = null)
    {
        return OpenCoreAsync(paths, null, log, ct);
    }

    /// <summary>
    /// <see cref="OpenAsync"/> with the server steps of the account cycle replaced; everything else is composed in the same
    /// way. Lets tests check the wiring without a server: the app never calls this.
    /// </summary>
    /// <param name="paths">Data folder to open.</param>
    /// <param name="cycleSteps">Replaces the server steps.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="log">Critical log of the account cycles; null means no logging.</param>
    /// <returns>The opened runtime.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or <paramref name="cycleSteps"/> is null.</exception>
    public static async Task<MailWardenRuntime> OpenWithCycleStepsAsync(
        DataPaths paths,
        AccountCycleSteps cycleSteps,
        CancellationToken ct = default,
        ICriticalLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(cycleSteps);
        return await OpenCoreAsync(paths, cycleSteps, log, ct).ConfigureAwait(false);
    }

    /// <summary>Opens a new, initialised connection of its own; it is owned and disposed by the runtime.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The connection.</returns>
    /// <exception cref="ObjectDisposedException">The runtime was disposed.</exception>
    public Task<SqliteDatabase> OpenDatabaseAsync(CancellationToken ct = default)
    {
        return connections.OpenAsync(ct);
    }

    /// <summary>Stops the service, disposes all connections, releases the lock. Idempotent.</summary>
    /// <returns>A task that completes when everything is released.</returns>
    /// <exception cref="AggregateException">
    /// An account loop ended with an unexpected failure; connections and lock are released anyway.
    /// </exception>
    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            disposeTask ??= DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private static async Task<MailWardenRuntime> OpenCoreAsync(
        DataPaths paths,
        AccountCycleSteps? cycleSteps,
        ICriticalLog? log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);

        DataFolderLock folderLock = DataFolderLock.TryAcquire(paths) ?? throw new DataFolderInUseException();
        var connections = new RuntimeConnections(paths);
        try
        {
            // Before anything else under the lock: an invalid file must not leave a migrated database behind.
            ClassificationPolicy classificationPolicy = LoadClassifierFile(paths, ClassificationPolicyFile.FileName, ClassificationPolicyFile.Load);
            ClassifierSettings classifierSettings = LoadClassifierFile(paths, ClassifierSettingsFile.FileName, ClassifierSettingsFile.Load);

            AuthConfiguration configuration = AuthConfiguration.Load(
                Path.Combine(paths.Root, AuthConfigFileName),
                Environment.GetEnvironmentVariable);

            // Exactly one credential store per directory: the providers share it (see DpapiCredentialStore).
            var credentialStore = new DpapiCredentialStore(Path.Combine(paths.Root, CredentialsDirectoryName));
            ITokenProvider[] tokenProviders =
            [
                new MicrosoftTokenProvider(configuration, credentialStore),
                new GoogleTokenProvider(configuration, credentialStore),
            ];

            TimeProvider timeProvider = TimeProvider.System;
            ICriticalLog criticalLog = log ?? new NoCriticalLog();
            SqliteDatabase serviceDatabase = await connections.OpenAsync(ct).ConfigureAwait(false);
            SqliteDatabase readDatabase = await connections.OpenAsync(ct).ConfigureAwait(false);
            var pipelines = new AccountPipelines(paths, connections, tokenProviders, timeProvider, classifierSettings, classificationPolicy);

            // Service and runtime need the cycle, the cycle needs them. Assigned below, before any cycle can run: the service
            // starts only after this method returned.
            MailboxService? service = null;
            MailWardenRuntime? runtime = null;
            var cycle = new ClassifyingSyncCycle(
                cycleSteps?.Sync ?? pipelines.SyncAccountAsync,
                cycleSteps?.Classify ?? pipelines.ClassifyAccountAsync,
                accountId => service!.TriggerAccount(accountId),
                summary => runtime!.RaiseClassificationPassCompleted(summary),
                criticalLog);
            service = new MailboxService(
                paths,
                new SqliteAccountStore(serviceDatabase),
                cycleSteps?.ExecuteCommands ?? pipelines.ExecuteCommandsAsync,
                cycle.RunAsync,
                cycleSteps?.IdleWatchers ?? pipelines,
                MailboxServiceOptions.Default,
                new TimeProviderServiceDelay(timeProvider),
                timeProvider,
                folderLock);
            var mailboxReadModel = new MailboxReadModel(readDatabase, new FileEmlStore(paths));
            runtime = new MailWardenRuntime(paths, folderLock, connections, service, mailboxReadModel, criticalLog);
            return runtime;
        }
        catch
        {
            await connections.DisposeAsync().ConfigureAwait(false);
            folderLock.Dispose();
            throw;
        }
    }

    // The exception stays the loader's own; it only gets the file name attached.
    private static TFileContent LoadClassifierFile<TFileContent>(DataPaths paths, string fileName, Func<string, TFileContent> load)
    {
        try
        {
            return load(Path.Combine(paths.Root, fileName));
        }
        catch (InvalidDataException exception)
        {
            exception.Data[InvalidFileNameDataKey] = fileName;
            throw;
        }
    }

    private void RaiseClassificationPassCompleted(ClassificationPassSummary summary)
    {
        lock (disposeGate)
        {
            if (disposeTask is not null)
            {
                return;
            }
        }

        EventHandler<ClassificationPassSummary>? handlers = ClassificationPassCompleted;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<ClassificationPassSummary> handler in handlers.GetInvocationList().Cast<EventHandler<ClassificationPassSummary>>())
        {
            try
            {
                handler(this, summary);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                log.Write(CriticalLogLevel.Critical, "MailWardenRuntime.RaiseClassificationPassCompleted", $"A subscriber failed: {exception.GetType().Name}");
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await Service.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // A read of the user interface may still run: it ends before its connection closes.
                await readModel.CloseAsync().ConfigureAwait(false);
                await connections.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                folderLock.Dispose();
            }
        }
    }

    // Stands in for a missing log, so that the cycle has one way to write.
    private sealed class NoCriticalLog : ICriticalLog
    {
        public void Write(string level, string source, string message)
        {
        }
    }
}
