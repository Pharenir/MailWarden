using System.Globalization;
using MailWarden.Core.Accounts;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Mime;
using MailWarden.Infrastructure.Storage;
using MimeKit;

namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// One-way sync server → local for one account: folder list with roles, new messages as EML + row + full-text body,
/// server flags, server-side deletions and UIDVALIDITY resets. Never changes anything on the server.
/// </summary>
/// <remarks>
/// Order per folder: open (EXAMINE) → UIDVALIDITY check → deletions → new messages → flags → sync state. EML files
/// are written before their rows and deleted before their rows, so a crash leaves at most an orphan file that the next
/// run overwrites.
/// <para>
/// New messages: on every pass the candidates are all server UIDs that are not stored locally and lie above the floor
/// (<see cref="FolderSyncState.SkippedUpToUid"/>). There is no high-water mark, so a UID the server did not deliver
/// in an earlier pass (missing from SEARCH, FETCH returned nothing, or a per-message failure) is retried on the next
/// pass for as long as it exists on the server. The floor is decided once, at the first completed sync of the folder:
/// 0 without an initial window (<see cref="SyncOptions.InitialSyncDays"/> 0) or for an empty folder; otherwise the
/// lowest UID inside the window − 1, or the highest server UID when no message lies inside the window. It never
/// changes afterwards, until a UIDVALIDITY reset deletes the state. Accepted consequence: a message outside the window
/// whose UID is above the floor (e.g. moved into the folder later with an old date) is fetched from the second pass on.
/// </para>
/// <para>
/// Candidates are fetched in ascending batches of <see cref="SyncOptions.FetchBatchSize"/> with
/// <see cref="IMailTransport.FetchMessagesAsync"/>. A batch that fails with a protocol failure falls back to single
/// fetches of the UIDs it did not deliver yet.
/// </para>
/// <para>
/// Authentication failures and cancellation abort the pass, as do failures while connecting or reading the folder list.
/// A network failure ends the current folder; any other failure of one message is collected and the folder continues:
/// the folder completes with Error "&lt;n&gt; message(s) failed (first: &lt;Type.Name&gt;)", its state is saved as
/// usual and the failed UIDs are retried next pass because they are still missing locally. Any other failure of the
/// folder itself (including network failures) is recorded as "&lt;ExceptionType&gt;: &lt;message&gt;", the folder keeps
/// its previous sync state and the next folder continues. Before every folder after the first, a lost connection
/// (<see cref="IMailTransport.IsConnected"/> false) is replaced by a new one, at most
/// <see cref="SyncOptions.MaxReconnectsPerPass"/> times per pass. Accepted consequence: a message that always makes
/// MailKit drop the connection (e.g. an <c>ImapProtocolException</c> while reading it) costs the rest of its folder
/// in every pass, not the whole account. Not thread-safe; one pass at a time.
/// </para>
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
    /// <param name="syncStates">Sync state per folder (initial-window floor, CONDSTORE value).</param>
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

    /// <summary>Runs one complete pass over the account without progress reports.</summary>
    /// <param name="accountId">Account to sync.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Counts per folder; folders that failed carry their error.</returns>
    /// <exception cref="ArgumentException">The account is unknown.</exception>
    /// <exception cref="AuthenticationFailedException">No token could be obtained (e.g. a sign-in is needed).</exception>
    /// <exception cref="MailTransportException">See <see cref="SyncAccountAsync(Guid, IProgress{SyncProgress}?, CancellationToken)"/>.</exception>
    /// <exception cref="StorageException">See <see cref="SyncAccountAsync(Guid, IProgress{SyncProgress}?, CancellationToken)"/>.</exception>
    /// <exception cref="OperationCanceledException">The pass was cancelled.</exception>
    public Task<SyncReport> SyncAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        return SyncAccountAsync(accountId, progress: null, ct);
    }

    /// <summary>
    /// Runs one complete pass over the account (folder list, then every visible folder: Inbox, Junk, rest) and reports
    /// its progress.
    /// </summary>
    /// <param name="accountId">Account to sync.</param>
    /// <param name="progress">
    /// Receives <see cref="SyncProgress"/> synchronously via <see cref="IProgress{T}.Report"/>: when a folder starts,
    /// when its candidates are known, after every batch and when the folder ends (also when the folder fails). No
    /// folder-end report is sent when the pass aborts (cancellation, authentication, reconnect failure or budget). An
    /// exception thrown by <see cref="IProgress{T}.Report"/> aborts the pass. Null = no reports.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Counts per folder; folders that failed carry their error.</returns>
    /// <exception cref="ArgumentException">The account is unknown.</exception>
    /// <exception cref="AuthenticationFailedException">No token could be obtained (e.g. a sign-in is needed).</exception>
    /// <exception cref="MailTransportException">
    /// Connecting or reconnecting failed; authentication failure; the folder list could not be read or contains no
    /// inbox (Protocol; nothing is changed then); or the connection was lost more often than
    /// <see cref="SyncOptions.MaxReconnectsPerPass"/> allows (Network). Network and protocol failures inside a folder
    /// are recorded for that folder instead.
    /// </exception>
    /// <exception cref="StorageException">
    /// The folder list could not be stored or a removed folder could not be deleted. Failing EML deletes of removed
    /// folders are skipped; storage failures inside a folder (including EML deletes) are recorded for that folder.
    /// </exception>
    /// <exception cref="OperationCanceledException">The pass was cancelled.</exception>
    public async Task<SyncReport> SyncAccountAsync(
        Guid accountId,
        IProgress<SyncProgress>? progress,
        CancellationToken ct = default)
    {
        Account account = await accounts.GetAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"Unknown account id {accountId}.", nameof(accountId));

        // The current transport is disposed on every path, including aborts; a replaced one is disposed before its
        // successor is connected.
        IMailTransport? transport = await transportFactory.ConnectAsync(account, ct).ConfigureAwait(false);
        try
        {
            FolderListResult folderList = await SyncFolderListAsync(account, transport, ct).ConfigureAwait(false);

            List<MailFolder> orderedFolders = [.. OrderForSync(folderList.SyncFolders)];
            var folderResults = new List<FolderSyncResult>(orderedFolders.Count);
            int lostConnections = 0;
            for (int folderIndex = 0; folderIndex < orderedFolders.Count; folderIndex++)
            {
                MailFolder folder = orderedFolders[folderIndex];

                // B4 (fix.2 K1): before every folder after the first, a lost connection is replaced (bounded per
                // pass), whether or not the previous folder failed. Nothing is reconnected after the last folder.
                if (folderIndex > 0 && !transport.IsConnected)
                {
                    lostConnections++;
                    if (lostConnections > options.MaxReconnectsPerPass)
                    {
                        throw new MailTransportException(
                            MailTransportFailure.Network,
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"The connection to the mail server was lost too often during the pass ({lostConnections} times, at most {options.MaxReconnectsPerPass} reconnects allowed)."));
                    }

                    IMailTransport lostTransport = transport;
                    transport = null;
                    await lostTransport.DisposeAsync().ConfigureAwait(false);
                    transport = await transportFactory.ConnectAsync(account, ct).ConfigureAwait(false);
                }

                var reporter = new FolderProgressReporter(
                    progress,
                    account.Id,
                    folder,
                    folderIndex + 1,
                    orderedFolders.Count);
                folderResults.Add(await SyncFolderAsync(account, transport, folder, reporter, ct).ConfigureAwait(false));
            }

            return new SyncReport(account.Id, folderList.Added, folderList.Removed, folderResults);
        }
        finally
        {
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
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

            // A file that cannot be deleted is skipped so that the folder row still goes (orphan left for a cleanup).
            await EmlFiles.DeleteSkippingFailuresAsync(emlStore, emlPaths.Values, ct).ConfigureAwait(false);

            // Cascades to rows, full-text entries and sync state.
            await folders.DeleteAsync(storedFolder.Id, ct).ConfigureAwait(false);
            removedCount++;
        }

        return new FolderListResult(syncFolders, addedCount, removedCount);
    }

    // R9 (B4): only authentication failures and cancellation abort the pass; everything else, including network
    // failures, stays in its folder.
    private async Task<FolderSyncResult> SyncFolderAsync(
        Account account,
        IMailTransport transport,
        MailFolder folder,
        FolderProgressReporter reporter,
        CancellationToken ct)
    {
        var progress = new FolderProgress();
        reporter.Report(progress);
        try
        {
            await SyncFolderContentAsync(account, transport, folder, progress, reporter, ct).ConfigureAwait(false);

            // Only per-message failures: the folder completed, the failed UIDs are retried next time.
            string? messageFailures = progress.FailedMessages == 0
                ? null
                : $"{progress.FailedMessages} message(s) failed (first: {progress.FirstFailureType})";
            reporter.Report(progress);
            return progress.ToResult(folder, messageFailures);
        }
        catch (Exception exception) when (!AbortsPass(exception))
        {
            reporter.Report(progress);
            return progress.ToResult(folder, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task SyncFolderContentAsync(
        Account account,
        IMailTransport transport,
        MailFolder folder,
        FolderProgress progress,
        FolderProgressReporter reporter,
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

        // R5 (fix.3 G1): candidates are every server UID that is not stored locally and lies above the floor, so a UID
        // the server did not deliver in an earlier pass is retried as long as it exists. The floor is decided once,
        // at the first completed sync of the folder, and stays fixed until a UIDVALIDITY reset deletes the state.
        FolderSyncState? syncState = await syncStates.GetAsync(folder.Id, ct).ConfigureAwait(false);
        HashSet<uint>? windowUidSet = null;
        uint skippedUpToUid;
        if (syncState is not null)
        {
            skippedUpToUid = syncState.SkippedUpToUid;
        }
        else
        {
            (skippedUpToUid, windowUidSet) = await InitialFloorAsync(transport, serverUids, ct).ConfigureAwait(false);
        }

        List<uint> candidateUids =
        [
            .. serverUids
                .Where(uid => uid > skippedUpToUid
                    && !localFlags.ContainsKey(uid)
                    && (windowUidSet is null || windowUidSet.Contains(uid)))
                .Order(),
        ];

        progress.Candidates = candidateUids.Count;
        reporter.Report(progress);

        // B2: ascending batches; one message never blocks its folder: non-aborting failures are collected and retried
        // next time (they stay missing locally and above the floor).
        for (int batchStart = 0; batchStart < candidateUids.Count; batchStart += options.FetchBatchSize)
        {
            List<uint> batchUids = candidateUids.GetRange(
                batchStart,
                Math.Min(options.FetchBatchSize, candidateUids.Count - batchStart));
            await FetchBatchAsync(account, transport, folder, batchUids, progress, ct).ConfigureAwait(false);
            progress.Processed += batchUids.Count;
            reporter.Report(progress);
        }

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

        // R8 (fix.3 G2): saved only after the folder completed; the floor of the first sync is kept unchanged and
        // per-message failures do not change it.
        var completedState = new FolderSyncState(
            folder.Id,
            skippedUpToUid,
            serverState.HighestModSeq,
            timeProvider.GetUtcNow());
        await syncStates.SaveAsync(completedState, ct).ConfigureAwait(false);
    }

    // B2: one batch fetch; every delivered message is stored with the per-message failure rule. A Protocol failure of
    // the batch falls back to single fetches of the UIDs not delivered yet, each with the per-message failure rule.
    // UIDs the server returns nothing for stay missing and are retried next pass. Other failures end the folder.
    private async Task FetchBatchAsync(
        Account account,
        IMailTransport transport,
        MailFolder folder,
        IReadOnlyList<uint> batchUids,
        FolderProgress progress,
        CancellationToken ct)
    {
        var deliveredUids = new HashSet<uint>();
        try
        {
            await transport
                .FetchMessagesAsync(
                    batchUids,
                    async (remoteMessage, messageCt) =>
                    {
                        deliveredUids.Add(remoteMessage.Uid);
                        await StoreCountingFailuresAsync(account, folder, remoteMessage, progress, messageCt)
                            .ConfigureAwait(false);
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (MailTransportException exception) when (exception.Failure == MailTransportFailure.Protocol)
        {
            foreach (uint uid in batchUids.Where(uid => !deliveredUids.Contains(uid)))
            {
                RemoteMessage? remoteMessage;
                try
                {
                    remoteMessage = await transport.FetchMessageAsync(uid, ct).ConfigureAwait(false);
                }
                catch (Exception singleException) when (!AbortsFolder(singleException))
                {
                    progress.CountFailure(singleException);
                    continue;
                }

                if (remoteMessage is not null)
                {
                    await StoreCountingFailuresAsync(account, folder, remoteMessage, progress, ct).ConfigureAwait(false);
                }
            }
        }
    }

    // F1: a non-aborting failure while storing one message is counted; the content is disposed on every path.
    private async Task StoreCountingFailuresAsync(
        Account account,
        MailFolder folder,
        RemoteMessage remoteMessage,
        FolderProgress progress,
        CancellationToken ct)
    {
        try
        {
            await StoreAsync(account, folder, remoteMessage, ct).ConfigureAwait(false);
            progress.Added++;
        }
        catch (Exception exception) when (!AbortsFolder(exception))
        {
            progress.CountFailure(exception);
        }
    }

    // R5/R6/R11: EML file → parse the stored bytes → row with full-text body.
    private async Task StoreAsync(Account account, MailFolder folder, RemoteMessage remoteMessage, CancellationToken ct)
    {
        // Owned from here on: disposed on every path, including failures of the path or the write.
        await using Stream remoteContent = remoteMessage.Content;
        uint uid = remoteMessage.Uid;
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

    // G1 (first sync of a folder, no state): floor and the UIDs inside the initial window (null = no window, every
    // missing UID is a candidate). Floor: no window or empty folder → 0; window UIDs found → lowest window UID − 1;
    // no window UID found → highest server UID (everything was outside the window).
    private async Task<(uint Floor, HashSet<uint>? WindowUids)> InitialFloorAsync(
        IMailTransport transport,
        IReadOnlyList<uint> serverUids,
        CancellationToken ct)
    {
        DateTimeOffset? windowStart = InitialWindowStart();
        if (windowStart is null || serverUids.Count == 0)
        {
            return (0, null);
        }

        IReadOnlyList<uint> windowUids = await transport.SearchUidsAsync(windowStart, ct).ConfigureAwait(false);
        if (windowUids.Count == 0)
        {
            return (serverUids.Max(), []);
        }

        // B7: UID 0 is not a valid IMAP UID (like UIDVALIDITY 0); the folder fails before anything is stored for it.
        uint lowestWindowUid = windowUids.Min();
        if (lowestWindowUid == 0)
        {
            throw new MailTransportException(
                MailTransportFailure.Protocol,
                "The server returned UID 0 in the initial window search.");
        }

        return (lowestWindowUid - 1, [.. windowUids]);
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

    // B4: cancellation and authentication failures end the whole pass.
    private static bool AbortsPass(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => true,
            AuthenticationFailedException => true,
            MailTransportException transportException =>
                transportException.Failure == MailTransportFailure.Authentication,
            _ => false,
        };
    }

    // F1: besides the pass-aborting failures, a network failure ends the folder instead of counting as a failure of one
    // message (the session is most likely gone).
    private static bool AbortsFolder(Exception exception)
    {
        return AbortsPass(exception)
            || exception is MailTransportException { Failure: MailTransportFailure.Network };
    }

    private sealed record FolderListResult(IReadOnlyList<MailFolder> SyncFolders, int Added, int Removed);

    // Reports the progress of one folder; a null target makes every report a no-op.
    private sealed class FolderProgressReporter(
        IProgress<SyncProgress>? target,
        Guid accountId,
        MailFolder folder,
        int folderNumber,
        int folderCount)
    {
        public void Report(FolderProgress progress)
        {
            target?.Report(new SyncProgress(
                accountId,
                folder.Id,
                folder.Role,
                folderNumber,
                folderCount,
                progress.Processed,
                progress.Candidates));
        }
    }

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

        public int Candidates { get; set; }

        public int Processed { get; set; }

        public void CountFailure(Exception exception)
        {
            FailedMessages++;
            FirstFailureType ??= exception.GetType().Name;
        }

        public FolderSyncResult ToResult(MailFolder folder, string? error) =>
            new(folder.FullName, folder.Role, Added, FlagsUpdated, Deleted, UidValidityReset, error);
    }
}
