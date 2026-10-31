namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>
/// <see cref="IEmlStore"/> decorator that delegates to a real store and throws <see cref="StorageException"/> for
/// writes whose relative path matches <see cref="FailWriteWhen"/> (nothing is written in that case).
/// </summary>
internal sealed class FaultyEmlStore(IEmlStore inner) : IEmlStore
{
    /// <summary>When set and true for a path, <see cref="WriteAsync"/> throws before writing.</summary>
    public Func<string, bool>? FailWriteWhen { get; set; }

    public string RelativePathFor(Guid accountId, long folderId, uint uid) => inner.RelativePathFor(accountId, folderId, uid);

    public Task WriteAsync(string relativePath, Stream content, CancellationToken ct = default)
    {
        if (FailWriteWhen?.Invoke(relativePath) == true)
        {
            throw new StorageException("Injected EML write failure.");
        }

        return inner.WriteAsync(relativePath, content, ct);
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default) => inner.OpenReadAsync(relativePath, ct);

    public bool Exists(string relativePath) => inner.Exists(relativePath);

    public Task DeleteAsync(string relativePath, CancellationToken ct = default) => inner.DeleteAsync(relativePath, ct);
}
