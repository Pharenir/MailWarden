using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using MailWarden.Core.Accounts;

namespace MailWarden.Infrastructure.Security;

/// <summary>
/// <see cref="ICredentialStore"/> with one file per key (<c>&lt;directory&gt;\&lt;key&gt;.bin</c>), encrypted with
/// DPAPI for the current Windows user and the fixed additional entropy "MailWarden.Credentials.v1" (UTF-8). Writes
/// are atomic (temporary file + replace). The directory is created on the first write.
/// </summary>
/// <remarks>
/// Keys are effectively case-insensitive here because NTFS is: two keys that differ only in case address the same
/// file. Callers use lower-case keys (see <see cref="ICredentialStore"/>). Read, write and delete of one instance
/// are serialised, so concurrent calls (e.g. a token read while a refresh writes) never collide; use exactly one
/// instance per directory, because Windows denies replacing a file that another instance or process is reading.
/// File system failures surface as <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.
/// </remarks>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private const int MaxKeyLength = 128;
    private const string SecretFileExtension = ".bin";
    private const string TemporaryFileExtension = ".tmp";

    private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("MailWarden.Credentials.v1");
    private static readonly SearchValues<char> KeyAlphabet =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-");

    private readonly string directory;

    // Serialises read, write and delete of this instance: File.Move(overwrite) is denied by Windows while another
    // handle on the target is open (even with delete sharing), so a read must never overlap a write.
    private readonly SemaphoreSlim fileAccessLock = new(1, 1);

    /// <summary>Creates the store. Does not touch the file system.</summary>
    /// <param name="directory">Fully qualified directory that holds the encrypted files.</param>
    /// <exception cref="ArgumentException">The directory is empty or not a fully qualified path.</exception>
    public DpapiCredentialStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException(
                $"Credential directory must be a fully qualified path: '{directory}'.",
                nameof(directory));
        }

        this.directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        string path = PathFor(key, nameof(key));
        byte[] encryptedBytes;
        await fileAccessLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            encryptedBytes = await ReadSharedAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        finally
        {
            fileAccessLock.Release();
        }

        // DPAPI rejects data of another user/machine and any tampered or random content.
        try
        {
            return ProtectedData.Unprotect(encryptedBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new CredentialStoreException(
                $"The stored credential '{key}' cannot be decrypted (corrupt or created by another Windows user).",
                exception);
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct = default)
    {
        string path = PathFor(key, nameof(key));

        // Encrypt from a private copy that is wiped right after use.
        byte[] plainBytes = secret.ToArray();
        byte[] encryptedBytes;
        try
        {
            encryptedBytes = ProtectedData.Protect(plainBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }

        // Unique temporary name in the same directory, so the final move is an atomic replace on the same volume.
        string temporaryPath = Path.Combine(directory, $"{key}.{Guid.NewGuid():N}{TemporaryFileExtension}");
        await fileAccessLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };
            await using (var stream = new FileStream(temporaryPath, options))
            {
                await stream.WriteAsync(encryptedBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            // Only left over when writing or moving failed.
            DeleteTemporaryFile(temporaryPath);
            fileAccessLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        string path = PathFor(key, nameof(key));
        await fileAccessLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // File.Delete is a no-op for a missing file; a missing directory means there is nothing to delete either.
            if (Directory.Exists(directory))
            {
                File.Delete(path);
            }
        }
        finally
        {
            fileAccessLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct = default)
    {
        // "" lists every key; a non-empty prefix follows the key rules.
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Length > 0)
        {
            ValidateKey(prefix, nameof(prefix));
        }

        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        // Filter by name in code: Win32 wildcard matching of "*.bin" is not exact enough to rely on.
        var keys = new List<string>();
        foreach (string filePath in Directory.EnumerateFiles(directory))
        {
            string fileName = Path.GetFileName(filePath);
            if (!fileName.EndsWith(SecretFileExtension, StringComparison.Ordinal))
            {
                continue;
            }

            string key = fileName[..^SecretFileExtension.Length];
            if (IsValidKey(key) && key.StartsWith(prefix, StringComparison.Ordinal))
            {
                keys.Add(key);
            }
        }

        keys.Sort(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    // Delete sharing (as in FileEmlStore): other processes may delete the file while it is being read. Replacing it
    // is still denied by Windows while a handle is open, which is why this instance serialises its own file access.
    private static async Task<byte[]> ReadSharedAsync(string path, CancellationToken ct)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous,
        };
        await using var stream = new FileStream(path, options);
        byte[] content = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(content, ct).ConfigureAwait(false);
        return content;
    }

    // Best effort by design: the original exception (or success) matters more than a leftover temp file, which never
    // shadows a secret because of its unique name and is ignored by ListKeysAsync.
    private static void DeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            // Ignored on purpose, see method comment.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignored on purpose, see method comment.
        }
    }

    private string PathFor(string key, string parameterName)
    {
        ValidateKey(key, parameterName);
        return Path.Combine(directory, key + SecretFileExtension);
    }

    private static void ValidateKey(string key, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        if (!IsValidKey(key))
        {
            throw new ArgumentException(
                $"Credential keys must be 1–{MaxKeyLength} characters of [A-Za-z0-9._-].",
                parameterName);
        }
    }

    private static bool IsValidKey(string key)
    {
        return key.Length is > 0 and <= MaxKeyLength && !key.AsSpan().ContainsAnyExcept(KeyAlphabet);
    }
}
