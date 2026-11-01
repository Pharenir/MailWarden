using MailWarden.Core.Commands;
using MailWarden.Core.Model;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;

namespace MailWarden.Presentation;

/// <summary>
/// What the lists show differently from the stored mail because of queued commands: rows of a pending move or delete are
/// hidden, rows of a pending flag change show the wanted flags. The queue is the truth: every refresh takes its pending
/// commands, so a row comes back when its command failed or was discarded, and the stored flags count again when the command
/// has run. The wanted flags themselves are not in the queue view; they live here, until the end of the session at most.
/// </summary>
internal sealed class PendingMailState(IMailboxActions actions, ICriticalLog log, ShellBanner banner)
{
    // Refreshes and actions can overlap on different threads when no UI context is present.
    private readonly Lock stateGate = new();
    private readonly Dictionary<long, WantedFlagChange> wantedFlags = [];
    private readonly HashSet<long> pendingRemovalIds = [];
    private readonly HashSet<long> pendingFlagIds = [];
    private readonly HashSet<long> inFlightRemovalIds = [];
    private readonly HashSet<long> inFlightFlagIds = [];
    private IReadOnlyList<PendingAction> latestActions = [];
    private long refreshSequence;
    private bool isClosed;

    /// <summary>Raised after a refresh on the caller's context.</summary>
    public event EventHandler? Refreshed;

    /// <summary>Raised on the caller's context when the hidden messages may have changed: what counts as unread follows them.</summary>
    public event EventHandler? HiddenChanged;

    /// <summary>The commands of the last refresh.</summary>
    public IReadOnlyList<PendingAction> LatestActions
    {
        get
        {
            lock (stateGate)
            {
                return latestActions;
            }
        }
    }

    /// <summary>Upper bound of the hidden rows, for reading past them.</summary>
    public int HiddenCount
    {
        get
        {
            lock (stateGate)
            {
                return pendingRemovalIds.Count + inFlightRemovalIds.Count;
            }
        }
    }

    /// <summary>The ids of the hidden messages right now; null when there are none, so that a query stays as it was.</summary>
    public IReadOnlyCollection<long>? HiddenMessageIds
    {
        get
        {
            lock (stateGate)
            {
                return pendingRemovalIds.Count + inFlightRemovalIds.Count == 0
                    ? null
                    : pendingRemovalIds.Union(inFlightRemovalIds).ToList();
            }
        }
    }

    /// <summary>
    /// Reads the queue. A failure is logged and reported in the banner, and the last known state stays. A result that an
    /// action or a newer refresh has overtaken is dropped.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the state is updated.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        long sequence;
        lock (stateGate)
        {
            if (isClosed)
            {
                return;
            }

            sequence = ++refreshSequence;
        }

        IReadOnlyList<PendingAction> listedActions;
        try
        {
            listedActions = await actions.ListPendingAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!IsClosed)
            {
                log.Write(CriticalLogLevel.Critical, "PendingMailState.RefreshAsync", $"Reading the queued actions failed: {exception.GetType().Name}");
                banner.Set(ShellBanner.BannerSource.ActionsRead, UiText.BannerActionsReadFailed);
            }

            return;
        }

        bool isHiddenChanged;
        lock (stateGate)
        {
            if (isClosed || sequence != refreshSequence)
            {
                return;
            }

            isHiddenChanged = ApplyListedActions(listedActions);
        }

        banner.Clear(ShellBanner.BannerSource.ActionsRead);
        Refreshed?.Invoke(this, EventArgs.Empty);
        if (isHiddenChanged)
        {
            HiddenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True when a row of this message is to stay out of the lists.</summary>
    /// <param name="messageId">Message id.</param>
    /// <returns>True for a pending move or delete.</returns>
    public bool IsHidden(long messageId)
    {
        lock (stateGate)
        {
            return pendingRemovalIds.Contains(messageId) || inFlightRemovalIds.Contains(messageId);
        }
    }

    /// <summary>The flags a row of this message shows.</summary>
    /// <param name="message">Message as stored.</param>
    /// <returns>The stored flags with the wanted changes.</returns>
    public MessageFlags ShownFlags(MailMessage message)
    {
        lock (stateGate)
        {
            return wantedFlags.TryGetValue(message.Id, out WantedFlagChange change)
                ? (message.Flags | change.Added) & ~change.Removed
                : message.Flags;
        }
    }

    /// <summary>Hides messages from now on, before their commands are stored; ended by <see cref="EndAction"/>.</summary>
    /// <param name="messageIds">Message ids.</param>
    public void BeginRemoval(IEnumerable<long> messageIds)
    {
        bool isHiddenChanged;
        lock (stateGate)
        {
            int countBefore = inFlightRemovalIds.Count;
            inFlightRemovalIds.UnionWith(messageIds);
            isHiddenChanged = inFlightRemovalIds.Count != countBefore;
        }

        if (isHiddenChanged)
        {
            HiddenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Records the wanted state of a flag from now on, before the commands are stored; ended by <see cref="EndAction"/>.</summary>
    /// <param name="messageIds">Message ids.</param>
    /// <param name="flag">The flag.</param>
    /// <param name="isSet">True when the flag is wanted.</param>
    public void BeginFlagChange(IEnumerable<long> messageIds, MessageFlags flag, bool isSet)
    {
        lock (stateGate)
        {
            foreach (long messageId in messageIds)
            {
                inFlightFlagIds.Add(messageId);
                WantedFlagChange change = wantedFlags.GetValueOrDefault(messageId);
                wantedFlags[messageId] = isSet
                    ? new WantedFlagChange(change.Added | flag, change.Removed & ~flag)
                    : new WantedFlagChange(change.Added & ~flag, change.Removed | flag);
            }
        }
    }

    /// <summary>
    /// Ends the running action. After a success its messages count as pending until the next refresh says otherwise, and a
    /// refresh that started before the commands were stored is dropped.
    /// </summary>
    /// <param name="isStored">True when the commands are stored.</param>
    public void EndAction(bool isStored)
    {
        bool isHiddenChanged;
        lock (stateGate)
        {
            if (isStored)
            {
                pendingRemovalIds.UnionWith(inFlightRemovalIds);
                pendingFlagIds.UnionWith(inFlightFlagIds);
                refreshSequence++;
            }

            // Stored, the same messages stay hidden, only under another name.
            isHiddenChanged = !isStored && inFlightRemovalIds.Count > 0;
            inFlightRemovalIds.Clear();
            inFlightFlagIds.Clear();
        }

        if (isHiddenChanged)
        {
            HiddenChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Ends all work: later refreshes do nothing, a running one neither logs nor changes anything.</summary>
    public void Close()
    {
        lock (stateGate)
        {
            isClosed = true;
        }
    }

    private bool IsClosed
    {
        get
        {
            lock (stateGate)
            {
                return isClosed;
            }
        }
    }

    // Under the gate. A failed command hides nothing and wants nothing: its rows come back and show the stored flags.
    private bool ApplyListedActions(IReadOnlyList<PendingAction> listedActions)
    {
        latestActions = listedActions;
        var removalIdsBefore = new HashSet<long>(pendingRemovalIds);
        pendingRemovalIds.Clear();
        pendingFlagIds.Clear();
        foreach (PendingAction action in listedActions.Where(action => action.Status == MailCommandStatus.Pending))
        {
            HashSet<long> ids = action.Kind == MailCommandKind.SetFlags ? pendingFlagIds : pendingRemovalIds;
            ids.UnionWith(action.MessageIds);
        }

        foreach (long messageId in wantedFlags.Keys.ToList())
        {
            if (!pendingFlagIds.Contains(messageId) && !inFlightFlagIds.Contains(messageId))
            {
                wantedFlags.Remove(messageId);
            }
        }

        return !removalIdsBefore.SetEquals(pendingRemovalIds);
    }

    /// <summary>The flags a user wants added and removed on top of the stored ones.</summary>
    /// <param name="Added">Flags to add.</param>
    /// <param name="Removed">Flags to remove.</param>
    private readonly record struct WantedFlagChange(MessageFlags Added, MessageFlags Removed);
}
