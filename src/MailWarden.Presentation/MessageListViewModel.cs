using System.Collections.ObjectModel;
using System.Globalization;
using MailWarden.Core.Model;
using MailWarden.Core.Text;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;

namespace MailWarden.Presentation;

/// <summary>
/// One row of the message list; all texts are cleaned for display. The flags can change in place, so that a row shows the
/// wanted state at once and the selection of the list stays.
/// </summary>
public sealed class MessageRowViewModel : ObservableObject
{
    internal MessageRowViewModel(MailMessage message, string accountLabel, DateTimeOffset now, TimeZoneInfo localZone)
    {
        Message = message;
        AccountLabel = accountLabel;
        string fromName = SafeDisplayText.Shorten(message.FromName, MailDisplayText.MaxSenderCharacters);
        Sender = fromName.Length == 0
            ? MailDisplayText.ShortenAddress(message.FromAddress, MailDisplayText.MaxSenderCharacters)
            : fromName;
        Subject = MailDisplayText.Subject(message.Subject);
        Preview = SafeDisplayText.Shorten(message.TextPreview, MailDisplayText.MaxPreviewCharacters);
        DateText = FormatDate(message.Date, now, localZone);
    }

    /// <summary>The message of the row.</summary>
    public MailMessage Message { get; private set; }

    /// <summary>Shortened FromName, else shortened FromAddress.</summary>
    public string Sender { get; }

    /// <summary>Shortened subject; "(Kein Betreff)" when empty.</summary>
    public string Subject { get; }

    /// <summary>Shortened text preview.</summary>
    public string Preview { get; }

    /// <summary>Local time: today "HH:mm", same year "dd.MM.", otherwise "dd.MM.yyyy".</summary>
    public string DateText { get; }

    /// <summary>Cleaned account display name, else its address.</summary>
    public string AccountLabel { get; }

    /// <summary>True when the message has no \Seen flag.</summary>
    public bool IsUnread => !Message.Flags.HasFlag(MessageFlags.Seen);

    /// <summary>True when the message has the \Flagged flag.</summary>
    public bool IsFlagged => Message.Flags.HasFlag(MessageFlags.Flagged);

    /// <summary>True when the message has attachments.</summary>
    public bool HasAttachments => Message.HasAttachments;

    /// <summary>Shows other flags; nothing else of a row depends on them.</summary>
    /// <param name="flags">The flags to show.</param>
    internal void ShowFlags(MessageFlags flags)
    {
        if (Message.Flags != flags)
        {
            Message = Message with { Flags = flags };
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(IsUnread));
            OnPropertyChanged(nameof(IsFlagged));
        }
    }

    private static string FormatDate(DateTimeOffset date, DateTimeOffset now, TimeZoneInfo localZone)
    {
        DateTimeOffset localDate = TimeZoneInfo.ConvertTime(date, localZone);
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(now, localZone);
        string format = localDate.Date == localNow.Date
            ? "HH:mm"
            : localDate.Year == localNow.Year ? "dd.MM." : "dd.MM.yyyy";
        return localDate.ToString(format, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The message list of the selected view or of a search, paged and newest first. Continuations of the awaits stay on the
/// caller's context (the UI thread), which owns <see cref="Items"/>. Of overlapping loads only the newest changes the list.
/// Rows of a pending move or delete stay out of it, rows of a pending flag change show the wanted flags (see
/// <see cref="PendingMailState"/>).
/// </summary>
public sealed class MessageListViewModel : ObservableObject
{
    /// <summary>Number of messages per page.</summary>
    public const int PageSize = 100;

    private readonly IMailboxReadModel readModel;
    private readonly ICriticalLog log;
    private readonly ShellBanner banner;
    private readonly TimeProvider timeProvider;
    private readonly PendingMailState pendingMail;
    private MessageRowViewModel? selectedRow;
    private IReadOnlyList<MessageRowViewModel> selectedRows = [];
    private MailView? currentView;
    private string? searchText;
    private bool hasMore;
    private bool isLoading;
    private int loadGeneration;
    private CancellationTokenSource? loadCancellation;
    private bool isClosed;

    // Stored rows before the end of the list that are not in Items, because they are hidden: the next page starts behind them.
    private int hiddenRowsRead;

    internal MessageListViewModel(
        IMailboxReadModel readModel,
        ICriticalLog log,
        ShellBanner banner,
        TimeProvider timeProvider,
        PendingMailState pendingMail)
    {
        this.readModel = readModel;
        this.log = log;
        this.banner = banner;
        this.timeProvider = timeProvider;
        this.pendingMail = pendingMail;
    }

    /// <summary>The loaded rows.</summary>
    public ObservableCollection<MessageRowViewModel> Items { get; } = [];

    /// <summary>The row the reading pane shows: the selected one when exactly one is selected, otherwise null.</summary>
    public MessageRowViewModel? SelectedRow
    {
        get => selectedRow;
        set => ApplySelection(value is null ? [] : [value]);
    }

    /// <summary>The selected rows; actions work on them.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public IReadOnlyList<MessageRowViewModel> SelectedRows
    {
        get => selectedRows;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ApplySelection(value);
        }
    }

    /// <summary>True when the last page was full.</summary>
    public bool HasMore
    {
        get => hasMore;
        private set => SetProperty(ref hasMore, value);
    }

    /// <summary>True while the newest load runs.</summary>
    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    /// <summary>The view the list shows or is loading, also the one behind a search.</summary>
    internal MailView? CurrentView => currentView;

    /// <summary>True while the list shows search results.</summary>
    internal bool IsShowingSearch => searchText is not null;

    /// <summary>Shows the first page of a view; the selection is cleared at once and a search ends.</summary>
    /// <param name="view">View to show.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when this load ended or a newer one replaced it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="view"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task LoadAsync(MailView view, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (isClosed)
        {
            return;
        }

        currentView = view;
        searchText = null;
        ClearList();
        await RunLoadAsync(
            "MessageListViewModel.LoadAsync",
            async token =>
            {
                await pendingMail.RefreshAsync(token);
                RowRead read = await ReadViewAsync(view, PageSize, 0, token);
                return () => ShowFirstPage(read);
            },
            ct);
    }

    /// <summary>Appends the next page. Does nothing without a view, without more rows or during another load.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the page is appended.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (isClosed || currentView is not MailView view || IsShowingSearch || !HasMore || IsLoading)
        {
            return;
        }

        int offset = Items.Count + hiddenRowsRead;
        await RunLoadAsync(
            "MessageListViewModel.LoadMoreAsync",
            async token =>
            {
                RowRead read = await ReadViewAsync(view, PageSize, offset, token);
                return () =>
                {
                    // New mail moves the rows down during paging, so a page can repeat rows that are already shown.
                    List<MessageRowViewModel> rows = BuildVisibleRows(read, out int hiddenCount);
                    var knownIds = Items.Select(row => row.Message.Id).ToHashSet();
                    foreach (MessageRowViewModel row in rows.Where(row => knownIds.Add(row.Message.Id)))
                    {
                        Items.Add(row);
                    }

                    hiddenRowsRead += hiddenCount;
                    HasMore = read.IsPageFull;
                };
            },
            ct);
    }

    /// <summary>
    /// Loads as many rows as are currently shown (at least one page) and updates the list in place, so that the scroll
    /// position stays. The selection stays by message id for the rows that still exist. Also runs the search again when the
    /// list shows one.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the list is updated.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        MailView? view = currentView;
        string? search = searchText;
        if (isClosed || (view is null && search is null))
        {
            return;
        }

        int limit = Math.Max(PageSize, Items.Count);
        await RunLoadAsync(
            "MessageListViewModel.ReloadAsync",
            async token =>
            {
                await pendingMail.RefreshAsync(token);
                RowRead read = search is not null
                    ? await ReadSearchAsync(search, token)
                    : await ReadViewAsync(view!, limit, 0, token);
                return () =>
                {
                    List<MessageRowViewModel> rows = BuildVisibleRows(read, out int hiddenCount);
                    var selectedIds = selectedRows.Select(row => row.Message.Id).ToHashSet();
                    MergeRows(rows);
                    hiddenRowsRead = hiddenCount;
                    HasMore = read.IsPageFull;
                    ApplySelection(rows.Where(row => selectedIds.Contains(row.Message.Id)).ToList());
                };
            },
            ct);
    }

    /// <summary>Shows the results of a search in place of the view; the selection is cleared at once.</summary>
    /// <param name="text">Search text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when this load ended or a newer one replaced it.</returns>
    internal async Task ShowSearchAsync(string text, CancellationToken ct)
    {
        if (isClosed)
        {
            return;
        }

        searchText = text;
        ClearList();
        await RunLoadAsync(
            "MessageListViewModel.ShowSearchAsync",
            async token =>
            {
                await pendingMail.RefreshAsync(token);
                RowRead read = await ReadSearchAsync(text, token);
                return () => ShowFirstPage(read);
            },
            ct);
    }

    /// <summary>Ends a search and shows the view behind it again.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when this load ended or a newer one replaced it.</returns>
    internal async Task ReturnToViewAsync(CancellationToken ct)
    {
        if (currentView is MailView view)
        {
            await LoadAsync(view, ct);
            return;
        }

        searchText = null;
        loadGeneration++;
        CancelPreviousLoad();
        ClearList();
    }

    /// <summary>
    /// Takes rows out of the list at once. When a selected row goes, the next row takes over the selection (as in Outlook),
    /// or the last one when nothing follows.
    /// </summary>
    /// <param name="messageIds">Messages whose rows go.</param>
    internal void RemoveRows(IReadOnlySet<long> messageIds)
    {
        if (isClosed)
        {
            return;
        }

        // The selection first, so that the reading pane never passes through "nothing selected".
        List<MessageRowViewModel> remainingSelection = selectedRows.Where(row => !messageIds.Contains(row.Message.Id)).ToList();
        if (remainingSelection.Count != selectedRows.Count)
        {
            if (remainingSelection.Count == 0 && FindRowAfterRemoval(messageIds) is MessageRowViewModel nextRow)
            {
                remainingSelection.Add(nextRow);
            }

            ApplySelection(remainingSelection);
        }

        for (int index = Items.Count - 1; index >= 0; index--)
        {
            if (messageIds.Contains(Items[index].Message.Id))
            {
                Items.RemoveAt(index);
                hiddenRowsRead++;
            }
        }
    }

    /// <summary>Ends the running load without a further read or log line; later calls do nothing.</summary>
    internal void Close()
    {
        isClosed = true;
        loadGeneration++;
        CancelPreviousLoad();
    }

    private static bool ShowsSameContent(MessageRowViewModel left, MessageRowViewModel right)
    {
        return left.Message == right.Message && left.AccountLabel == right.AccountLabel && left.DateText == right.DateText;
    }

    private static bool DiffersOnlyInFlags(MessageRowViewModel left, MessageRowViewModel right)
    {
        return left.Message with { Flags = right.Message.Flags } == right.Message
            && left.AccountLabel == right.AccountLabel
            && left.DateText == right.DateText;
    }

    // One event per real change: the row objects decide, their order does not.
    private void ApplySelection(IReadOnlyList<MessageRowViewModel> rows)
    {
        if (!new HashSet<MessageRowViewModel>(selectedRows).SetEquals(rows))
        {
            selectedRows = rows.ToList();
            OnPropertyChanged(nameof(SelectedRows));
        }

        SetProperty(ref selectedRow, rows.Count == 1 ? rows[0] : null, nameof(SelectedRow));
    }

    private void ClearList()
    {
        ApplySelection([]);
        Items.Clear();
        HasMore = false;
        hiddenRowsRead = 0;
    }

    private void ShowFirstPage(RowRead read)
    {
        List<MessageRowViewModel> rows = BuildVisibleRows(read, out int hiddenCount);
        foreach (MessageRowViewModel row in rows)
        {
            Items.Add(row);
        }

        hiddenRowsRead = hiddenCount;
        HasMore = read.IsPageFull;
    }

    // The next row behind the first selected row that goes, otherwise the last one before it.
    private MessageRowViewModel? FindRowAfterRemoval(IReadOnlySet<long> messageIds)
    {
        var selectedIds = selectedRows.Select(row => row.Message.Id).ToHashSet();
        int firstIndex = -1;
        for (int index = 0; index < Items.Count && firstIndex < 0; index++)
        {
            if (selectedIds.Contains(Items[index].Message.Id))
            {
                firstIndex = index;
            }
        }

        if (firstIndex < 0)
        {
            return null;
        }

        return Items.Skip(firstIndex).FirstOrDefault(row => !messageIds.Contains(row.Message.Id))
            ?? Items.Take(firstIndex).LastOrDefault(row => !messageIds.Contains(row.Message.Id));
    }

    // The newest call wins: an older result, failure or cancellation changes nothing. The read runs under the generation
    // it started with; only the action it returns touches the list, and only while it is still the newest.
    private async Task RunLoadAsync(string source, Func<CancellationToken, Task<Action>> read, CancellationToken ct)
    {
        int generation = ++loadGeneration;
        CancelPreviousLoad();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        loadCancellation = linkedCancellation;
        IsLoading = true;
        try
        {
            Action applyResult = await read(linkedCancellation.Token);
            if (generation == loadGeneration)
            {
                applyResult();
                banner.Clear(ShellBanner.BannerSource.MessageList);
            }
        }
        catch (OperationCanceledException) when (generation != loadGeneration && !ct.IsCancellationRequested)
        {
            // Replaced by a newer load.
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            if (generation == loadGeneration)
            {
                log.Write(CriticalLogLevel.Critical, source, $"Reading the message list failed: {exception.GetType().Name}");
                banner.Set(ShellBanner.BannerSource.MessageList, UiText.BannerListFailed);
            }
        }
        finally
        {
            if (generation == loadGeneration)
            {
                loadCancellation = null;
                IsLoading = false;
            }
        }
    }

    private void CancelPreviousLoad()
    {
        try
        {
            loadCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That load has just ended.
        }
    }

    // Reads on behind hidden rows until the page has enough visible ones, so that a big pending delete does not leave an
    // empty list that cannot scroll to the next page. More than limit + hidden rows can never be needed.
    private async Task<RowRead> ReadViewAsync(MailView view, int limit, int offset, CancellationToken ct)
    {
        Dictionary<Guid, string> accountLabels = await ReadAccountLabelsAsync(ct);
        var messages = new List<MailMessage>();
        int visibleCount = 0;
        bool isPageFull;
        do
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<MailMessage> page = await readModel.ListMessagesAsync(view.ToQuery(limit, offset + messages.Count), ct);
            messages.AddRange(page);
            visibleCount += page.Count(message => !pendingMail.IsHidden(message.Id));
            isPageFull = page.Count == limit;
        }
        while (isPageFull && visibleCount < limit && messages.Count < limit + pendingMail.HiddenCount);

        return new RowRead(messages, accountLabels, isPageFull);
    }

    // A text the search cannot use is no failure: it simply finds nothing.
    private async Task<RowRead> ReadSearchAsync(string text, CancellationToken ct)
    {
        Dictionary<Guid, string> accountLabels = await ReadAccountLabelsAsync(ct);
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<MailMessage> messages;
        try
        {
            messages = await readModel.SearchAsync(text, SearchViewModel.ResultLimit, ct);
        }
        catch (ArgumentException)
        {
            messages = [];
        }

        return new RowRead(messages, accountLabels, IsPageFull: false);
    }

    private async Task<Dictionary<Guid, string>> ReadAccountLabelsAsync(CancellationToken ct)
    {
        IReadOnlyList<Account> accounts = await readModel.ListAccountsAsync(ct);
        return accounts.ToDictionary(account => account.Id, NavigationViewModel.AccountLabel);
    }

    // At the moment the result is applied: an action can have hidden a row or changed its flags during the read.
    private List<MessageRowViewModel> BuildVisibleRows(RowRead read, out int hiddenCount)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var rows = new List<MessageRowViewModel>(read.Messages.Count);
        hiddenCount = 0;
        foreach (MailMessage message in read.Messages.DistinctBy(message => message.Id))
        {
            if (pendingMail.IsHidden(message.Id))
            {
                hiddenCount++;
                continue;
            }

            rows.Add(new MessageRowViewModel(
                message with { Flags = pendingMail.ShownFlags(message) },
                read.AccountLabels.GetValueOrDefault(message.AccountId, string.Empty),
                now,
                timeProvider.LocalTimeZone));
        }

        return rows;
    }

    // Brings Items to the order of newRows with as few collection changes as possible; unchanged rows keep their object, also
    // when only their flags are new.
    private void MergeRows(List<MessageRowViewModel> newRows)
    {
        var newIds = newRows.Select(row => row.Message.Id).ToHashSet();
        for (int index = Items.Count - 1; index >= 0; index--)
        {
            if (!newIds.Contains(Items[index].Message.Id))
            {
                Items.RemoveAt(index);
            }
        }

        for (int index = 0; index < newRows.Count; index++)
        {
            MessageRowViewModel newRow = newRows[index];
            int existingIndex = FindRowIndex(newRow.Message.Id, index);
            if (existingIndex < 0)
            {
                Items.Insert(index, newRow);
                continue;
            }

            if (existingIndex != index)
            {
                Items.Move(existingIndex, index);
            }

            if (ShowsSameContent(Items[index], newRow))
            {
                newRows[index] = Items[index];
            }
            else if (DiffersOnlyInFlags(Items[index], newRow))
            {
                Items[index].ShowFlags(newRow.Message.Flags);
                newRows[index] = Items[index];
            }
            else
            {
                Items[index] = newRow;
            }
        }
    }

    private int FindRowIndex(long messageId, int startIndex)
    {
        for (int index = startIndex; index < Items.Count; index++)
        {
            if (Items[index].Message.Id == messageId)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>What a load has read; the rows are built when it is applied.</summary>
    /// <param name="Messages">Stored messages, hidden ones included.</param>
    /// <param name="AccountLabels">Label per account.</param>
    /// <param name="IsPageFull">True when the last page read was full.</param>
    private sealed record RowRead(IReadOnlyList<MailMessage> Messages, Dictionary<Guid, string> AccountLabels, bool IsPageFull);
}
