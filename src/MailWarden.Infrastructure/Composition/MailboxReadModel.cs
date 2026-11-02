using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using MailWarden.Infrastructure.Mime;
using MailWarden.Infrastructure.Storage;

namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// <see cref="IMailboxReadModel"/> on one own database connection. A gate serialises the connection; queries run on the
/// thread pool because Microsoft.Data.Sqlite executes synchronously and the caller is usually the UI thread.
/// </summary>
internal sealed class MailboxReadModel : IMailboxReadModel
{
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SqliteAccountStore accounts;
    private readonly SqliteFolderStore folders;
    private readonly SqliteMessageStore messages;
    private readonly IEmlStore emlStore;
    private bool isClosed;

    public MailboxReadModel(SqliteDatabase database, IEmlStore emlStore)
    {
        accounts = new SqliteAccountStore(database);
        folders = new SqliteFolderStore(database);
        messages = new SqliteMessageStore(database);
        this.emlStore = emlStore;
    }

    public Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct = default)
    {
        return RunSerialisedAsync(innerToken => accounts.ListAsync(innerToken), ct);
    }

    public Task<IReadOnlyList<MailFolder>> ListFoldersAsync(Guid accountId, CancellationToken ct = default)
    {
        return RunSerialisedAsync(innerToken => folders.ListAsync(accountId, innerToken), ct);
    }

    public Task<FolderRole?> GetFolderRoleAsync(long folderId, CancellationToken ct = default)
    {
        return RunSerialisedAsync(
            async innerToken => (await folders.GetAsync(folderId, innerToken).ConfigureAwait(false))?.Role,
            ct);
    }

    public Task<IReadOnlyList<MailMessage>> ListMessagesAsync(MessageQuery query, CancellationToken ct = default)
    {
        return RunSerialisedAsync(innerToken => messages.ListAsync(query, innerToken), ct);
    }

    public Task<int> CountUnreadAsync(MessageQuery query, CancellationToken ct = default)
    {
        return RunSerialisedAsync(innerToken => messages.CountUnreadAsync(query, innerToken), ct);
    }

    public Task<IReadOnlyList<MailMessage>> SearchAsync(string text, int limit, CancellationToken ct = default)
    {
        return RunSerialisedAsync(innerToken => messages.SearchAsync(text, limit, innerToken), ct);
    }

    public Task<MailContent?> ReadContentAsync(MailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Files only: no connection use, so a long parse does not hold up the lists.
        return Task.Run(() => ReadContentCoreAsync(message, ct), ct);
    }

    public Task CopyAttachmentAsync(MailMessage message, int attachmentIndex, MailAttachmentInfo expected, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(destination);

        // Files only, like ReadContentAsync: a long copy does not hold up the lists.
        return Task.Run(() => CopyAttachmentCoreAsync(message, attachmentIndex, expected, destination, ct), ct);
    }

    /// <summary>
    /// Waits for the running query and refuses later ones, so that the connection can be closed safely afterwards.
    /// </summary>
    /// <returns>A task that completes when no query runs any more.</returns>
    public async Task CloseAsync()
    {
        await connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            isClosed = true;
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task<MailContent?> ReadContentCoreAsync(MailMessage message, CancellationToken ct)
    {
        if (message.EmlPath is null)
        {
            return null;
        }

        var inMemoryEml = new MemoryStream();
        try
        {
            await using Stream emlFile = await emlStore.OpenReadAsync(message.EmlPath, ct).ConfigureAwait(false);
            await emlFile.CopyToAsync(inMemoryEml, ct).ConfigureAwait(false);
        }
        catch (EmlNotFoundException)
        {
            return null;
        }

        inMemoryEml.Position = 0;
        return await MailContentReader.ReadAsync(inMemoryEml, ct).ConfigureAwait(false);
    }

    private async Task CopyAttachmentCoreAsync(MailMessage message, int attachmentIndex, MailAttachmentInfo expected, Stream destination, CancellationToken ct)
    {
        if (message.EmlPath is null)
        {
            throw new FileNotFoundException("The message has no local copy.");
        }

        Stream emlFile;
        try
        {
            emlFile = await emlStore.OpenReadAsync(message.EmlPath, ct).ConfigureAwait(false);
        }
        catch (EmlNotFoundException)
        {
            // Without the inner exception: it names the path of the file.
            throw new FileNotFoundException("The message has no local copy.");
        }

        await using (emlFile.ConfigureAwait(false))
        {
            await MailContentReader
                .CopyAttachmentAsync(emlFile, attachmentIndex, expected, destination, ct)
                .ConfigureAwait(false);
        }
    }

    private Task<TResult> RunSerialisedAsync<TResult>(Func<CancellationToken, Task<TResult>> query, CancellationToken ct)
    {
        return Task.Run(
            async () =>
            {
                await connectionGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(isClosed, this);
                    return await query(ct).ConfigureAwait(false);
                }
                finally
                {
                    connectionGate.Release();
                }
            },
            ct);
    }
}
