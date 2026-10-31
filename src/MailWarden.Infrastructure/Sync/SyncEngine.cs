using MailWarden.Core.Accounts;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Mime;
using MimeKit;

namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// One-way sync server → local for one account: folder list with roles, new messages as EML + row + full-text body,
/// server flags, server-side deletions and UIDVALIDITY resets. Never changes anything on the server.
/// </summary>
/// <remarks>
/// Order per folder: open (EXAMINE) → UIDVALIDITY check → deletions → new messages → flags → sync state. EML files
/// are written before their rows and deleted before their rows, so a crash leaves at most an orphan file that the next
/// run overwrites. Network/authentication failures and cancellation abort the pass; any other failure is recorded for
/// its folder and the next folder continues. Not thread-safe; one pass at a time.
/// </remarks>
public sealed class SyncEngine
{
    private const int PreviewMaxChars = 200;
    private const int BodyTextMaxChars = 100_000;

    private readonly IMailTransportFactory transportFactory;
    private readonly IAccountStore accounts;
    private readonly IFolderStore folders;
    private readonly IMessageStore messages;
    private readonly IEmlStore emlStore;
    private readonly IFolderSyncStateStore syncStates;
    private readonly SyncOptions options;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the engine.</summary>
    /// <param name="transportFactory">Opens the IMAP session of an account.</param>
    /// <param name="accounts">Account store (the account to sync is loaded from here).</param>
    /// <param name="folders">Folder store.</param>
    /// <param name="messages">Message store including the full-text index.</param>
    /// <param name="emlStore">Store of the raw messages.</param>
    /// <param name="syncStates">Sync progress per folder.</param>
    /// <param name="options">Sync settings.</param>
    /// <param name="timeProvider">Clock for the initial window (local calendar day) and the sync timestamp.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public SyncEngine(
        IMailTransportFactory transportFactory,
        IAccountStore accounts,
        IFolderStore folders,
        IMessageStore messages,
        IEmlStore emlStore,
        IFolderSyncStateStore syncStates,
        SyncOptions options,
        TimeProvider timeProvider)
    {
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.folders = folders ?? throw new ArgumentNullException(nameof(folders));
        this.messages = messages ?? throw new ArgumentNullException(nameof(messages));
        this.emlStore = emlStore ?? throw new ArgumentNullException(nameof(emlStore));
        this.syncStates = syncStates ?? throw new ArgumentNullException(nameof(syncStates));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Runs one complete pass over the account (folder list, then every visible folder: Inbox, Junk, rest).</summary>
    /// <param name="accountId">Account to sync.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Counts per folder; folders that failed carry their error.</returns>
    /// <exception cref="ArgumentException">The account is unknown.</exception>
    /// <exception cref="AuthenticationFailedException">No token could be obtained (e.g. a sign-in is needed).</exception>
    /// <exception cref="MailTransportException">
    /// Network or authentication failure (protocol failures inside a folder are recorded instead), the folder list
    /// could not be read, or it contains no inbox (Protocol; nothing is changed then).
    /// </exception>
    /// <exception cref="StorageException">
    /// The folder list could not be stored or a removed folder could not be deleted. Failing EML deletes of removed
    /// folders are skipped; storage failures inside a folder (including EML deletes) are recorded for that folder.
    /// </exception>
    /// <exception cref="OperationCanceledException">The pass was cancelled.</exception>
    public async Task<SyncReport> SyncAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        Account account = await accounts.GetAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"Unknown account id {accountId}.", nameof(accountId));

        // The transport is disposed on every path, including aborts.
        await using IMailTransport transport = await transportFactory.ConnectAsync(account, ct).ConfigureAwait(false);

        FolderListResult folderList = await SyncFolderListAsync(account, transport, ct).ConfigureAwait(false);

        var folderResults = new List<FolderSyncResult>(folderList.SyncFolders.Count);
        foreach (MailFolder folder in OrderForSync(folderList.SyncFolders))
        {
            folderResults.Add(await SyncFolderAsync(account, transport, folder, ct).ConfigureAwait(false));
        }

        return new SyncReport(account.Id, folderList.Added, folderList.Removed, folderResults);
    }

    // R1: visible server folders are upserted (role from the resolver, stored UIDVALIDITY kept, 0 for new ones);
    // stored folders that vanished or became hidden are removed (EML files first, then the folder with its rows).
    private async Task<FolderListResult> SyncFolderListAsync(Account account, IMailTransport transport, CancellationToken ct)
    {
        IReadOnlyList<RemoteFolder> remoteFolders = await transport.ListFoldersAsync(ct).ConfigureAwait(false);
        IReadOnlyList<MailFolder> storedFolders = await folders.ListAsync(account.Id, ct).ConfigureAwait(false);

        // IMAP folder names are case-sensitive, like the (account_id, full_name) key of the folder table.
        var storedByName = new Dictionary<string, MailFolder>(StringComparer.Ordinal);
        foreach (MailFolder storedFolder in storedFolders)
        {
            storedByName[storedFolder.FullName] = storedFolder;
        }

        // Visible folders by name; a name listed twice counts once (first listing wins).
        var visibleRoles = new Dictionary<string, FolderRole>(StringComparer.Ordinal);
        foreach (RemoteFolder remoteFolder in remoteFolders)
        {
            if (visibleRoles.ContainsKey(remoteFolder.FullName))
            {
                continue;
            }

            FolderClassification classification = FolderRoleResolver.Resolve(
                account.Provider,
                remoteFolder.FullName,
                remoteFolder.Separator,
                remoteFolder.Flags);
            if (!classification.Hidden)
            {
                visibleRoles.Add(remoteFolder.FullName, classification.Role);
            }
        }

        // An account always has an inbox; a list without one is incomplete and must not remove local folders.
        if (!visibleRoles.ContainsValue(FolderRole.Inbox))
        {
            throw new MailTransportException(
                MailTransportFailure.Protocol,
                "The server folder list contains no inbox; no local folder was changed.");
        }

        int addedCount = 0;
        var syncFolders = new List<MailFolder>(visibleRoles.Count);
        foreach ((string fullName, FolderRole role) in visibleRoles)
        {
            MailFolder? storedFolder = storedByName.GetValueOrDefault(fullName);
            if (storedFolder is not null && storedFolder.Role == role)
            {
                syncFolders.Add(storedFolder);
                continue;
            }

            if (storedFolder is null)
            {
                addedCount++;
            }

            var folderToStore = new MailFolder(0, account.Id, fullName, role, storedFolder?.UidValidity ?? 0);
            syncFolders.Add(await folders.UpsertAsync(folderToStore, ct).ConfigureAwait(false));
        }

        int removedCount = 0;
        foreach (MailFolder storedFolder in storedFolders)
        {
            if (visibleRoles.ContainsKey(storedFolder.FullName))
            {
                continue;
            }

            IReadOnlyDictionary<uint, string?> emlPaths = await messages
                .GetUidEmlPathsAsync(storedFolder.Id, ct)
                .ConfigureAwait(false);
            await DeleteEmlFilesSkippingFailuresAsync(emlPaths.Values, ct).ConfigureAwait(false);

            // Cascades to rows, full-text entries and sync state.
            await folders.DeleteAsync(storedFolder.Id, ct).ConfigureAwait(false);
            removedCount++;
        }

        return new FolderListResult(syncFolders, addedCount, removedCount);
    }

    // R9: only network/authentication failures and cancellation abort the pass; everything else stays in its folder.
    private async Task<FolderSyncResult> SyncFolderAsync(
        Account account,
        IMailTransport transport,
        MailFolder folder,
        CancellationToken ct)
    {
        var progress = new FolderProgress();
        try
        {
            await SyncFolderContentAsync(account, transport, folder, progress, ct).ConfigureAwait(false);

            // Only per-message failures: the folder completed, the failed UIDs are retried next time.
            string? messageFailures = progress.FailedMessages == 0
                ? null
                : $"{progress.FailedMessages} message(s) failed (first: {progress.FirstFailureType})";
            return progress.ToResult(folder, messageFailures);
        }
        catch (Exception exception) when (!AbortsSync(exception))
        {
            return progress.ToResult(folder, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task SyncFolderContentAsync(
        Account account,
        IMailTransport transport,
        MailFolder folder,
        FolderProgress progress,
        CancellationToken ct)
    {
        RemoteFolderState serverState = await transport.OpenFolderAsync(folder.FullName, ct).ConfigureAwait(false);

        // UIDVALIDITY 0 is not a valid IMAP value; nothing of this folder is stored or changed then.
        if (serverState.UidValidity == 0)
        {
            throw new MailTransportException(
                MailTransportFailure.Protocol,
                "The server reported UIDVALIDITY 0 for the folder.");
        }

        // R3: a changed UIDVALIDITY invalidates every stored UID of the folder; never keep a mixed copy.
        if (folder.UidValidity != 0 && folder.UidValidity != serverState.UidValidity)
        {
            IReadOnlyDictionary<uint, string?> emlPaths = await messages
                .GetUidEmlPathsAsync(folder.Id, ct)
                .ConfigureAwait(false);
            await DeleteMessagesAsync(folder.Id, emlPaths, [.. emlPaths.Keys], ct).ConfigureAwait(false);
            await syncStates.DeleteAsync(folder.Id, ct).ConfigureAwait(false);
            progress.UidValidityReset = true;
        }

        if (folder.UidValidity != serverState.UidValidity)
        {
            folder = await folders
                .UpsertAsync(folder with { UidValidity = serverState.UidValidity }, ct)
                .ConfigureAwait(false);
        }

        // R4: local messages whose UID is gone on the server are removed (files first).
        IReadOnlyList<uint> serverUids = await transport.SearchUidsAsync(since: null, ct).ConfigureAwait(false);
        var serverUidSet = new HashSet<uint>(serverUids);
        IReadOnlyDictionary<uint, MessageFlags> localFlags = await messages
            .GetUidFlagsAsync(folder.Id, ct)
            .ConfigureAwait(false);
        List<uint> vanishedUids = [.. localFlags.Keys.Where(uid => !serverUidSet.Contains(uid))];
        if (vanishedUids.Count > 0)
        {
            IReadOnlyDictionary<uint, string?> emlPaths = await messages
                .GetUidEmlPathsAsync(folder.Id, ct)
                .ConfigureAwait(false);
            await DeleteMessagesAsync(folder.Id, emlPaths, vanishedUids, ct).ConfigureAwait(false);
            progress.Deleted = vanishedUids.Count;
        }

        // R5: new UIDs above the high-water mark; the first sync is limited to the initial window.
        FolderSyncState? syncState = await syncStates.GetAsync(folder.Id, ct).ConfigureAwait(false);
        uint highestSyncedUid = syncState?.HighestSyncedUid ?? 0;
        List<uint> candidateUids =
            [.. serverUids.Where(uid => uid > highestSyncedUid && !localFlags.ContainsKey(uid)).Order()];
        if (syncState is null && candidateUids.Count > 0)
        {
            DateTimeOffset? windowStart = InitialWindowStart();
            if (windowStart is not null)
            {
                IReadOnlyList<uint> windowUids = await transport
                    .SearchUidsAsync(windowStart, ct)
                    .ConfigureAwait(false);
                var windowUidSet = new HashSet<uint>(windowUids);
                candidateUids.RemoveAll(uid => !windowUidSet.Contains(uid));
            }
        }

        // One message never blocks its folder: non-aborting failures are collected and retried next time.
        uint? lowestFailedUid = null;
        foreach (uint uid in candidateUids)
        {
            try
            {
                if (await FetchAndStoreAsync(account, transport, folder, uid, ct).ConfigureAwait(false))
                {
                    progress.Added++;
                }
            }
            catch (Exception exception) when (!AbortsSync(exception))
            {
                progress.FailedMessages++;
                progress.FirstFailureType ??= exception.GetType().Name;
                lowestFailedUid ??= uid;
            }
        }

        // Candidates are ascending, so the first failed UID is the lowest; it and every newer UID are retried.
        uint highestServerUid = serverUids.Count == 0 ? 0 : serverUids.Max();
        uint newHighestSyncedUid = lowestFailedUid is uint failedUid
            ? failedUid - 1
            : Math.Max(highestSyncedUid, highestServerUid);

        // R7: server flags win for messages that were stored before this pass and still exist on the server.
        if (localFlags.Keys.Any(serverUidSet.Contains))
        {
            ulong changedSinceModSeq = ChangedSinceModSeq(syncState?.HighestModSeq ?? 0, serverState.HighestModSeq);
            IReadOnlyList<RemoteFlags> remoteFlags = await transport
                .FetchFlagsAsync(changedSinceModSeq, ct)
                .ConfigureAwait(false);

            var changedFlags = new Dictionary<uint, MessageFlags>();
            foreach (RemoteFlags remote in remoteFlags)
            {
                if (serverUidSet.Contains(remote.Uid)
                    && localFlags.TryGetValue(remote.Uid, out MessageFlags storedFlags)
                    && storedFlags != remote.Flags)
                {
                    changedFlags[remote.Uid] = remote.Flags;
                }
            }

            await messages.UpdateFlagsAsync(folder.Id, changedFlags, ct).ConfigureAwait(false);
            progress.FlagsUpdated = changedFlags.Count;
        }

        // R8: the state only moves forward after the folder completed.
        var completedState = new FolderSyncState(
            folder.Id,
            newHighestSyncedUid,
            serverState.HighestModSeq,
            timeProvider.GetUtcNow());
        await syncStates.SaveAsync(completedState, ct).ConfigureAwait(false);
    }

    // R5/R6/R11: fetch → EML file → parse the stored bytes → row with full-text body. False when the UID vanished.
    private async Task<bool> FetchAndStoreAsync(
        Account account,
        IMailTransport transport,
        MailFolder folder,
        uint uid,
        CancellationToken ct)
    {
        RemoteMessage? remoteMessage = await transport.FetchMessageAsync(uid, ct).ConfigureAwait(false);
        if (remoteMessage is null)
        {
            return false;
        }

        // Owned from here on: disposed on every path, including failures of the path or the write.
        await using Stream remoteContent = remoteMessage.Content;
        string emlPath = emlStore.RelativePathFor(account.Id, folder.Id, uid);
        await emlStore.WriteAsync(emlPath, remoteContent, ct).ConfigureAwait(false);

        // Parse what was stored, so row and file always describe the same bytes.
        ParsedContent content;
        Stream storedContent = await emlStore.OpenReadAsync(emlPath, ct).ConfigureAwait(false);
        await using (storedContent.ConfigureAwait(false))
        {
            content = await ParseAsync(storedContent, ct).ConfigureAwait(false);
        }

        var row = new MailMessage(
            Id: 0,
            AccountId: account.Id,
            FolderId: folder.Id,
            Uid: uid,
            MessageId: content.MessageId,
            Subject: content.Subject,
            FromAddress: content.FromAddress,
            FromName: content.FromName,
            Date: content.Date ?? remoteMessage.InternalDate,
            Flags: remoteMessage.Flags,
            HasAttachments: content.HasAttachments,
            Size: remoteMessage.Size,
            EmlPath: emlPath,
            TextPreview: MimeBodyText.Cut(content.BodyText, PreviewMaxChars));
        await messages.UpsertAsync(row, content.BodyText, ct).ConfigureAwait(false);
        return true;
    }

    // R6: header fields and body text. Any failure while loading or extracting (except cancellation) means
    // "unparseable": the message is stored with empty fields. persistent: the stream is seekable and stays open while
    // the message is used, so MimeKit does not copy the content into memory again.
    private static async Task<ParsedContent> ParseAsync(Stream storedContent, CancellationToken ct)
    {
        try
        {
            using MimeMessage message = await MimeMessage
                .LoadAsync(storedContent, persistent: true, ct)
                .ConfigureAwait(false);
            MailboxAddress? sender = message.From.Mailboxes.FirstOrDefault();

            // A missing or unparseable Date header (MimeKit reports MinValue) falls back to INTERNALDATE.
            DateTimeOffset? date = message.Headers.Contains(HeaderId.Date) && message.Date != DateTimeOffset.MinValue
                ? message.Date
                : null;

            return new ParsedContent(
                MessageId: message.MessageId,
                Subject: message.Subject ?? string.Empty,
                FromAddress: sender?.Address ?? string.Empty,
                FromName: sender?.Name ?? string.Empty,
                Date: date,
                HasAttachments: message.Attachments.Any(),
                BodyText: MimeBodyText.Extract(message, BodyTextMaxChars));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ParsedContent.Unparseable;
        }
    }

    private async Task DeleteMessagesAsync(
        long folderId,
        IReadOnlyDictionary<uint, string?> emlPaths,
        IReadOnlyCollection<uint> uids,
        CancellationToken ct)
    {
        await DeleteEmlFilesAsync(emlPaths, uids, ct).ConfigureAwait(false);
        await messages.DeleteByUidsAsync(folderId, uids, ct).ConfigureAwait(false);
    }

    // R11: files go before their rows; a missing file is fine (IEmlStore.DeleteAsync is a no-op then).
    private async Task DeleteEmlFilesAsync(
        IReadOnlyDictionary<uint, string?> emlPaths,
        IEnumerable<uint> uids,
        CancellationToken ct)
    {
        foreach (uint uid in uids)
        {
            if (emlPaths.TryGetValue(uid, out string? emlPath) && emlPath is not null)
            {
                await emlStore.DeleteAsync(emlPath, ct).ConfigureAwait(false);
            }
        }
    }

    // R1 (folder removal): a file that cannot be deleted is skipped so that the folder row still goes; the orphan file
    // is left for a later cleanup. Cancellation still propagates.
    private async Task DeleteEmlFilesSkippingFailuresAsync(IEnumerable<string?> emlPaths, CancellationToken ct)
    {
        foreach (string? emlPath in emlPaths)
        {
            if (emlPath is null)
            {
                continue;
            }

            try
            {
                await emlStore.DeleteAsync(emlPath, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Skipped on purpose, see method comment.
            }
        }
    }

    // Local midnight InitialSyncDays days back; null = no window (0 days, or a window reaching before year 1).
    private DateTimeOffset? InitialWindowStart()
    {
        if (options.InitialSyncDays == 0)
        {
            return null;
        }

        DateTime today = timeProvider.GetLocalNow().Date;
        if (options.InitialSyncDays >= (today - DateTime.MinValue).TotalDays)
        {
            return null;
        }

        DateTime windowStartDay = today.AddDays(-options.InitialSyncDays);
        return new DateTimeOffset(windowStartDay, timeProvider.LocalTimeZone.GetUtcOffset(windowStartDay));
    }

    // CHANGEDSINCE only when both sides know CONDSTORE; a server value below the stored one (server-side reset without
    // UIDVALIDITY change) falls back to a full flag fetch so that no change is missed.
    private static ulong ChangedSinceModSeq(ulong storedModSeq, ulong serverModSeq)
    {
        return storedModSeq > 0 && serverModSeq > 0 && storedModSeq <= serverModSeq ? storedModSeq : 0;
    }

    // R2: Inbox first, then Junk, then all others by name (case-insensitive, ordinal as tie-breaker).
    private static IEnumerable<MailFolder> OrderForSync(IEnumerable<MailFolder> syncFolders)
    {
        return syncFolders
            .OrderBy(folder => folder.Role switch
            {
                FolderRole.Inbox => 0,
                FolderRole.Junk => 1,
                _ => 2,
            })
            .ThenBy(folder => folder.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(folder => folder.FullName, StringComparer.Ordinal);
    }

    private static bool AbortsSync(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => true,
            AuthenticationFailedException => true,
            MailTransportException transportException => transportException.Failure
                is MailTransportFailure.Network or MailTransportFailure.Authentication,
            _ => false,
        };
    }

    private sealed record FolderListResult(IReadOnlyList<MailFolder> SyncFolders, int Added, int Removed);

    private sealed record ParsedContent(
        string? MessageId,
        string Subject,
        string FromAddress,
        string FromName,
        DateTimeOffset? Date,
        bool HasAttachments,
        string BodyText)
    {
        public static ParsedContent Unparseable { get; } =
            new(null, string.Empty, string.Empty, string.Empty, null, false, string.Empty);
    }

    // Counts of one folder; kept outside the try so that a failed folder still reports what was done.
    private sealed class FolderProgress
    {
        public int Added { get; set; }

        public int FlagsUpdated { get; set; }

        public int Deleted { get; set; }

        public bool UidValidityReset { get; set; }

        public int FailedMessages { get; set; }

        public string? FirstFailureType { get; set; }

        public FolderSyncResult ToResult(MailFolder folder, string? error) =>
            new(folder.FullName, folder.Role, Added, FlagsUpdated, Deleted, UidValidityReset, error);
    }
}
