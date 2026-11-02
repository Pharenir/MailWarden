using System.Collections.Frozen;

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
    private const string AttachmentsOpenDirectoryName = "attachments-open";

    private static readonly char[] PathSeparators = ['/', '\\'];
    private static readonly char[] InvalidSegmentCharacters = Path.GetInvalidFileNameChars();

    // Reserved DOS device names (case-insensitive); COM/LPT with superscript 1-3 and the console names are devices as well.
    private static readonly FrozenSet<string> DosDeviceNames = new[]
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
        AttachmentsOpenRoot = Path.Combine(Root, AttachmentsOpenDirectoryName);
    }

    /// <summary>Normalised data root directory.</summary>
    public string Root { get; }

    /// <summary>SQLite database file: <c>&lt;Root&gt;\mailwarden.db</c>.</summary>
    public string DatabasePath { get; }

    /// <summary>Root of the EML store: <c>&lt;Root&gt;\mail</c>.</summary>
    public string MailRoot { get; }

    /// <summary>Log directory: <c>&lt;Root&gt;\logs</c>.</summary>
    public string LogRoot { get; }

    /// <summary>Attachments prepared for opening; emptied at every start and exit: <c>&lt;Root&gt;\attachments-open</c>.</summary>
    public string AttachmentsOpenRoot { get; }

    /// <summary>
    /// Resolves a path relative to <see cref="MailRoot"/> (forward or back slashes) to an absolute path with OS
    /// separators.
    /// </summary>
    /// <param name="relativePath">Relative path, e.g. <c>accounts/{id}/folders/1/2.eml</c>.</param>
    /// <returns>Absolute path below <see cref="MailRoot"/>.</returns>
    /// <exception cref="ArgumentException">
    /// The path is empty, rooted, contains "..", "." or empty segments, segments ending in "." or a space, reserved
    /// device names (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>CONIN$</c>, <c>CONOUT$</c>, <c>COM0</c>-<c>COM9</c>,
    /// <c>LPT0</c>-<c>LPT9</c>, the superscript variants <c>COM¹</c>-<c>COM³</c> and <c>LPT¹</c>-<c>LPT³</c>, with or without
    /// extension), or
    /// characters invalid in file names.
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

            // Windows strips a trailing '.' or space ("x.eml." aliases "x.eml", "..." aliases the parent).
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw new ArgumentException(
                    $"Mail path contains a segment ending in '.' or a space: '{relativePath}'.",
                    nameof(relativePath));
            }

            if (IsDosDeviceName(segment))
            {
                throw new ArgumentException(
                    $"Mail path contains a reserved device name: '{relativePath}'.",
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

    /// <summary>
    /// Checks for a name Windows maps to a device in every directory, with or without extension ("NUL", "con.eml"); trailing
    /// spaces before the extension are ignored by Windows as well ("CON .eml").
    /// </summary>
    /// <param name="segment">One path segment.</param>
    /// <returns>True when the segment names a device.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> is null.</exception>
    public static bool IsDosDeviceName(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        int extensionStart = segment.IndexOf('.', StringComparison.Ordinal);
        string baseName = (extensionStart < 0 ? segment : segment[..extensionStart]).TrimEnd(' ');
        return DosDeviceNames.Contains(baseName);
    }
}
