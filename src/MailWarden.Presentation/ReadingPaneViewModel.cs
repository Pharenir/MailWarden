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
    // Parser, CSS parser and serializer recurse on what the mail holds, and a stack overflow ends the process without any
    // handler. The limits of the limiter come first; this leaves room in proportion to the input for what they do not foresee.
    // Only reserved, not committed; at most one build runs at a time.
    private const int BuildThreadStackBytes = 256 * 1024 * 1024;

    private readonly IMailboxReadModel readModel;
    private readonly ICriticalLog log;
    private readonly TimeProvider timeProvider;
    private readonly RelayCommand loadRemoteImagesCommand;
    private readonly Func<MailContent, bool, bool, ReadingDocument> documentBuilder;
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
    private bool isClosed;
    private string? contentNotice;

    // The newest build. It cannot be stopped once it runs, so it may outlive the message it was started for.
    private Task<ReadingDocument>? runningBuild;
    private long runningBuildMessageId;
    private ImageApproval runningBuildApproval;

    internal ReadingPaneViewModel(
        IMailboxReadModel readModel,
        ICriticalLog log,
        TimeProvider timeProvider,
        Func<MailContent, bool, bool, ReadingDocument> documentBuilder)
    {
        this.readModel = readModel;
        this.log = log;
        this.timeProvider = timeProvider;
        this.documentBuilder = documentBuilder;
        loadRemoteImagesCommand = new RelayCommand(LoadRemoteImages, () => CanLoadRemoteImages);
    }

    /// <summary>
    /// Longest wait for the document of a message; afterwards it is shown as plain text. It is the backstop for what the
    /// limits of the sanitizer do not foresee: its work cannot be interrupted. Measured on the time provider of the shell.
    /// </summary>
    public static TimeSpan BuildTimeLimit { get; } = TimeSpan.FromSeconds(5);

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

    /// <summary>Shortened subject; "(Kein Betreff)" when empty; empty without a message.</summary>
    public string Subject
    {
        get => subject;
        private set => SetProperty(ref subject, value);
    }

    /// <summary>Shortened sender.</summary>
    public string From
    {
        get => from;
        private set => SetProperty(ref from, value);
    }

    /// <summary>Shortened To recipients, comma separated.</summary>
    public string To
    {
        get => to;
        private set => SetProperty(ref to, value);
    }

    /// <summary>Shortened Cc recipients, comma separated.</summary>
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

    /// <summary>
    /// Shortened names of at most 50 attachments, then one entry with the number of the others; they cannot be opened or
    /// saved in this version.
    /// </summary>
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

    /// <summary>Fixed text about what the document does not show, above the document; null when nothing is missing.</summary>
    public string? ContentNotice
    {
        get => contentNotice;
        private set
        {
            if (SetProperty(ref contentNotice, value))
            {
                OnPropertyChanged(nameof(HasContentNotice));
            }
        }
    }

    /// <summary>True when <see cref="ContentNotice"/> has a text.</summary>
    public bool HasContentNotice => contentNotice is not null;

    /// <summary>Number of images blocked in the current document: remote ones and, in Junk and Quarantine, embedded ones.</summary>
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
    /// Rebuilds the document with remote and embedded images allowed, off the UI thread and without reading the EML again.
    /// The approval holds only for the current message; a result that arrives after the next message is ignored.
    /// </summary>
    public ICommand LoadRemoteImagesCommand => loadRemoteImagesCommand;

    /// <summary>The message the pane shows or is loading.</summary>
    internal MailMessage? CurrentMessage { get; private set; }

    /// <summary>
    /// Shows a message, or the empty pane for null. Embedded images of a message in a folder with role Junk or Quarantine, or
    /// in an unknown folder, wait for the approval as well; the role of the message's folder decides, not the selected view.
    /// A failure ends in <see cref="ReadingState.Error"/> with a fixed text and
    /// a critical log line with the type name. At most one document build runs at a time. While the build of another mail
    /// runs, the pane stays in <see cref="ReadingState.Loading"/> and waits for its end, at most
    /// <see cref="BuildTimeLimit"/> from this call; only the newest call survives the wait and then builds normally. The
    /// same mail with the same image approval waits for the running build instead of starting a second one. When a wait
    /// exceeds the limit, the plain text is shown with a notice, and a later result of that build is ignored.
    /// </summary>
    /// <param name="message">Message to show, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when this call ended or a newer one replaced it.</returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled; the pane is then empty, it does not show an error.
    /// </exception>
    public async Task ShowAsync(MailMessage? message, CancellationToken ct = default)
    {
        if (isClosed)
        {
            return;
        }

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

            FolderRole? folderRole = await readModel.GetFolderRoleAsync(message.FolderId, linkedCancellation.Token);
            if (generation != showGeneration)
            {
                return;
            }

            var approval = new ImageApproval(Remote: false, Embedded: folderRole is not (null or FolderRole.Junk or FolderRole.Quarantine));
            BuiltDocument document = await BuildWithinLimitAsync(message.Id, content, approval, linkedCancellation.Token);
            if (generation != showGeneration)
            {
                return;
            }

            currentContent = content;
            ShowContentHeader(content, message);
            ShowDocument(document, content);
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
        catch (Exception exception)
        {
            // Also OutOfMemoryException: a mail too large for the memory must not leave the pane in Loading.
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

    /// <summary>Ends the running load without a further read or log line; later calls do nothing.</summary>
    internal void Close()
    {
        isClosed = true;
        showGeneration++;
        CancelPreviousShow();
    }

    private static string? DescribeMissingContent(BuiltDocument built, MailContent content)
    {
        string? documentNotice = built.IsPlainTextFallback
            ? UiText.ContentShownAsText
            : built.Document.WasReduced ? UiText.ContentReduced : null;
        string? opaqueNotice = content.HasOpaqueParts ? UiText.ContentHasOpaqueParts : null;
        return documentNotice is not null && opaqueNotice is not null
            ? $"{documentNotice} {opaqueNotice}"
            : documentNotice ?? opaqueNotice;
    }

    // A task that nobody waits for any more must not end as an unobserved exception.
    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static abandonedTask => _ = abandonedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // Sanitizing a large mail takes noticeable time, so it leaves the UI thread (see StartBuild). The plain text is linear in
    // its length.
    private async Task<BuiltDocument> BuildWithinLimitAsync(long messageId, MailContent content, ImageApproval approval, CancellationToken ct)
    {
        bool isOwnBuildRunning = runningBuild is { IsCompleted: false }
            && runningBuildMessageId == messageId
            && runningBuildApproval == approval;
        if (!isOwnBuildRunning && !await WaitForOtherBuildAsync(ct))
        {
            return new BuiltDocument(ReadingDocumentBuilder.BuildFromPlainText(content), IsPlainTextFallback: true);
        }

        Task<ReadingDocument> build = isOwnBuildRunning ? runningBuild! : StartBuild(messageId, content, approval);
        using var limitCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task limit = Task.Delay(BuildTimeLimit, timeProvider, limitCancellation.Token);
        if (await Task.WhenAny(build, limit) == build)
        {
            await limitCancellation.CancelAsync();
            return new BuiltDocument(await build, IsPlainTextFallback: false);
        }

        // A cancelled wait ends here with its exception.
        await limit;
        return new BuiltDocument(ReadingDocumentBuilder.BuildFromPlainText(content), IsPlainTextFallback: true);
    }

    // On a thread of its own with a large stack, in the background so that it does not keep the process alive. Not cancelled
    // together with its caller: the same mail selected again keeps waiting for this task. It may go on without a reader.
    private Task<ReadingDocument> StartBuild(long messageId, MailContent content, ImageApproval approval)
    {
        var completion = new TaskCompletionSource<ReadingDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildThread = new Thread(
            () =>
            {
                try
                {
                    completion.SetResult(documentBuilder(content, approval.Remote, approval.Embedded));
                }
                catch (Exception exception)
                {
                    // Also OutOfMemoryException: the caller shows the failure, the pane must not stay in Loading.
                    completion.SetException(exception);
                }
            },
            BuildThreadStackBytes)
        {
            IsBackground = true,
            Name = "MailWarden document build",
        };
        buildThread.Start();

        Task<ReadingDocument> build = completion.Task;
        runningBuild = build;
        runningBuildMessageId = messageId;
        runningBuildApproval = approval;
        ObserveFault(build);
        return build;
    }

    // True when a build may start now, false when the running build does not end within the limit. A newer call cancels this
    // wait, so intermediate selections never build anything. A build with other image approvals counts as another mail.
    private async Task<bool> WaitForOtherBuildAsync(CancellationToken ct)
    {
        if (runningBuild is not { IsCompleted: false })
        {
            ct.ThrowIfCancellationRequested();
            return true;
        }

        using var limitCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task limit = Task.Delay(BuildTimeLimit, timeProvider, limitCancellation.Token);
        if (await Task.WhenAny(runningBuild, limit) != runningBuild)
        {
            await limit;
            return false;
        }

        await limitCancellation.CancelAsync();
        ct.ThrowIfCancellationRequested();
        return true;
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
        ContentNotice = null;
        BlockedRemoteResourceCount = 0;
        AreRemoteImagesAllowed = false;
        Subject = message is null ? string.Empty : MailDisplayText.Subject(message.Subject);
        From = message is null ? string.Empty : MailDisplayText.SenderLine([new MailSender(message.FromName, message.FromAddress)]);
        To = string.Empty;
        Cc = string.Empty;
        DateText = message is null ? string.Empty : FormatDate(message.Date);
        AttachmentNames = [];
    }

    private void ShowContentHeader(MailContent content, MailMessage message)
    {
        Subject = MailDisplayText.Subject(content.Subject);
        From = MailDisplayText.SenderLine(content.Senders ?? [new MailSender(string.Empty, content.From)]);
        To = MailDisplayText.Recipients(content.To);
        Cc = MailDisplayText.Recipients(content.Cc);
        DateText = FormatDate(content.Date ?? message.Date);
        AttachmentNames = MailDisplayText.AttachmentNames(content.Attachments.Select(attachment => attachment.FileName).ToList());
    }

    private void ShowDocument(BuiltDocument built, MailContent content)
    {
        BlockedRemoteResourceCount = built.Document.BlockedRemoteResourceCount;
        ContentNotice = DescribeMissingContent(built, content);
        DocumentHtml = built.Document.Html;
        State = ReadingState.Ready;
    }

    private void ShowFailure(string source, Exception exception)
    {
        currentContent = null;
        DocumentHtml = null;
        ContentNotice = null;
        BlockedRemoteResourceCount = 0;
        State = ReadingState.Error;
        log.Write(CriticalLogLevel.Critical, source, $"Showing the message failed: {exception.GetType().Name}");
    }

    private void LoadRemoteImages()
    {
        if (CanLoadRemoteImages && currentContent is MailContent content && CurrentMessage is MailMessage message)
        {
            _ = LoadRemoteImagesAsync(message.Id, content);
        }
    }

    // Same guard as ShowAsync: the next message makes this result stale. Never throws, nobody can await it.
    private async Task LoadRemoteImagesAsync(long messageId, MailContent content)
    {
        int generation = ++showGeneration;
        CancelPreviousShow();
        using var cancellation = new CancellationTokenSource();
        showCancellation = cancellation;

        // At once, so the button is gone and cannot start a second build.
        AreRemoteImagesAllowed = true;
        try
        {
            BuiltDocument document = await BuildWithinLimitAsync(messageId, content, new ImageApproval(Remote: true, Embedded: true), cancellation.Token);
            if (generation == showGeneration)
            {
                ShowDocument(document, content);
            }
        }
        catch (OperationCanceledException) when (generation != showGeneration)
        {
            // Replaced by the next message.
        }
        catch (Exception exception)
        {
            if (generation == showGeneration)
            {
                ShowFailure("ReadingPaneViewModel.LoadRemoteImagesAsync", exception);
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

    /// <summary>Which images a document may show.</summary>
    /// <param name="Remote">True when remote images stay.</param>
    /// <param name="Embedded">True when <c>cid:</c> and <c>data:</c> images stay.</param>
    private readonly record struct ImageApproval(bool Remote, bool Embedded);

    /// <summary>A document and how it came about.</summary>
    /// <param name="Document">The document.</param>
    /// <param name="IsPlainTextFallback">True when it replaces a build that took too long or could not start.</param>
    private sealed record BuiltDocument(ReadingDocument Document, bool IsPlainTextFallback);
}
