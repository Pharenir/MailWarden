using System.Collections.ObjectModel;
using System.Globalization;
using MailWarden.Core.Model;
using MailWarden.Core.Text;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;

namespace MailWarden.Presentation;

/// <summary>
/// One row of the message list; all texts are cleaned for display.
/// </summary>
public sealed class MessageRowViewModel
{
    internal MessageRowViewModel(MailMessage message, string accountLabel, DateTimeOffset now, TimeZoneInfo localZone)
    {
        Message = message;
        AccountLabel = accountLabel;
        string fromName = SafeDisplayText.Shorten(message.FromName, MailDisplayText.MaxSenderCharacters);
        Sender = fromName.Length == 0
            ? SafeDisplayText.Shorten(message.FromAddress, MailDisplayText.MaxSenderCharacters)
            : fromName;
        Subject = MailDisplayText.Subject(message.Subject);
        Preview = SafeDisplayText.Shorten(message.TextPreview, MailDisplayText.MaxPreviewCharacters);
        DateText = FormatDate(message.Date, now, localZone);
    }

    /// <summary>The message of the row.</summary>
    public MailMessage Message { get; }

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
/// The message list of the selected view, paged and newest first. Continuations of the awaits stay on the caller's
/// context (the UI thread), which owns <see cref="Items"/>. Of overlapping loads only the newest changes the list.
/// </summary>
public sealed class MessageListViewModel : ObservableObject
{
    /// <summary>Number of messages per page.</summary>
    public const int PageSize = 100;

    private readonly IMailboxReadModel readModel;
    private readonly ICriticalLog log;
    private readonly ShellBanner banner;
    private readonly TimeProvider timeProvider;
    private MessageRowViewModel? selectedRow;
    private MailView? currentView;
    private bool hasMore;
    private bool isLoading;
    private int loadGeneration;
    private CancellationTokenSource? loadCancellation;
    private bool isClosed;

    internal MessageListViewModel(
        IMailboxReadModel readModel,
        ICriticalLog log,
        ShellBanner banner,
        TimeProvider timeProvider)
    {
        this.readModel = readModel;
        this.log = log;
        this.banner = banner;
        this.timeProvider = timeProvider;
    }

    /// <summary>The loaded rows.</summary>
    public ObservableCollection<MessageRowViewModel> Items { get; } = [];

    /// <summary>The selected row; null when none.</summary>
    public MessageRowViewModel? SelectedRow
    {
        get => selectedRow;
        set => SetProperty(ref selectedRow, value);
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

    /// <summary>The view the list shows or is loading.</summary>
    internal MailView? CurrentView => currentView;

    /// <summary>Shows the first page of a view; the selection is cleared at once.</summary>
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
        SelectedRow = null;
        Items.Clear();
        HasMore = false;
        await RunLoadAsync(
            "MessageListViewModel.LoadAsync",
            async token =>
            {
                (List<MessageRowViewModel> rows, bool isPageFull) = await ReadRowsAsync(view, PageSize, 0, token);
                return () =>
                {
                    foreach (MessageRowViewModel row in rows)
                    {
                        Items.Add(row);
                    }

                    HasMore = isPageFull;
                };
            },
            ct);
    }

    /// <summary>Appends the next page. Does nothing without a view, without more rows or during another load.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the page is appended.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (isClosed || currentView is not MailView view || !HasMore || IsLoading)
        {
            return;
        }

        int offset = Items.Count;
        await RunLoadAsync(
            "MessageListViewModel.LoadMoreAsync",
            async token =>
            {
                (List<MessageRowViewModel> rows, bool isPageFull) = await ReadRowsAsync(view, PageSize, offset, token);
                return () =>
                {
                    // New mail moves the rows down during paging, so a page can repeat rows that are already shown.
                    var knownIds = Items.Select(row => row.Message.Id).ToHashSet();
                    foreach (MessageRowViewModel row in rows.Where(row => knownIds.Add(row.Message.Id)))
                    {
                        Items.Add(row);
                    }

                    HasMore = isPageFull;
                };
            },
            ct);
    }

    /// <summary>
    /// Loads as many rows as are currently shown (at least one page) and updates the list in place, so that the scroll
    /// position stays. The selection stays by message id when the row still exists and is cleared otherwise.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the list is updated.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        if (isClosed || currentView is not MailView view)
        {
            return;
        }

        int limit = Math.Max(PageSize, Items.Count);
        await RunLoadAsync(
            "MessageListViewModel.ReloadAsync",
            async token =>
            {
                (List<MessageRowViewModel> rows, bool isPageFull) = await ReadRowsAsync(view, limit, 0, token);
                return () =>
                {
                    long? selectedId = SelectedRow?.Message.Id;
                    MergeRows(rows);
                    HasMore = isPageFull;
                    SelectedRow = selectedId is null ? null : rows.FirstOrDefault(row => row.Message.Id == selectedId);
                };
            },
            ct);
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

    private async Task<(List<MessageRowViewModel> Rows, bool IsPageFull)> ReadRowsAsync(
        MailView view,
        int limit,
        int offset,
        CancellationToken ct)
    {
        IReadOnlyList<Account> accounts = await readModel.ListAccountsAsync(ct);
        Dictionary<Guid, string> accountLabels = accounts.ToDictionary(account => account.Id, NavigationViewModel.AccountLabel);
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<MailMessage> messages = await readModel.ListMessagesAsync(view.ToQuery(limit, offset), ct);
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<MessageRowViewModel> rows = messages
            .Select(message => new MessageRowViewModel(
                message,
                accountLabels.GetValueOrDefault(message.AccountId, string.Empty),
                now,
                timeProvider.LocalTimeZone))
            .ToList();
        return (rows, messages.Count == limit);
    }

    // Brings Items to the order of newRows with as few collection changes as possible; unchanged rows keep their object.
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
}
