using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>
/// Unique directory below the system temp folder, removed on dispose. Clears SQLite connection pools first so
/// that database files can be deleted on Windows.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "mailwarden-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    /// <summary>Absolute path of the directory.</summary>
    public string RootPath { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
