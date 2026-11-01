using System.Globalization;
using System.Windows.Input;
using MailWarden.Core.Model;
using MailWarden.Core.Text;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation;

/// <summary>
/// State of the reading pane.
/// </summary>
public enum ReadingState
{
    /// <summary>No message selected.</summary>
    Empty = 1,

    /// <summary>The message is being read.</summary>
    Loading = 2,

    /// <summary>The document can be shown.</summary>
    Ready = 3,

    /// <summary>The message has no local copy.</summary>
    NotDownloaded = 4,

    /// <summary>Reading or preparing the message failed.</summary>
    Error = 5,
}

/// <summary>
/// The reading pane of the selected message: header texts and the safe document. Opening a message changes nothing, also
/// not its \Seen flag. Continuations of the awaits stay on the caller's context (the UI thread); of overlapping calls of
/// <see cref="ShowAsync"/> only the newest changes the pane.
/// </summary>
public sealed class ReadingPaneViewModel : ObservableObject
{
    private readonly IMailboxReadModel readModel;
    private readonly ICriticalLog log;
    private readonly TimeProvider timeProvider;
    private readonly RelayCommand loadRemoteImagesCommand;
    private ReadingState state = ReadingState.Empty;
    private string subject = string.Empty;
    private string from = string.Empty;
    private string to = string.Empty;
    private string cc = string.Empty;
    private string dateText = string.Empty;
    private IReadOnlyList<string> attachmentNames = [];
    private string? documentHtml;
    private int blockedRemoteResourceCount;
    private bool areRemoteImagesAllowed;
    private MailContent? currentContent;
    private int showGeneration;
    private CancellationTokenSource? showCancellation;

    internal ReadingPaneViewModel(IMailboxReadModel readModel, ICriticalLog log, TimeProvider timeProvider)
    {
        this.readModel = readModel;
        this.log = log;
        this.timeProvider = timeProvider;
        loadRemoteImagesCommand = new RelayCommand(LoadRemoteImages, () => CanLoadRemoteImages);
    }

    /// <summary>State of the pane.</summary>
    public ReadingState State
    {
        get => state;
        private set
        {
            if (SetProperty(ref state, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CanLoadRemoteImages));
                loadRemoteImagesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Fixed text shown instead of the document; empty in <see cref="ReadingState.Ready"/>.</summary>
    public string StatusText => State switch
    {
        ReadingState.Empty => UiText.ReadingEmpty,
        ReadingState.Loading => UiText.ReadingLoading,
        ReadingState.NotDownloaded => UiText.ReadingNotDownloaded,
        ReadingState.Error => UiText.ReadingError,
        _ => string.Empty,
    };

    /// <summary>Cleaned subject; "(Kein Betreff)" when empty; empty without a message.</summary>
    public string Subject
    {
        get => subject;
        private set => SetProperty(ref subject, value);
    }

    /// <summary>Cleaned sender.</summary>
    public string From
    {
        get => from;
        private set => SetProperty(ref from, value);
    }

    /// <summary>Cleaned To recipients, comma separated.</summary>
    public string To
    {
        get => to;
        private set => SetProperty(ref to, value);
    }

    /// <summary>Cleaned Cc recipients, comma separated.</summary>
    public string Cc
    {
        get => cc;
        private set => SetProperty(ref cc, value);
    }

    /// <summary>Local date and time of the message.</summary>
    public string DateText
    {
        get => dateText;
        private set => SetProperty(ref dateText, value);
    }

    /// <summary>Cleaned names of the attachments; they cannot be opened or saved in this version.</summary>
    public IReadOnlyList<string> AttachmentNames
    {
        get => attachmentNames;
        private set => SetProperty(ref attachmentNames, value);
    }

    /// <summary>Output of <see cref="ReadingDocumentBuilder"/>; null unless <see cref="ReadingState.Ready"/>.</summary>
    public string? DocumentHtml
    {
        get => documentHtml;
        private set => SetProperty(ref documentHtml, value);
    }

    /// <summary>Number of remote resources blocked in the current document.</summary>
    public int BlockedRemoteResourceCount
    {
        get => blockedRemoteResourceCount;
        private set
        {
            if (SetProperty(ref blockedRemoteResourceCount, value))
            {
                OnPropertyChanged(nameof(CanLoadRemoteImages));
                loadRemoteImagesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when the user approved remote images for the current message.</summary>
    public bool AreRemoteImagesAllowed
    {
        get => areRemoteImagesAllowed;
        private set
        {
            if (SetProperty(ref areRemoteImagesAllowed, value))
            {
                OnPropertyChanged(nameof(CanLoadRemoteImages));
                loadRemoteImagesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True in <see cref="ReadingState.Ready"/> with blocked resources that are not yet allowed.</summary>
    public bool CanLoadRemoteImages =>
        State == ReadingState.Ready && BlockedRemoteResourceCount > 0 && !AreRemoteImagesAllowed;

    /// <summary>
    /// Rebuilds the document with remote images allowed, without reading the EML again. The approval holds only for the
    /// current message.
    /// </summary>
    public ICommand LoadRemoteImagesCommand => loadRemoteImagesCommand;

    /// <summary>The message the pane shows or is loading.</summary>
    internal MailMessage? CurrentMessage { get; private set; }

    /// <summary>
    /// Shows a message, or the empty pane for null. A failure ends in <see cref="ReadingState.Error"/> with a fixed text and
    /// a critical log line with the type name.
    /// </summary>
    /// <param name="message">Message to show, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when this call ended or a newer one replaced it.</returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled; the pane is then empty, it does not show an error.
    /// </exception>
    public async Task ShowAsync(MailMessage? message, CancellationToken ct = default)
    {
        int generation = ++showGeneration;
        CancelPreviousShow();
        ShowHeader(message);
        CurrentMessage = message;
        if (message is null)
        {
            State = ReadingState.Empty;
            return;
        }

        State = ReadingState.Loading;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        showCancellation = linkedCancellation;
        try
        {
            MailContent? content = await readModel.ReadContentAsync(message, linkedCancellation.Token);
            if (generation != showGeneration)
            {
                return;
            }

            if (content is null)
            {
                State = ReadingState.NotDownloaded;
                return;
            }

            // Sanitizing a large mail takes noticeable time, so it leaves the UI thread here.
            ReadingDocument document = await Task.Run(
                () => ReadingDocumentBuilder.Build(content, allowRemoteImages: false),
                linkedCancellation.Token);
            if (generation != showGeneration)
            {
                return;
            }

            currentContent = content;
            ShowContentHeader(content, message);
            ShowDocument(document);
        }
        catch (OperationCanceledException) when (generation != showGeneration && !ct.IsCancellationRequested)
        {
            // Replaced by a newer call.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (generation == showGeneration)
            {
                CurrentMessage = null;
                ShowHeader(null);
                State = ReadingState.Empty;
            }

            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation == showGeneration)
            {
                ShowFailure("ReadingPaneViewModel.ShowAsync", exception);
            }
        }
        finally
        {
            if (generation == showGeneration)
            {
                showCancellation = null;
            }
        }
    }

    private static string CleanedJoin(IEnumerable<string> values)
    {
        return string.Join(", ", values.Select(SafeDisplayText.Clean));
    }

    private static string CleanSubject(string subject)
    {
        string cleaned = SafeDisplayText.Clean(subject);
        return string.IsNullOrWhiteSpace(cleaned) ? UiText.NoSubject : cleaned;
    }

    private static string DescribeSender(MailMessage message)
    {
        string name = SafeDisplayText.Clean(message.FromName);
        string address = SafeDisplayText.Clean(message.FromAddress);
        return string.IsNullOrWhiteSpace(name) ? address : $"{name} <{address}>";
    }

    private void CancelPreviousShow()
    {
        try
        {
            showCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That call has just ended.
        }

        showCancellation = null;
    }

    private string FormatDate(DateTimeOffset date)
    {
        return TimeZoneInfo.ConvertTime(date, timeProvider.LocalTimeZone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    // The header from the stored message; everything of the previous message goes, also its image approval.
    private void ShowHeader(MailMessage? message)
    {
        currentContent = null;
        DocumentHtml = null;
        BlockedRemoteResourceCount = 0;
        AreRemoteImagesAllowed = false;
        Subject = message is null ? string.Empty : CleanSubject(message.Subject);
        From = message is null ? string.Empty : DescribeSender(message);
        To = string.Empty;
        Cc = string.Empty;
        DateText = message is null ? string.Empty : FormatDate(message.Date);
        AttachmentNames = [];
    }

    private void ShowContentHeader(MailContent content, MailMessage message)
    {
        Subject = CleanSubject(content.Subject);
        From = SafeDisplayText.Clean(content.From);
        To = CleanedJoin(content.To);
        Cc = CleanedJoin(content.Cc);
        DateText = FormatDate(content.Date ?? message.Date);
        AttachmentNames = content.Attachments
            .Select(attachment => SafeDisplayText.Clean(attachment.FileName))
            .Select(name => string.IsNullOrWhiteSpace(name) ? UiText.UnnamedAttachment : name)
            .ToList();
    }

    private void ShowDocument(ReadingDocument document)
    {
        BlockedRemoteResourceCount = document.BlockedRemoteResourceCount;
        DocumentHtml = document.Html;
        State = ReadingState.Ready;
    }

    private void ShowFailure(string source, Exception exception)
    {
        currentContent = null;
        DocumentHtml = null;
        BlockedRemoteResourceCount = 0;
        State = ReadingState.Error;
        log.Write(LogLevels.Critical, source, $"Showing the message failed: {exception.GetType().Name}");
    }

    // Synchronous: the command has no way to report completion, and the same input was sanitized once before.
    private void LoadRemoteImages()
    {
        if (!CanLoadRemoteImages || currentContent is null)
        {
            return;
        }

        try
        {
            ReadingDocument document = ReadingDocumentBuilder.Build(currentContent, allowRemoteImages: true);
            AreRemoteImagesAllowed = true;
            ShowDocument(document);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowFailure("ReadingPaneViewModel.LoadRemoteImages", exception);
        }
    }
}
