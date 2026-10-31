using MailWarden.Core.Accounts;
using MailWarden.Core.Commands;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Storage;

namespace MailWarden.Infrastructure.Commands;

/// <summary>
/// Executes queued server changes (AP-03c): the due commands of one account run one after another, oldest first, in
/// one command session (<see cref="IMailTransportFactory.ConnectForCommandsAsync"/>). The local store changes only
/// after the server confirmed the change. Not thread-safe; one run per account at a time.
/// </summary>
/// <remarks>
/// <para>
/// Per command: the local source (and target) folder must still exist, then the source folder is selected and its
/// UIDVALIDITY must equal <see cref="MailCommand.FolderUidValidity"/>; otherwise the command fails permanently with a
/// fixed text and nothing is sent for it. Server action per kind:
/// SetFlags → add/remove flags; Move and MoveToTrash → UID MOVE to the target; DeletePermanently in a Trash or Junk
/// folder → expunge exactly these UIDs; DeletePermanently elsewhere → UID MOVE to the account's Trash, select Trash
/// and expunge exactly the new UIDs from the COPYUID map. Nothing is expunged in Trash (the messages stay there; nothing
/// is lost) when the map is empty or inconsistent (a source UID outside the command, a repeated or zero new UID,
/// UIDVALIDITY 0) or when its UIDVALIDITY differs from the one the SELECT of Trash returns.
/// </para>
/// <para>
/// Local effect after server success: SetFlags updates the stored flags of the locally stored UIDs; the other kinds
/// delete the EML files (failures skipped) and rows of the source UIDs. Moved messages appear in the target with the
/// next sync pass. A failure of the local effect still completes the command (the next sync repairs the local state).
/// </para>
/// <para>
/// Failures: authentication failures and cancellation propagate and leave the current command unchanged. A network
/// failure records a (never permanent) attempt and propagates; the remaining commands stay due. Any other failure
/// records an attempt and the run continues; after <see cref="CommandOptions.MaxAttempts"/> attempts the command is
/// Failed, otherwise its next attempt is 2^(attempts − 1) minutes later, at most 60 minutes. The recorded error is the
/// exception type name or a fixed text, never an exception message.
/// </para>
/// <para>
/// <see cref="IMailCommandTransport.IsConnected"/> is not read: after a lost connection the next command fails with a
/// network failure, records one non-permanent attempt and ends the run.
/// </para>
/// </remarks>
public sealed class CommandExecutor
{
    /// <summary>Fixed failure text: the server reports another UIDVALIDITY for the source folder.</summary>
    public const string UidValidityChangedError = "UIDVALIDITY changed";

    /// <summary>Fixed failure text: the source folder is no longer in the local store.</summary>
    public const string SourceFolderMissingError = "Source folder no longer exists";

    /// <summary>Fixed failure text: the target folder is no longer in the local store.</summary>
    public const string TargetFolderMissingError = "Target folder no longer exists";

    /// <summary>Fixed failure text: a permanent delete outside Trash/Junk needs a Trash folder, and there is none.</summary>
    public const string NoTrashFolderError = "Account has no Trash folder";

    private const int MaxBackoffMinutes = 60;

    private readonly IMailTransportFactory transportFactory;
    private readonly IAccountStore accounts;
    private readonly IFolderStore folders;
    private readonly IMessageStore messages;
    private readonly IEmlStore emlStore;
    private readonly IMailCommandStore commands;
    private readonly CommandOptions options;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the executor.</summary>
    /// <param name="transportFactory">Opens the command session of an account.</param>
    /// <param name="accounts">Account store.</param>
    /// <param name="folders">Folder store (source, target and Trash folder of a command).</param>
    /// <param name="messages">Message store (local effect).</param>
    /// <param name="emlStore">Store of the raw messages (local effect).</param>
    /// <param name="commands">Executor side of the command queue.</param>
    /// <param name="options">Executor settings.</param>
    /// <param name="timeProvider">Clock for due checks and the retry schedule.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public CommandExecutor(
        IMailTransportFactory transportFactory,
        IAccountStore accounts,
        IFolderStore folders,
        IMessageStore messages,
        IEmlStore emlStore,
        IMailCommandStore commands,
        CommandOptions options,
        TimeProvider timeProvider)
    {
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.folders = folders ?? throw new ArgumentNullException(nameof(folders));
        this.messages = messages ?? throw new ArgumentNullException(nameof(messages));
        this.emlStore = emlStore ?? throw new ArgumentNullException(nameof(emlStore));
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Executes the due commands of the account in queue order with one command session.</summary>
    /// <param name="accountId">Account whose commands run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Counts of completed, retrying and permanently failed commands of this run.</returns>
    /// <exception cref="ArgumentException">Due commands exist but the account is unknown.</exception>
    /// <exception cref="AuthenticationFailedException">No token could be obtained (e.g. a sign-in is needed).</exception>
    /// <exception cref="MailTransportException">
    /// Connecting failed, the login was rejected (Authentication) or the connection failed during a command (Network;
    /// the attempt of that command is recorded).
    /// </exception>
    /// <exception cref="StorageException">The queue itself could not be read or updated.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public async Task<CommandRunReport> ExecutePendingAsync(Guid accountId, CancellationToken ct = default)
    {
        // E1: no due command → no connection.
        IReadOnlyList<MailCommand> dueCommands = await commands
            .ListDueAsync(accountId, timeProvider.GetUtcNow(), ct)
            .ConfigureAwait(false);
        if (dueCommands.Count == 0)
        {
            return new CommandRunReport(0, 0, 0);
        }

        Account account = await accounts.GetAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"Unknown account id {accountId}.", nameof(accountId));

        var tally = new RunTally();
        IMailCommandTransport transport = await transportFactory
            .ConnectForCommandsAsync(account, ct)
            .ConfigureAwait(false);
        try
        {
            foreach (MailCommand command in dueCommands)
            {
                ct.ThrowIfCancellationRequested();
                await ExecuteCommandAsync(account, transport, command, tally, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        return new CommandRunReport(tally.Completed, tally.Retrying, tally.Failed);
    }

    // One command: server action, then local effect and completion; failures are recorded per E5.
    private async Task ExecuteCommandAsync(
        Account account,
        IMailCommandTransport transport,
        MailCommand command,
        RunTally tally,
        CancellationToken ct)
    {
        ServerOutcome outcome;
        try
        {
            outcome = await ExecuteOnServerAsync(account, transport, command, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (!PropagatesUnrecorded(exception))
        {
            if (exception is MailTransportException { Failure: MailTransportFailure.Network })
            {
                // E5: the session is most likely gone; the attempt counts but never makes the command permanent.
                await RecordAttemptAsync(command, exception.GetType().Name, permanentAllowed: false, ct)
                    .ConfigureAwait(false);
                throw;
            }

            bool permanent = await RecordAttemptAsync(command, exception.GetType().Name, permanentAllowed: true, ct)
                .ConfigureAwait(false);
            tally.Count(permanent);
            return;
        }

        if (outcome.PermanentFailure is string permanentFailure)
        {
            // E2/E3: fixed text, never retried; nothing was sent for this command.
            await commands
                .RecordFailureAsync(command.Id, permanentFailure, nextAttemptAt: null, permanent: true, ct)
                .ConfigureAwait(false);
            tally.Count(permanent: true);
            return;
        }

        await ApplyLocalEffectAsync(command, outcome.Source!, ct).ConfigureAwait(false);
        await commands.CompleteAsync(command.Id, ct).ConfigureAwait(false);
        tally.Completed++;
    }

    // E2 + E3: local checks first (nothing is sent when they fail), then SELECT + UIDVALIDITY guard, then the action.
    private async Task<ServerOutcome> ExecuteOnServerAsync(
        Account account,
        IMailCommandTransport transport,
        MailCommand command,
        CancellationToken ct)
    {
        MailFolder? source = await folders.GetAsync(command.FolderId, ct).ConfigureAwait(false);
        if (source is null || source.AccountId != command.AccountId)
        {
            return ServerOutcome.Rejected(SourceFolderMissingError);
        }

        MailFolder? target = null;
        if (command.Kind is MailCommandKind.Move or MailCommandKind.MoveToTrash)
        {
            target = command.TargetFolderId is long targetFolderId
                ? await folders.GetAsync(targetFolderId, ct).ConfigureAwait(false)
                : null;
            if (target is null || target.AccountId != command.AccountId)
            {
                return ServerOutcome.Rejected(TargetFolderMissingError);
            }
        }

        bool expungeInPlace = command.Kind == MailCommandKind.DeletePermanently
            && source.Role is FolderRole.Trash or FolderRole.Junk;
        MailFolder? trash = null;
        if (command.Kind == MailCommandKind.DeletePermanently && !expungeInPlace)
        {
            trash = await FindTrashFolderAsync(account.Id, ct).ConfigureAwait(false);
            if (trash is null)
            {
                return ServerOutcome.Rejected(NoTrashFolderError);
            }
        }

        uint serverUidValidity = await transport.SelectFolderAsync(source.FullName, ct).ConfigureAwait(false);
        if (serverUidValidity != command.FolderUidValidity)
        {
            return ServerOutcome.Rejected(UidValidityChangedError);
        }

        switch (command.Kind)
        {
            case MailCommandKind.SetFlags:
                if (command.FlagsToAdd != MessageFlags.None)
                {
                    await transport.AddFlagsAsync(command.Uids, command.FlagsToAdd, ct).ConfigureAwait(false);
                }

                if (command.FlagsToRemove != MessageFlags.None)
                {
                    await transport.RemoveFlagsAsync(command.Uids, command.FlagsToRemove, ct).ConfigureAwait(false);
                }

                break;

            case MailCommandKind.Move:
            case MailCommandKind.MoveToTrash:
                await transport.MoveAsync(command.Uids, target!.FullName, ct).ConfigureAwait(false);
                break;

            case MailCommandKind.DeletePermanently when expungeInPlace:
                await transport.ExpungeAsync(command.Uids, ct).ConfigureAwait(false);
                break;

            case MailCommandKind.DeletePermanently:
                await MoveToTrashAndExpungeAsync(transport, command, trash!, ct).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unknown mail command kind {command.Kind}.");
        }

        return ServerOutcome.Succeeded(source);
    }

    // E3 + F2: outside Trash/Junk a permanent delete goes through Trash. Only new UIDs of a consistent COPYUID map are
    // expunged there, and only when Trash still has the map's UIDVALIDITY; otherwise the messages stay in Trash instead
    // of risking another message.
    private static async Task MoveToTrashAndExpungeAsync(
        IMailCommandTransport transport,
        MailCommand command,
        MailFolder trash,
        CancellationToken ct)
    {
        CopyUidMap copyUidMap = await transport
            .MoveAsync(command.Uids, trash.FullName, ct)
            .ConfigureAwait(false);
        List<uint>? trashUids = TrashUidsToExpunge(copyUidMap, command.Uids);
        if (trashUids is null)
        {
            return;
        }

        uint trashUidValidity = await transport.SelectFolderAsync(trash.FullName, ct).ConfigureAwait(false);
        if (trashUidValidity != copyUidMap.DestinationUidValidity)
        {
            return;
        }

        await transport.ExpungeAsync(trashUids, ct).ConfigureAwait(false);
    }

    // F2: the new Trash UIDs (ascending), or null when nothing may be expunged: empty map, UIDVALIDITY 0, a source UID
    // outside the command, or a repeated or zero new UID.
    private static List<uint>? TrashUidsToExpunge(CopyUidMap copyUidMap, IReadOnlyList<uint> commandUids)
    {
        if (copyUidMap.Uids.Count == 0 || copyUidMap.DestinationUidValidity == 0)
        {
            return null;
        }

        var commandUidSet = new HashSet<uint>(commandUids);
        var trashUids = new HashSet<uint>();
        foreach ((uint sourceUid, uint trashUid) in copyUidMap.Uids)
        {
            if (!commandUidSet.Contains(sourceUid) || trashUid == 0 || !trashUids.Add(trashUid))
            {
                return null;
            }
        }

        return [.. trashUids.Order()];
    }

    // E4: runs only after the server confirmed the change. Storage failures are swallowed on purpose: the server has
    // changed, so the command completes and the next sync pass repairs the local state. Cancellation propagates (the
    // command stays pending; executing it again is harmless).
    private async Task ApplyLocalEffectAsync(MailCommand command, MailFolder source, CancellationToken ct)
    {
        try
        {
            if (command.Kind == MailCommandKind.SetFlags)
            {
                IReadOnlyDictionary<uint, MessageFlags> storedFlags = await messages
                    .GetUidFlagsAsync(source.Id, ct)
                    .ConfigureAwait(false);
                var updatedFlags = new Dictionary<uint, MessageFlags>();
                foreach (uint uid in command.Uids)
                {
                    if (storedFlags.TryGetValue(uid, out MessageFlags flags))
                    {
                        updatedFlags[uid] = (flags | command.FlagsToAdd) & ~command.FlagsToRemove;
                    }
                }

                if (updatedFlags.Count > 0)
                {
                    await messages.UpdateFlagsAsync(source.Id, updatedFlags, ct).ConfigureAwait(false);
                }

                return;
            }

            // Move, MoveToTrash, DeletePermanently: the messages left the source folder.
            IReadOnlyDictionary<uint, string?> emlPaths = await messages
                .GetUidEmlPathsAsync(source.Id, ct)
                .ConfigureAwait(false);
            await EmlFiles
                .DeleteSkippingFailuresAsync(emlStore, command.Uids.Select(emlPaths.GetValueOrDefault), ct)
                .ConfigureAwait(false);
            await messages.DeleteByUidsAsync(source.Id, command.Uids, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Swallowed on purpose, see method comment.
        }
    }

    // Records a failed attempt with the retry schedule; returns true when the command is now Failed.
    private async Task<bool> RecordAttemptAsync(
        MailCommand command,
        string error,
        bool permanentAllowed,
        CancellationToken ct)
    {
        int attemptsAfterFailure = command.Attempts + 1;
        bool permanent = permanentAllowed && attemptsAfterFailure >= options.MaxAttempts;
        DateTimeOffset? nextAttemptAt = permanent
            ? null
            : timeProvider.GetUtcNow() + RetryDelay(attemptsAfterFailure);
        await commands.RecordFailureAsync(command.Id, error, nextAttemptAt, permanent, ct).ConfigureAwait(false);
        return permanent;
    }

    // 2^(attempts − 1) minutes, at most MaxBackoffMinutes: 1, 2, 4, 8, 16, 32, 60, 60, …
    private static TimeSpan RetryDelay(int attempts)
    {
        int exponent = Math.Max(attempts - 1, 0);
        double minutes = exponent >= 6 ? MaxBackoffMinutes : Math.Min(1 << exponent, MaxBackoffMinutes);
        return TimeSpan.FromMinutes(minutes);
    }

    // Same rule as the queue (FolderLookup.FindTrash).
    private async Task<MailFolder?> FindTrashFolderAsync(Guid accountId, CancellationToken ct)
    {
        IReadOnlyList<MailFolder> accountFolders = await folders.ListAsync(accountId, ct).ConfigureAwait(false);
        return FolderLookup.FindTrash(accountFolders);
    }

    // E5: these end the run without touching the current command.
    private static bool PropagatesUnrecorded(Exception exception)
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

    // Result of the server phase: either a permanent failure with a fixed text, or success with the source folder.
    private sealed record ServerOutcome(string? PermanentFailure, MailFolder? Source)
    {
        public static ServerOutcome Rejected(string permanentFailure) => new(permanentFailure, null);

        public static ServerOutcome Succeeded(MailFolder source) => new(null, source);
    }

    private sealed class RunTally
    {
        public int Completed { get; set; }

        public int Retrying { get; set; }

        public int Failed { get; set; }

        public void Count(bool permanent)
        {
            if (permanent)
            {
                Failed++;
            }
            else
            {
                Retrying++;
            }
        }
    }
}
