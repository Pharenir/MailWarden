using System.Globalization;

namespace MailWarden.Core.Storage;

/// <summary>
/// The database schema is newer than this build understands. Thrown before anything is written to the database.
/// </summary>
public sealed class SchemaVersionException : StorageException
{
    /// <summary>Creates the exception; the message contains both version numbers.</summary>
    /// <param name="foundVersion">Schema version stored in the database.</param>
    /// <param name="supportedVersion">Highest schema version this build supports.</param>
    public SchemaVersionException(int foundVersion, int supportedVersion)
        : base(string.Format(
            CultureInfo.InvariantCulture,
            "Database schema version {0} is newer than the supported version {1}. Update MailWarden.",
            foundVersion,
            supportedVersion))
    {
        FoundVersion = foundVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>Schema version stored in the database.</summary>
    public int FoundVersion { get; }

    /// <summary>Highest schema version this build supports.</summary>
    public int SupportedVersion { get; }
}
