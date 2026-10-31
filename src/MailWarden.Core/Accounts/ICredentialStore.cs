namespace MailWarden.Core.Accounts;

/// <summary>
/// Secret storage (OAuth token caches, refresh tokens). The only place where credentials are persisted.
/// Keys consist of 1–128 characters of <c>[A-Za-z0-9._-]</c>; any other key is rejected with
/// <see cref="ArgumentException"/> (<see cref="ArgumentNullException"/> for null) by every method.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Reads a secret.</summary>
    /// <param name="key">Key of the secret.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The secret bytes, or null when no secret is stored under the key.</returns>
    /// <exception cref="CredentialStoreException">The stored secret cannot be decrypted or is corrupt.</exception>
    Task<byte[]?> ReadAsync(string key, CancellationToken ct = default);

    /// <summary>Writes a secret atomically; an existing secret under the same key is overwritten.</summary>
    /// <param name="key">Key of the secret.</param>
    /// <param name="secret">Secret bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the secret is stored.</returns>
    Task WriteAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct = default);

    /// <summary>Deletes a secret. A missing key is a no-op.</summary>
    /// <param name="key">Key of the secret.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the secret is deleted.</returns>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Lists the keys that start with a prefix.</summary>
    /// <param name="prefix">Key prefix (same alphabet and length rules as a key).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching keys, sorted ordinally.</returns>
    Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct = default);
}
