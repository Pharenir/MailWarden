using MailWarden.Infrastructure.Storage;

namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// Opens and owns every database connection of a runtime. <see cref="SqliteDatabase"/> is not thread-safe, so each flow
/// (service account list, account cycle, IDLE watcher, read model) gets its own connection; WAL mode allows this.
/// </summary>
internal sealed class RuntimeConnections(DataPaths paths) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<SqliteDatabase> databases = [];
    private bool isDisposed;

    /// <summary>Opens a new, initialised connection; it stays owned by this object.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The connection.</returns>
    /// <exception cref="ObjectDisposedException">The connections were disposed.</exception>
    public async Task<SqliteDatabase> OpenAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            SqliteDatabase database = SqliteDatabase.ForFile(paths.DatabasePath);
            try
            {
                await database.InitializeAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await database.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            databases.Add(database);
            return database;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Closes all connections; later calls of <see cref="OpenAsync"/> fail.</summary>
    /// <returns>A task that completes when every connection is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            isDisposed = true;
            foreach (SqliteDatabase database in databases)
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }

            databases.Clear();
        }
        finally
        {
            gate.Release();
        }
    }
}
