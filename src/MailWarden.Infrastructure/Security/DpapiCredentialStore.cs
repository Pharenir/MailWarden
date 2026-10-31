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
/// Keys are case-sensitive in the contract, but the file system is not: two keys that differ only in case address
/// the same file.
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
        try
        {
            encryptedBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
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
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $"{key}.{Guid.NewGuid():N}{TemporaryFileExtension}");
        try
        {
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
            File.Delete(temporaryPath);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        string path = PathFor(key, nameof(key));
        ct.ThrowIfCancellationRequested();

        // File.Delete is a no-op for a missing file; a missing directory means there is nothing to delete either.
        if (Directory.Exists(directory))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct = default)
    {
        ValidateKey(prefix, nameof(prefix));
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
