using MailWarden.Core.Accounts;
using MailWarden.Infrastructure.Auth;
using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Security;
using MailWarden.Infrastructure.Storage;

namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// Composition root of one opened data folder: holds the data-folder lock, the database connections (one per flow), one
/// credential store with the token providers on it, the background service and the read model of the user interface.
/// </summary>
public sealed class MailWardenRuntime : IAsyncDisposable
{
    private const string AuthConfigFileName = "auth-config.json";
    private const string CredentialsDirectoryName = "credentials";

    private readonly DataFolderLock folderLock;
    private readonly RuntimeConnections connections;
    private readonly Lock disposeGate = new();
    private Task? disposeTask;

    private MailWardenRuntime(
        DataPaths paths,
        DataFolderLock folderLock,
        RuntimeConnections connections,
        MailboxService service,
        IMailboxReadModel readModel)
    {
        Paths = paths;
        this.folderLock = folderLock;
        this.connections = connections;
        Service = service;
        ReadModel = readModel;
    }

    /// <summary>Paths of the opened data folder.</summary>
    public DataPaths Paths { get; }

    /// <summary>The background service; not started yet after <see cref="OpenAsync"/>.</summary>
    public MailboxService Service { get; }

    /// <summary>Read access for the user interface.</summary>
    public IMailboxReadModel ReadModel { get; }

    /// <summary>
    /// Takes the data-folder lock BEFORE opening or migrating the database, loads the auth configuration from
    /// <c>&lt;root&gt;\auth-config.json</c> + environment, creates one credential store in <c>&lt;root&gt;\credentials</c>,
    /// one provider of each kind, and does not start the service. Failures after the lock release it again.
    /// </summary>
    /// <param name="paths">Data folder to open.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The opened runtime.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <exception cref="DataFolderInUseException">The lock is held; the database file is then untouched.</exception>
    /// <exception cref="MailWarden.Core.Storage.SchemaVersionException">The database schema is newer than this build.</exception>
    /// <exception cref="MailWarden.Core.Storage.StorageException">The database cannot be opened or migrated.</exception>
    /// <exception cref="AuthenticationFailedException">The auth configuration file is invalid.</exception>
    public static async Task<MailWardenRuntime> OpenAsync(DataPaths paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        DataFolderLock folderLock = DataFolderLock.TryAcquire(paths) ?? throw new DataFolderInUseException();
        var connections = new RuntimeConnections(paths);
        try
        {
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
            SqliteDatabase serviceDatabase = await connections.OpenAsync(ct).ConfigureAwait(false);
            SqliteDatabase readDatabase = await connections.OpenAsync(ct).ConfigureAwait(false);
            var pipelines = new AccountPipelines(paths, connections, tokenProviders, timeProvider);
            var service = new MailboxService(
                paths,
                new SqliteAccountStore(serviceDatabase),
                pipelines.ExecuteCommandsAsync,
                pipelines.SyncAccountAsync,
                pipelines,
                MailboxServiceOptions.Default,
                new TimeProviderServiceDelay(timeProvider),
                timeProvider,
                folderLock);
            var readModel = new MailboxReadModel(readDatabase, new FileEmlStore(paths));
            return new MailWardenRuntime(paths, folderLock, connections, service, readModel);
        }
        catch
        {
            await connections.DisposeAsync().ConfigureAwait(false);
            folderLock.Dispose();
            throw;
        }
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
                await connections.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                folderLock.Dispose();
            }
        }
    }
}
