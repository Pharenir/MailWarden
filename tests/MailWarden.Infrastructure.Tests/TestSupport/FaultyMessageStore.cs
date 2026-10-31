namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>
/// <see cref="IMessageStore"/> decorator over a real store. The local-effect writes of the command executor can be
/// made to fail with <see cref="StorageException"/> (nothing is written in that case).
/// </summary>
internal sealed class FaultyMessageStore(IMessageStore inner) : IMessageStore
{
    public bool FailDeleteByUids { get; set; }

    public bool FailUpdateFlags { get; set; }

    public Task<MailMessage> UpsertAsync(MailMessage message, string? bodyText, CancellationToken ct = default) =>
        inner.UpsertAsync(message, bodyText, ct);

    public Task<MailMessage?> GetAsync(long id, CancellationToken ct = default) => inner.GetAsync(id, ct);

    public Task<MailMessage?> GetByUidAsync(long folderId, uint uid, CancellationToken ct = default) =>
        inner.GetByUidAsync(folderId, uid, ct);

    public Task<IReadOnlyList<MailMessage>> ListAsync(MessageQuery query, CancellationToken ct = default) =>
        inner.ListAsync(query, ct);

    public Task<IReadOnlyList<MailMessage>> SearchAsync(string text, int limit = 100, CancellationToken ct = default) =>
        inner.SearchAsync(text, limit, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);

    public Task DeleteByUidsAsync(long folderId, IReadOnlyCollection<uint> uids, CancellationToken ct = default)
    {
        if (FailDeleteByUids)
        {
            throw new StorageException("Injected message delete failure.");
        }

        return inner.DeleteByUidsAsync(folderId, uids, ct);
    }

    public Task<IReadOnlyDictionary<uint, MessageFlags>> GetUidFlagsAsync(long folderId, CancellationToken ct = default) =>
        inner.GetUidFlagsAsync(folderId, ct);

    public Task<IReadOnlyDictionary<uint, string?>> GetUidEmlPathsAsync(long folderId, CancellationToken ct = default) =>
        inner.GetUidEmlPathsAsync(folderId, ct);

    public Task UpdateFlagsAsync(long folderId, IReadOnlyDictionary<uint, MessageFlags> flags, CancellationToken ct = default)
    {
        if (FailUpdateFlags)
        {
            throw new StorageException("Injected flag update failure.");
        }

        return inner.UpdateFlagsAsync(folderId, flags, ct);
    }
}
