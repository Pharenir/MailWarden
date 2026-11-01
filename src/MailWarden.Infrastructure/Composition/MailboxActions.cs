using MailWarden.Core.Commands;
using MailWarden.Core.Model;
using MailWarden.Infrastructure.Storage;

namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// <see cref="IMailboxActions"/> on one own database connection. A gate serialises the connection; queries run on the thread
/// pool because Microsoft.Data.Sqlite executes synchronously and the caller is usually the UI thread.
/// </summary>
/// <param name="database">Initialised connection used by nothing else.</param>
/// <param name="triggerAccount">Schedules a cycle of an account after commands were stored.</param>
/// <param name="timeProvider">Clock of the queue.</param>
public sealed class MailboxActions(SqliteDatabase database, Action<Guid> triggerAccount, TimeProvider timeProvider) : IMailboxActions
{
    private const string RejectedText = "Die Aktion wurde abgelehnt, die Ansicht ist vermutlich nicht mehr aktuell.";
    private const string UnknownFolderText = "Der Ordner dieser Aktion ist nicht mehr bekannt.";
    private const string NoTrashFolderText = "Dieses Konto hat keinen Papierkorb.";
    private const string CannotEmptyFolderText = "Nur der Papierkorb und der Junk-Ordner können geleert werden.";

    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SqliteMailCommandQueue queue = new(database, timeProvider);
    private readonly SqliteAccountStore accounts = new(database);
    private readonly SqliteFolderStore folders = new(database);
    private readonly SqliteMessageStore messageStore = new(database);
    private readonly Action<Guid> triggerAccount = triggerAccount ?? throw new ArgumentNullException(nameof(triggerAccount));
    private bool isClosed;

    /// <inheritdoc />
    public Task SetSeenAsync(IReadOnlyCollection<MailMessage> messages, bool seen, CancellationToken ct = default)
    {
        return SetFlagAsync(messages, MessageFlags.Seen, seen, ct);
    }

    /// <inheritdoc />
    public Task SetFlaggedAsync(IReadOnlyCollection<MailMessage> messages, bool flagged, CancellationToken ct = default)
    {
        return SetFlagAsync(messages, MessageFlags.Flagged, flagged, ct);
    }

    /// <inheritdoc />
    public Task MoveToTrashAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken ct = default)
    {
        return EnqueuePerFolderAsync(
            messages,
            (folder, uids, token) => queue.EnqueueMoveToTrashAsync(folder.Id, folder.UidValidity, uids, token),
            ct);
    }

    /// <inheritdoc />
    public Task DeletePermanentlyAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken ct = default)
    {
        return EnqueuePerFolderAsync(
            messages,
            (folder, uids, token) => queue.EnqueueDeletePermanentlyAsync(folder.Id, folder.UidValidity, uids, token),
            ct);
    }

    /// <inheritdoc />
    public Task MoveAsync(IReadOnlyCollection<MailMessage> messages, long targetFolderId, CancellationToken ct = default)
    {
        return EnqueuePerFolderAsync(
            messages,
            (folder, uids, token) => queue.EnqueueMoveAsync(folder.Id, folder.UidValidity, uids, targetFolderId, token),
            ct);
    }

    /// <inheritdoc />
    public Task<int> CountForEmptyAsync(long folderId, CancellationToken ct = default)
    {
        return RunSerialisedAsync(
            async token =>
            {
                await RequireEmptiableFolderAsync(folderId, token).ConfigureAwait(false);
                IReadOnlyDictionary<uint, MessageFlags> localMessages = await messageStore.GetUidFlagsAsync(folderId, token).ConfigureAwait(false);
                return localMessages.Count;
            },
            ct);
    }

    /// <inheritdoc />
    public Task EmptyFolderAsync(long folderId, CancellationToken ct = default)
    {
        return RunSerialisedAsync(
            async token =>
            {
                await RequireEmptiableFolderAsync(folderId, token).ConfigureAwait(false);
                MailCommand? command = await CallQueueAsync(() => queue.EnqueueEmptyFolderAsync(folderId, token)).ConfigureAwait(false);
                if (command is not null)
                {
                    triggerAccount(command.AccountId);
                }

                return true;
            },
            ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PendingAction>> ListPendingAsync(CancellationToken ct = default)
    {
        return RunSerialisedAsync<IReadOnlyList<PendingAction>>(
            async token =>
            {
                var pendingActions = new List<PendingAction>();

                // One map per folder: a command of "Ordner leeren" can hold thousands of UIDs.
                var idsByFolder = new Dictionary<long, IReadOnlyDictionary<uint, long>>();
                foreach (Account account in await accounts.ListAsync(token).ConfigureAwait(false))
                {
                    foreach (MailCommand command in await queue.ListAsync(account.Id, token).ConfigureAwait(false))
                    {
                        if (!idsByFolder.TryGetValue(command.FolderId, out IReadOnlyDictionary<uint, long>? messageIds))
                        {
                            messageIds = await messageStore.GetUidIdsAsync(command.FolderId, token).ConfigureAwait(false);
                            idsByFolder.Add(command.FolderId, messageIds);
                        }

                        pendingActions.Add(ToPendingAction(command, messageIds));
                    }
                }

                return pendingActions;
            },
            ct);
    }

    /// <inheritdoc />
    public Task DiscardAsync(long commandId, CancellationToken ct = default)
    {
        return RunSerialisedAsync(
            async token =>
            {
                await queue.DiscardAsync(commandId, token).ConfigureAwait(false);
                return true;
            },
            ct);
    }

    /// <summary>
    /// Waits for the running call and refuses later ones, so that the connection can be closed safely afterwards.
    /// </summary>
    /// <returns>A task that completes when no call runs any more.</returns>
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

    private static PendingAction ToPendingAction(MailCommand command, IReadOnlyDictionary<uint, long> messageIds)
    {
        List<long> storedIds = [];
        foreach (uint uid in command.Uids)
        {
            if (messageIds.TryGetValue(uid, out long messageId))
            {
                storedIds.Add(messageId);
            }
        }

        return new PendingAction(command.Id, command.AccountId, command.Kind, command.Status, storedIds, command.LastError);
    }

    // The user interface gets the fixed text of ours, never the one of the queue. ObjectDisposedException is an
    // InvalidOperationException and no rejection.
    private static async Task<TResult> CallQueueAsync<TResult>(Func<Task<TResult>> queueCall)
    {
        try
        {
            return await queueCall().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            throw new MailActionException(RejectedText);
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            throw new MailActionException(NoTrashFolderText);
        }
    }

    private Task SetFlagAsync(IReadOnlyCollection<MailMessage> messages, MessageFlags flag, bool isSet, CancellationToken ct)
    {
        MessageFlags flagsToAdd = isSet ? flag : MessageFlags.None;
        MessageFlags flagsToRemove = isSet ? MessageFlags.None : flag;
        return EnqueuePerFolderAsync(
            messages,
            (folder, uids, token) => queue.EnqueueSetFlagsAsync(folder.Id, folder.UidValidity, uids, flagsToAdd, flagsToRemove, token),
            ct);
    }

    // One command per folder and per 1,000 UIDs. A rejection ends the call: what is stored stays, and the accounts of these
    // commands are triggered all the same.
    private Task EnqueuePerFolderAsync(
        IReadOnlyCollection<MailMessage> messages,
        Func<MailFolder, IReadOnlyCollection<uint>, CancellationToken, Task<MailCommand>> enqueue,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        List<IGrouping<long, MailMessage>> messagesPerFolder = messages.GroupBy(message => message.FolderId).ToList();
        if (messagesPerFolder.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunSerialisedAsync(
            async token =>
            {
                var accountsWithNewCommands = new HashSet<Guid>();
                try
                {
                    foreach (IGrouping<long, MailMessage> folderMessages in messagesPerFolder)
                    {
                        MailFolder folder = await folders.GetAsync(folderMessages.Key, token).ConfigureAwait(false)
                            ?? throw new MailActionException(UnknownFolderText);
                        await EnsureRowsAreCurrentAsync(folder.Id, folderMessages, token).ConfigureAwait(false);

                        List<uint> uids = folderMessages.Select(message => message.Uid).Distinct().Order().ToList();
                        foreach (uint[] commandUids in uids.Chunk(SqliteMailCommandQueue.MaxUidsPerCommand))
                        {
                            MailCommand command = await CallQueueAsync(() => enqueue(folder, commandUids, token)).ConfigureAwait(false);
                            accountsWithNewCommands.Add(command.AccountId);
                        }
                    }
                }
                finally
                {
                    foreach (Guid accountId in accountsWithNewCommands)
                    {
                        triggerAccount(accountId);
                    }
                }

                return true;
            },
            ct);
    }

    // The UIDVALIDITY is the one stored now, so it no longer shows that the row of the caller is of this generation: after a
    // reset the same UID can be another mail with a new id, and a delete must never reach that one. A UID that is gone is
    // left to the queue.
    private async Task EnsureRowsAreCurrentAsync(long folderId, IEnumerable<MailMessage> messages, CancellationToken ct)
    {
        IReadOnlyDictionary<uint, long> storedIds = await messageStore.GetUidIdsAsync(folderId, ct).ConfigureAwait(false);
        foreach (MailMessage message in messages)
        {
            if (message.Id != 0 && storedIds.TryGetValue(message.Uid, out long storedId) && storedId != message.Id)
            {
                throw new MailActionException(RejectedText);
            }
        }
    }

    private async Task RequireEmptiableFolderAsync(long folderId, CancellationToken ct)
    {
        MailFolder folder = await folders.GetAsync(folderId, ct).ConfigureAwait(false)
            ?? throw new MailActionException(UnknownFolderText);
        if (folder.Role is not (FolderRole.Trash or FolderRole.Junk))
        {
            throw new MailActionException(CannotEmptyFolderText);
        }
    }

    private Task<TResult> RunSerialisedAsync<TResult>(Func<CancellationToken, Task<TResult>> work, CancellationToken ct)
    {
        return Task.Run(
            async () =>
            {
                await connectionGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(isClosed, this);
                    return await work(ct).ConfigureAwait(false);
                }
                finally
                {
                    connectionGate.Release();
                }
            },
            ct);
    }
}
