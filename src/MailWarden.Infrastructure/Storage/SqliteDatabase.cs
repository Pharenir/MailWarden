using System.Globalization;
using MailWarden.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// Owns one open <see cref="SqliteConnection"/> (desktop app, single caller). <see cref="InitializeAsync"/> opens the
/// connection, enables foreign keys, checks the schema version and runs pending migrations.
/// Not thread-safe; a command queue serialises access from AP-03 on.
/// </summary>
public sealed class SqliteDatabase : IAsyncDisposable
{
    /// <summary>Schema version this build creates and understands.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly string connectionString;
    private readonly string? databaseFilePath;
    private SqliteConnection? openConnection;
    private int schemaVersion;
    private bool isDisposed;

    /// <summary>Creates a database for an arbitrary connection string. Nothing is opened until initialisation.</summary>
    /// <param name="connectionString">Microsoft.Data.Sqlite connection string.</param>
    /// <exception cref="ArgumentException">The connection string is empty.</exception>
    public SqliteDatabase(string connectionString)
        : this(connectionString, databaseFilePath: null)
    {
    }

    private SqliteDatabase(string connectionString, string? databaseFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
        this.databaseFilePath = databaseFilePath;
    }

    /// <summary>
    /// Creates a database for a file ("Data Source=&lt;path&gt;", connection pooling off so that disposing releases the
    /// file). Initialisation creates the parent directory if needed and switches the journal to WAL.
    /// </summary>
    /// <param name="databasePath">Fully qualified path of the database file.</param>
    /// <returns>An uninitialised database.</returns>
    /// <exception cref="ArgumentException">The path is empty or not fully qualified.</exception>
    public static SqliteDatabase ForFile(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException(
                $"Database path must be fully qualified: '{databasePath}'.",
                nameof(databasePath));
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        return new SqliteDatabase(builder.ToString(), databasePath);
    }

    /// <summary>Creates a private in-memory database ("Data Source=:memory:"); its content dies with the instance.</summary>
    /// <returns>An uninitialised database.</returns>
    public static SqliteDatabase InMemory()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Pooling = false,
        };
        return new SqliteDatabase(builder.ToString());
    }

    /// <summary>The open connection.</summary>
    /// <exception cref="InvalidOperationException"><see cref="InitializeAsync"/> has not completed.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    public SqliteConnection Connection
    {
        get
        {
            ThrowIfNotInitialized();
            return openConnection!;
        }
    }

    /// <summary>Schema version after <see cref="InitializeAsync"/> (always <see cref="CurrentSchemaVersion"/>).</summary>
    /// <exception cref="InvalidOperationException"><see cref="InitializeAsync"/> has not completed.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    public int SchemaVersion
    {
        get
        {
            ThrowIfNotInitialized();
            return schemaVersion;
        }
    }

    /// <summary>
    /// Opens the connection, enables foreign keys, reads the schema version and migrates from it to
    /// <see cref="CurrentSchemaVersion"/>. A newer schema is rejected before anything is written.
    /// May be called once per instance.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the database is ready.</returns>
    /// <exception cref="SchemaVersionException">The database schema is newer than this build.</exception>
    /// <exception cref="StorageException">The database cannot be opened, is corrupt or a migration fails.</exception>
    /// <exception cref="InvalidOperationException">The database is already initialised.</exception>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (openConnection is not null)
        {
            throw new InvalidOperationException("The database is already initialised.");
        }

        var connection = new SqliteConnection(connectionString);
        try
        {
            // Open: file databases get their directory first (SQLite does not create directories).
            string? databaseDirectory = databaseFilePath is null ? null : Path.GetDirectoryName(databaseFilePath);
            if (!string.IsNullOrEmpty(databaseDirectory))
            {
                Directory.CreateDirectory(databaseDirectory);
            }

            await connection.OpenAsync(ct).ConfigureAwait(false);
            await EnableForeignKeysAsync(connection, ct).ConfigureAwait(false);

            // Version check happens before any write (including the WAL switch, which writes the file header).
            int foundVersion = await ReadSchemaVersionAsync(connection, ct).ConfigureAwait(false);
            if (foundVersion > CurrentSchemaVersion)
            {
                throw new SchemaVersionException(foundVersion, CurrentSchemaVersion);
            }

            if (databaseFilePath is not null)
            {
                await EnableWriteAheadLogAsync(connection, ct).ConfigureAwait(false);
            }

            await MigrateAsync(connection, foundVersion, ct).ConfigureAwait(false);

            openConnection = connection;
            schemaVersion = CurrentSchemaVersion;
        }
        catch (SqliteException exception)
        {
            await CloseAndReleaseAsync(connection).ConfigureAwait(false);
            throw new StorageException($"Opening the database failed: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await CloseAndReleaseAsync(connection).ConfigureAwait(false);
            throw new StorageException($"Creating the database directory failed: {exception.Message}", exception);
        }
        catch
        {
            await CloseAndReleaseAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Closes the connection and releases the database file. Safe to call more than once.</summary>
    /// <returns>A task that completes when the connection is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        if (openConnection is not null)
        {
            await CloseAndReleaseAsync(openConnection).ConfigureAwait(false);
            openConnection = null;
        }
    }

    private void ThrowIfNotInitialized()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (openConnection is null)
        {
            throw new InvalidOperationException("The database is not initialised; call InitializeAsync first.");
        }
    }

    // Closes the connection and drops any pooled handle so that the database file is no longer locked.
    private static async Task CloseAndReleaseAsync(SqliteConnection connection)
    {
        await connection.CloseAsync().ConfigureAwait(false);
        SqliteConnection.ClearPool(connection);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task EnableForeignKeysAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var enableCommand = connection.CreateCommand();
        enableCommand.CommandText = "PRAGMA foreign_keys = ON;";
        await enableCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Fail fast if the SQLite build ignores the pragma; cascades and constraint checks depend on it.
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA foreign_keys;";
        object? enabled = await checkCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (Convert.ToInt64(enabled, CultureInfo.InvariantCulture) != 1)
        {
            throw new StorageException("SQLite foreign key enforcement could not be enabled.");
        }
    }

    private static async Task EnableWriteAheadLogAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        object? journalMode = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (!string.Equals(journalMode as string, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageException($"SQLite refused WAL journal mode (got '{journalMode}').");
        }
    }

    // Missing schema_version table = fresh database (version 0); otherwise exactly one row is required.
    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version';";
        long tableCount = Convert.ToInt64(
            await tableCommand.ExecuteScalarAsync(ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (tableCount == 0)
        {
            return 0;
        }

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT version FROM schema_version;";
        var versions = new List<long>();
        await using (var reader = await versionCommand.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                versions.Add(reader.GetInt64(0));
            }
        }

        if (versions.Count != 1)
        {
            throw new StorageException(
                $"Table schema_version must contain exactly one row but contains {versions.Count}.");
        }

        if (versions[0] < 1 || versions[0] > int.MaxValue)
        {
            throw new StorageException($"Table schema_version contains an invalid version {versions[0]}.");
        }

        return (int)versions[0];
    }

    // Runs every migration above foundVersion, each in its own transaction that ends with the new version number.
    private static async Task MigrateAsync(SqliteConnection connection, int foundVersion, CancellationToken ct)
    {
        EnsureMigrationListIsConsistent();
        foreach ((int version, string sql) in SchemaMigrations.Ordered)
        {
            if (version <= foundVersion)
            {
                continue;
            }

            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(ct)
                .ConfigureAwait(false);

            await using (var migrationCommand = connection.CreateCommand())
            {
                migrationCommand.Transaction = transaction;
                migrationCommand.CommandText = sql;
                await migrationCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.Transaction = transaction;
                versionCommand.CommandText =
                    "DELETE FROM schema_version; INSERT INTO schema_version (version) VALUES ($version);";
                versionCommand.Parameters.AddWithValue("$version", version);
                await versionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    // Programming error guard: versions must be 1..CurrentSchemaVersion without gaps.
    private static void EnsureMigrationListIsConsistent()
    {
        for (int index = 0; index < SchemaMigrations.Ordered.Count; index++)
        {
            if (SchemaMigrations.Ordered[index].Version != index + 1)
            {
                throw new InvalidOperationException(
                    $"SchemaMigrations entry {index} has version {SchemaMigrations.Ordered[index].Version}, "
                    + $"expected {index + 1}.");
            }
        }

        if (SchemaMigrations.Ordered.Count != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"SchemaMigrations ends at version {SchemaMigrations.Ordered.Count}, "
                + $"but CurrentSchemaVersion is {CurrentSchemaVersion}.");
        }
    }
}
