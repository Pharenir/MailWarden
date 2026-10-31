namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// Resolves everything below the data root (database, mail store, logs). Relative mail paths are validated so that
/// data from outside (folder names, stored paths) can never point outside the mail root.
/// </summary>
public sealed class DataPaths
{
    private const string DatabaseFileName = "mailwarden.db";
    private const string MailDirectoryName = "mail";
    private const string LogDirectoryName = "logs";

    private static readonly char[] PathSeparators = ['/', '\\'];
    private static readonly char[] InvalidSegmentCharacters = Path.GetInvalidFileNameChars();

    /// <summary>Creates the path set for a data root. Does not touch the file system.</summary>
    /// <param name="rootDirectory">Fully qualified data root directory.</param>
    /// <exception cref="ArgumentException">The root is empty or not a fully qualified path.</exception>
    public DataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException(
                $"Data root must be a fully qualified path: '{rootDirectory}'.",
                nameof(rootDirectory));
        }

        // Normalise once; all other paths derive from the normalised root.
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        DatabasePath = Path.Combine(Root, DatabaseFileName);
        MailRoot = Path.Combine(Root, MailDirectoryName);
        LogRoot = Path.Combine(Root, LogDirectoryName);
    }

    /// <summary>Normalised data root directory.</summary>
    public string Root { get; }

    /// <summary>SQLite database file: <c>&lt;Root&gt;\mailwarden.db</c>.</summary>
    public string DatabasePath { get; }

    /// <summary>Root of the EML store: <c>&lt;Root&gt;\mail</c>.</summary>
    public string MailRoot { get; }

    /// <summary>Log directory: <c>&lt;Root&gt;\logs</c>.</summary>
    public string LogRoot { get; }

    /// <summary>
    /// Resolves a path relative to <see cref="MailRoot"/> (forward or back slashes) to an absolute path with OS
    /// separators.
    /// </summary>
    /// <param name="relativePath">Relative path, e.g. <c>accounts/{id}/folders/1/2.eml</c>.</param>
    /// <returns>Absolute path below <see cref="MailRoot"/>.</returns>
    /// <exception cref="ArgumentException">
    /// The path is empty, rooted, contains "..", "." or empty segments, or contains characters invalid in file names.
    /// </exception>
    public string ResolveMailPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException($"Mail path must be relative: '{relativePath}'.", nameof(relativePath));
        }

        // Validate every segment; ".." and drive/stream characters (':') are rejected here.
        string[] segments = relativePath.Split(PathSeparators);
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new ArgumentException(
                    $"Mail path contains an empty, '.' or '..' segment: '{relativePath}'.",
                    nameof(relativePath));
            }

            if (segment.IndexOfAny(InvalidSegmentCharacters) >= 0)
            {
                throw new ArgumentException(
                    $"Mail path contains invalid characters: '{relativePath}'.",
                    nameof(relativePath));
            }
        }

        // Defence in depth: the normalised result must still lie below the mail root.
        string resolvedPath = Path.GetFullPath(Path.Combine(MailRoot, Path.Combine(segments)));
        string mailRootWithSeparator = MailRoot + Path.DirectorySeparatorChar;
        if (!resolvedPath.StartsWith(mailRootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Mail path resolves outside the mail root: '{relativePath}'.",
                nameof(relativePath));
        }

        return resolvedPath;
    }
}
