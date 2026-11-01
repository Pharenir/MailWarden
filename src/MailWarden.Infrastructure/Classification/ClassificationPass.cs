using MailWarden.Core.Accounts;
using MailWarden.Core.Classification;
using MailWarden.Core.Commands;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Storage;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// One classification pass over the newly arrived mail of an account: decides every new message of the Inbox and Junk
/// folders once, stores the decision and, in effective <see cref="ClassificationMode.Act"/>, enqueues the moves. Nothing
/// here talks to the server except <see cref="IMailFolderProvisioner"/>; the moves run later in the command cycle.
/// </summary>
/// <remarks>
/// <para>
/// Safety rules: a folder is only handled after a completed sync (it has a stored sync state), and the first pass over it
/// only sets its marker, so existing mail is never classified. Act is suspended while the address book is empty. A message
/// key in the log is never decided again. Retention deletes only mail this app moved to the Quarantine, by the stored
/// decision time.
/// </para>
/// <para>
/// A folder's entries and marker are committed in one transaction, after its commands were enqueued. After a crash in
/// between, a stored command can still run in the next cycle: the mail is then moved without a log entry. Retention never
/// deletes it for that reason, and it is decided again if it lands in the Inbox or Junk. Cancellation propagates as
/// <see cref="OperationCanceledException"/>; a folder whose commit did not run is handled again by the next pass.
/// </para>
/// <para>
/// A message whose EML is missing or no message at all gets the final outcome <see cref="ClassificationOutcome.ReadFailed"/>
/// and stays where it is. A readable message without one usable From address is caused by the sender and is routed with
/// <see cref="ClassificationRouting.RouteWithoutSender"/>, without a tier.
/// </para>
/// <para>
/// No report or exception of this class contains subject, body or address text. Reason texts go to the local decision
/// log only. Not thread-safe; one pass at a time.
/// </para>
/// </remarks>
public sealed class ClassificationPass
{
    // Fixed text, no message data: stands in the Override column of a decision without a tier.
    private const string NoUsableSenderReason = "Message has no single usable sender";

    private readonly IAccountStore accounts;
    private readonly IFolderStore folders;
    private readonly IFolderSyncStateStore syncStates;
    private readonly IMessageStore messages;
    private readonly IEmlStore emlStore;
    private readonly IContactStore contacts;
    private readonly IClassificationLog log;
    private readonly IMailCommandQueue queue;
    private readonly IMailFolderProvisioner provisioner;
    private readonly ClassifierSettings settings;
    private readonly ClassificationPolicy policy;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the pass. Settings and policy are validated here (fail fast).</summary>
    /// <param name="accounts">Account store.</param>
    /// <param name="folders">Folder store.</param>
    /// <param name="syncStates">Sync state per folder; a folder without one has no completed sync yet.</param>
    /// <param name="messages">Message store.</param>
    /// <param name="emlStore">Store of the raw messages.</param>
    /// <param name="contacts">Address book.</param>
    /// <param name="log">Decision log and markers.</param>
    /// <param name="queue">Command queue for moves and retention deletes.</param>
    /// <param name="provisioner">Creates the app folders on the server.</param>
    /// <param name="settings">Classifier thresholds and lists.</param>
    /// <param name="policy">Mode and retention.</param>
    /// <param name="timeProvider">Clock of the decision time and of the retention.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="ArgumentException">The settings or the policy are invalid.</exception>
    public ClassificationPass(
        IAccountStore accounts,
        IFolderStore folders,
        IFolderSyncStateStore syncStates,
        IMessageStore messages,
        IEmlStore emlStore,
        IContactStore contacts,
        IClassificationLog log,
        IMailCommandQueue queue,
        IMailFolderProvisioner provisioner,
        ClassifierSettings settings,
        ClassificationPolicy policy,
        TimeProvider timeProvider)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.folders = folders ?? throw new ArgumentNullException(nameof(folders));
        this.syncStates = syncStates ?? throw new ArgumentNullException(nameof(syncStates));
        this.messages = messages ?? throw new ArgumentNullException(nameof(messages));
        this.emlStore = emlStore ?? throw new ArgumentNullException(nameof(emlStore));
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        settings.Validate();
        policy.Validate();
    }

    /// <summary>Runs one pass over the account.</summary>
    /// <param name="accountId">Account to handle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The counts of this pass.</returns>
    /// <exception cref="ArgumentException">The account is unknown.</exception>
    /// <exception cref="MailTransportException">Creating the app folders failed; nothing was stored then.</exception>
    /// <exception cref="AuthenticationFailedException">Creating the app folders needs a sign-in; nothing was stored then.</exception>
    /// <exception cref="StorageException">The local store failed.</exception>
    /// <exception cref="OperationCanceledException">The pass was cancelled.</exception>
    public async Task<ClassificationPassReport> RunAsync(Guid accountId, CancellationToken ct = default)
    {
        // Step 1: account and snapshot of the sender directory.
        Account account = await accounts.GetAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"Unknown account id {accountId}.", nameof(accountId));
        ContactSenderDirectory senderDirectory = await CreateSenderDirectoryAsync(ct).ConfigureAwait(false);
        var classifier = new SpamClassifier(settings, senderDirectory, SpamClassifier.CreateDefaultSignals());

        // Step 2: without contacts every authenticated mail would land in the Screener, so Act is suspended.
        bool actSuspended = policy.Mode == ClassificationMode.Act && senderDirectory.ContactCount == 0;
        ClassificationMode effectiveMode = actSuspended ? ClassificationMode.RecordOnly : policy.Mode;

        // Step 3: before any decision. A failure propagates with nothing stored; the mail stays where it is.
        IReadOnlyList<MailFolder> accountFolders = await folders.ListAsync(account.Id, ct).ConfigureAwait(false);
        if (effectiveMode == ClassificationMode.Act
            && (FindFolder(accountFolders, FolderRole.Screener) is null
                || FindFolder(accountFolders, FolderRole.Quarantine) is null))
        {
            await provisioner.EnsureAppFoldersAsync(account, ct).ConfigureAwait(false);
            accountFolders = await folders.ListAsync(account.Id, ct).ConfigureAwait(false);
        }

        // Step 4: Inbox first, then Junk. A folder that was never synced has no UIDs to rely on yet.
        var state = new PassState(account.Id, classifier, effectiveMode, accountFolders);
        foreach (FolderRole role in new[] { FolderRole.Inbox, FolderRole.Junk })
        {
            foreach (MailFolder folder in accountFolders.Where(folder => folder.Role == role && folder.UidValidity != 0))
            {
                ct.ThrowIfCancellationRequested();
                await HandleFolderAsync(folder, state, ct).ConfigureAwait(false);
            }
        }

        // Step 5: retention, only when explicitly switched on and only while acting.
        ct.ThrowIfCancellationRequested();
        if (effectiveMode == ClassificationMode.Act && policy.QuarantineRetentionDays > 0)
        {
            await EnqueueRetentionDeletesAsync(state, ct).ConfigureAwait(false);
        }

        return new ClassificationPassReport(
            state.Classified,
            state.SkippedKnown,
            state.ReadFailed,
            state.MovesEnqueued,
            state.RetentionDeletesEnqueued,
            actSuspended,
            state.TierCounts,
            state.RetentionDeletesRejected);
    }

    private static MailFolder? FindFolder(IEnumerable<MailFolder> accountFolders, FolderRole role)
    {
        return accountFolders.FirstOrDefault(folder => folder.Role == role);
    }

    private static FolderRole TargetRole(ClassificationAction action)
    {
        return action switch
        {
            ClassificationAction.MoveToInbox => FolderRole.Inbox,
            ClassificationAction.MoveToScreener => FolderRole.Screener,
            ClassificationAction.MoveToJunk => FolderRole.Junk,
            ClassificationAction.MoveToQuarantine => FolderRole.Quarantine,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "The action has no target folder."),
        };
    }

    // Every failure means "no decision possible"; exception messages can quote message data and are not kept.
    private static ClassificationResult? TryClassify(SpamClassifier classifier, ClassificationInput input)
    {
        try
        {
            return classifier.Classify(input);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private async Task<ContactSenderDirectory> CreateSenderDirectoryAsync(CancellationToken ct)
    {
        IReadOnlyList<Contact> allContacts = await contacts.ListAsync(ct).ConfigureAwait(false);
        IReadOnlyList<Account> allAccounts = await accounts.ListAsync(ct).ConfigureAwait(false);
        return new ContactSenderDirectory(
            allContacts.Select(contact => contact.EmailAddress),
            allAccounts.Select(account => account.EmailAddress));
    }

    private async Task HandleFolderAsync(MailFolder folder, PassState state, CancellationToken ct)
    {
        // The sync stores UIDVALIDITY before its first fetch and the sync state only after the folder completed (a
        // UIDVALIDITY reset deletes the state). Without a state the local rows are an incomplete copy: old mail would look new.
        if (await syncStates.GetAsync(folder.Id, ct).ConfigureAwait(false) is null)
        {
            return;
        }

        IReadOnlyDictionary<uint, MessageFlags> localMessages = await messages
            .GetUidFlagsAsync(folder.Id, ct)
            .ConfigureAwait(false);
        ClassificationMarker? marker = await log.GetMarkerAsync(folder.Id, ct).ConfigureAwait(false);

        // First pass, or the folder's UIDs were reassigned: the mail present now counts as existing and is not classified.
        if (marker is null || marker.UidValidity != folder.UidValidity)
        {
            uint highestUid = localMessages.Keys.DefaultIfEmpty(0u).Max();
            await log
                .CommitAsync([], new ClassificationMarker(folder.Id, folder.UidValidity, highestUid), ct)
                .ConfigureAwait(false);
            return;
        }

        List<uint> newUids = [.. localMessages.Keys.Where(uid => uid > marker.ClassifiedUpToUid).Order()];
        if (newUids.Count == 0)
        {
            return;
        }

        var entries = new List<ClassificationLogEntry>(newUids.Count);
        var movesByTarget = new Dictionary<long, List<PendingMove>>();
        foreach (uint uid in newUids)
        {
            ct.ThrowIfCancellationRequested();
            MailMessage? message = await messages.GetByUidAsync(folder.Id, uid, ct).ConfigureAwait(false);
            if (message is null)
            {
                continue;
            }

            // The key needs the top-most Received header, so the EML is read before the known check. One decision per
            // message: also a second copy in this pass counts as known.
            ClassificationMessage? content = await TryReadContentAsync(message, ct).ConfigureAwait(false);
            string messageKey = MessageKey.For(message, content?.TopReceived);
            if (!state.DecidedKeys.Add(messageKey)
                || await log.GetAsync(state.AccountId, messageKey, ct).ConfigureAwait(false) is not null)
            {
                state.SkippedKnown++;
                continue;
            }

            ClassificationLogEntry entry = Decide(folder, message, messageKey, content, state, out MailFolder? target);
            if (target is not null)
            {
                if (!movesByTarget.TryGetValue(target.Id, out List<PendingMove>? targetMoves))
                {
                    targetMoves = [];
                    movesByTarget.Add(target.Id, targetMoves);
                }

                targetMoves.Add(new PendingMove(entries.Count, uid));
            }

            entries.Add(entry);
        }

        // Marker and entries only after enqueueing, so that a crash can never lose a planned move. The other way round a
        // crash leaves a stored command without an entry; see the class remarks.
        await EnqueueMovesAsync(folder, movesByTarget, entries, state, ct).ConfigureAwait(false);
        await log
            .CommitAsync(entries, new ClassificationMarker(folder.Id, folder.UidValidity, newUids[^1]), ct)
            .ConfigureAwait(false);
    }

    // The only way from a stored message to its EML content, used for the key of the pass and of the retention. Null when
    // the message has no EML or the file cannot be read; the top-most Received header is unknown then.
    private async Task<ClassificationMessage?> TryReadContentAsync(MailMessage message, CancellationToken ct)
    {
        if (message.EmlPath is null)
        {
            return null;
        }

        try
        {
            using var buffer = new MemoryStream();
            await using (Stream eml = await emlStore.OpenReadAsync(message.EmlPath, ct).ConfigureAwait(false))
            {
                await eml.CopyToAsync(buffer, ct).ConfigureAwait(false);
            }

            buffer.Position = 0;
            return await ClassificationInputReader.ReadMessageAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return null;
        }
    }

    // Tier and routing of one message. The outcome Enqueued is provisional: EnqueueMovesAsync replaces it when the queue
    // rejects.
    private ClassificationLogEntry Decide(
        MailFolder folder,
        MailMessage message,
        string messageKey,
        ClassificationMessage? content,
        PassState state,
        out MailFolder? target)
    {
        target = null;

        ClassificationResult? result = null;
        bool hasDecision = content is { Parsed: true };
        if (content?.Input is ClassificationInput input)
        {
            result = TryClassify(state.Classifier, input);
            hasDecision = result is not null;
        }

        if (!hasDecision)
        {
            state.ReadFailed++;
            return CreateEntry(folder, message, messageKey, state, ClassificationOutcome.ReadFailed, ClassificationAction.None, null, null);
        }

        ClassificationAction action;
        if (result is null)
        {
            // Readable, but no single usable sender.
            action = ClassificationRouting.RouteWithoutSender(folder.Role);
        }
        else
        {
            state.Classified++;
            state.TierCounts[result.Tier]++;
            action = ClassificationRouting.Route(folder.Role, result.Tier);
        }

        ClassificationOutcome outcome;
        if (action == ClassificationAction.None)
        {
            outcome = ClassificationOutcome.NoAction;
        }
        else if (state.EffectiveMode == ClassificationMode.RecordOnly)
        {
            outcome = ClassificationOutcome.Recorded;
        }
        else
        {
            target = FindFolder(state.AccountFolders, TargetRole(action));
            outcome = target is null ? ClassificationOutcome.TargetMissing : ClassificationOutcome.Enqueued;
        }

        string? overrideReason = result is null ? NoUsableSenderReason : result.Override;
        return CreateEntry(folder, message, messageKey, state, outcome, action, result, overrideReason);
    }

    private ClassificationLogEntry CreateEntry(
        MailFolder folder,
        MailMessage message,
        string messageKey,
        PassState state,
        ClassificationOutcome outcome,
        ClassificationAction action,
        ClassificationResult? result,
        string? overrideReason)
    {
        return new ClassificationLogEntry(
            state.AccountId,
            messageKey,
            folder.Id,
            message.Uid,
            timeProvider.GetUtcNow(),
            state.EffectiveMode,
            outcome,
            action,
            result?.Tier,
            result?.Score ?? 0,
            result?.AuthStatus,
            overrideReason,
            result is null ? [] : result.Signals);
    }

    // One command per target and at most 1,000 UIDs; a rejected command only affects its own messages.
    private async Task EnqueueMovesAsync(
        MailFolder folder,
        Dictionary<long, List<PendingMove>> movesByTarget,
        List<ClassificationLogEntry> entries,
        PassState state,
        CancellationToken ct)
    {
        foreach ((long targetFolderId, List<PendingMove> targetMoves) in movesByTarget)
        {
            foreach (PendingMove[] commandMoves in targetMoves.Chunk(SqliteMailCommandQueue.MaxUidsPerCommand))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await queue
                        .EnqueueMoveAsync(
                            folder.Id,
                            folder.UidValidity,
                            commandMoves.Select(move => move.Uid).ToList(),
                            targetFolderId,
                            ct)
                        .ConfigureAwait(false);
                    state.MovesEnqueued += commandMoves.Length;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    foreach (PendingMove move in commandMoves)
                    {
                        entries[move.EntryIndex] = entries[move.EntryIndex] with
                        {
                            Outcome = ClassificationOutcome.EnqueueFailed,
                        };
                    }
                }
            }
        }
    }

    // Only messages this app moved to the Quarantine, by the stored decision time (never the sender's Date header). A
    // message without such an entry, or without a readable EML (its key is unknown), is never deleted. UIDs that are already
    // in a queued command are skipped, so a waiting or failed delete is not repeated by every pass.
    private async Task EnqueueRetentionDeletesAsync(PassState state, CancellationToken ct)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        IReadOnlyList<MailCommand> queuedCommands = await queue.ListAsync(state.AccountId, ct).ConfigureAwait(false);
        foreach (MailFolder folder in state.AccountFolders.Where(folder => folder.Role == FolderRole.Quarantine && folder.UidValidity != 0))
        {
            // A command of an older UIDVALIDITY addresses other messages than these UIDs now.
            var queuedUids = new HashSet<uint>(queuedCommands
                .Where(command => command.FolderId == folder.Id && command.FolderUidValidity == folder.UidValidity)
                .SelectMany(command => command.Uids));
            IReadOnlyDictionary<uint, MessageFlags> localMessages = await messages
                .GetUidFlagsAsync(folder.Id, ct)
                .ConfigureAwait(false);

            var expiredUids = new List<uint>();
            foreach (uint uid in localMessages.Keys.Where(uid => !queuedUids.Contains(uid)).Order())
            {
                ct.ThrowIfCancellationRequested();
                MailMessage? message = await messages.GetByUidAsync(folder.Id, uid, ct).ConfigureAwait(false);
                if (message is null)
                {
                    continue;
                }

                ClassificationMessage? content = await TryReadContentAsync(message, ct).ConfigureAwait(false);
                if (content is not { Parsed: true })
                {
                    continue;
                }

                ClassificationLogEntry? entry = await log
                    .GetAsync(state.AccountId, MessageKey.For(message, content.TopReceived), ct)
                    .ConfigureAwait(false);
                if (entry is { Outcome: ClassificationOutcome.Enqueued, PlannedAction: ClassificationAction.MoveToQuarantine }
                    && entry.ClassifiedAt.AddDays(policy.QuarantineRetentionDays) <= now)
                {
                    expiredUids.Add(uid);
                }
            }

            foreach (uint[] commandUids in expiredUids.Chunk(SqliteMailCommandQueue.MaxUidsPerCommand))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await queue
                        .EnqueueDeletePermanentlyAsync(folder.Id, folder.UidValidity, commandUids, ct)
                        .ConfigureAwait(false);
                    state.RetentionDeletesEnqueued += commandUids.Length;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    state.RetentionDeletesRejected += commandUids.Length;
                }
            }
        }
    }

    // Position of the message's entry in the entry list of its folder, and its UID.
    private readonly record struct PendingMove(int EntryIndex, uint Uid);

    // Everything one pass carries from folder to folder.
    private sealed class PassState(
        Guid accountId,
        SpamClassifier classifier,
        ClassificationMode effectiveMode,
        IReadOnlyList<MailFolder> accountFolders)
    {
        public Guid AccountId { get; } = accountId;

        public SpamClassifier Classifier { get; } = classifier;

        public ClassificationMode EffectiveMode { get; } = effectiveMode;

        public IReadOnlyList<MailFolder> AccountFolders { get; } = accountFolders;

        public HashSet<string> DecidedKeys { get; } = new(StringComparer.Ordinal);

        public Dictionary<SpamTier, int> TierCounts { get; } = Enum.GetValues<SpamTier>().ToDictionary(tier => tier, _ => 0);

        public int Classified { get; set; }

        public int SkippedKnown { get; set; }

        public int ReadFailed { get; set; }

        public int MovesEnqueued { get; set; }

        public int RetentionDeletesEnqueued { get; set; }

        public int RetentionDeletesRejected { get; set; }
    }
}
