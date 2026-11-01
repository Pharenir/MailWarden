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
/// Safety rules: the first pass over a folder only sets its marker, so existing mail is never classified. Act is suspended
/// while the address book is empty. A message key in the log is never decided again. Retention deletes only mail this app
/// moved to the Quarantine, by the stored decision time.
/// </para>
/// <para>
/// A folder's entries and marker are committed in one transaction, after its commands were enqueued. A crash in between
/// can only repeat a move of UIDs that are already gone, which the server ignores. Cancellation propagates as
/// <see cref="OperationCanceledException"/>; a folder whose commit did not run is handled again by the next pass.
/// </para>
/// <para>
/// No report or exception of this class contains subject, body or address text. Reason texts go to the local decision
/// log only. Not thread-safe; one pass at a time.
/// </para>
/// </remarks>
public sealed class ClassificationPass
{
    private readonly IAccountStore accounts;
    private readonly IFolderStore folders;
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
        int retentionDeletes = effectiveMode == ClassificationMode.Act && policy.QuarantineRetentionDays > 0
            ? await EnqueueRetentionDeletesAsync(account.Id, accountFolders, ct).ConfigureAwait(false)
            : 0;

        return new ClassificationPassReport(
            state.Classified,
            state.SkippedKnown,
            state.ReadFailed,
            state.MovesEnqueued,
            retentionDeletes,
            actSuspended,
            state.TierCounts);
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

            // One decision per message: also a second copy in this pass counts as known.
            string messageKey = MessageKey.For(message);
            if (!state.DecidedKeys.Add(messageKey)
                || await log.GetAsync(state.AccountId, messageKey, ct).ConfigureAwait(false) is not null)
            {
                state.SkippedKnown++;
                continue;
            }

            ClassificationResult? result = await TryClassifyAsync(message, state.Classifier, ct).ConfigureAwait(false);
            ClassificationLogEntry entry = Decide(folder, message, messageKey, result, state, out MailFolder? target);
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

        // Marker and entries only after enqueueing, so that a crash can never lose a planned move.
        await EnqueueMovesAsync(folder, movesByTarget, entries, state, ct).ConfigureAwait(false);
        await log
            .CommitAsync(entries, new ClassificationMarker(folder.Id, folder.UidValidity, newUids[^1]), ct)
            .ConfigureAwait(false);
    }

    // Reads the EML into memory and classifies it. Every failure means "no decision possible": the message stays where it
    // is and the entry holds no exception text, because exception messages can quote message data.
    private async Task<ClassificationResult?> TryClassifyAsync(
        MailMessage message,
        SpamClassifier classifier,
        CancellationToken ct)
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
            ClassificationInput? input = await ClassificationInputReader.ReadAsync(buffer, ct).ConfigureAwait(false);
            return input is null ? null : classifier.Classify(input);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return null;
        }
    }

    // Routing of one message. The outcome Enqueued is provisional: EnqueueMovesAsync replaces it when the queue rejects.
    private ClassificationLogEntry Decide(
        MailFolder folder,
        MailMessage message,
        string messageKey,
        ClassificationResult? result,
        PassState state,
        out MailFolder? target)
    {
        target = null;
        if (result is null)
        {
            state.ReadFailed++;
            return new ClassificationLogEntry(
                state.AccountId,
                messageKey,
                folder.Id,
                message.Uid,
                timeProvider.GetUtcNow(),
                state.EffectiveMode,
                ClassificationOutcome.ReadFailed,
                ClassificationAction.None,
                Tier: null,
                Score: 0,
                AuthStatus: null,
                Override: null,
                Signals: []);
        }

        state.Classified++;
        state.TierCounts[result.Tier]++;

        ClassificationAction action = ClassificationRouting.Route(folder.Role, result.Tier);
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

        return new ClassificationLogEntry(
            state.AccountId,
            messageKey,
            folder.Id,
            message.Uid,
            timeProvider.GetUtcNow(),
            state.EffectiveMode,
            outcome,
            action,
            result.Tier,
            result.Score,
            result.AuthStatus,
            result.Override,
            result.Signals);
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
    // message without such an entry is never deleted. A rejected command is tried again by the next pass.
    private async Task<int> EnqueueRetentionDeletesAsync(
        Guid accountId,
        IReadOnlyList<MailFolder> accountFolders,
        CancellationToken ct)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        int deletesEnqueued = 0;
        foreach (MailFolder folder in accountFolders.Where(folder => folder.Role == FolderRole.Quarantine && folder.UidValidity != 0))
        {
            IReadOnlyDictionary<uint, MessageFlags> localMessages = await messages
                .GetUidFlagsAsync(folder.Id, ct)
                .ConfigureAwait(false);

            var expiredUids = new List<uint>();
            foreach (uint uid in localMessages.Keys.Order())
            {
                ct.ThrowIfCancellationRequested();
                MailMessage? message = await messages.GetByUidAsync(folder.Id, uid, ct).ConfigureAwait(false);
                if (message is null)
                {
                    continue;
                }

                ClassificationLogEntry? entry = await log
                    .GetAsync(accountId, MessageKey.For(message), ct)
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
                    deletesEnqueued += commandUids.Length;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    // Not counted; the messages are still local, so the next pass finds them again.
                }
            }
        }

        return deletesEnqueued;
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
    }
}
