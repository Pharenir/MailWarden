using System.Diagnostics.CodeAnalysis;
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
/// The reading pane of the selected message: header texts, attachments and the safe document. Opening a message changes
/// nothing, also not its \Seen flag, and neither does anything done with its attachments. Continuations of the awaits stay on the
/// caller's context (the UI thread); of overlapping calls of <see cref="ShowAsync"/> only the newest changes the pane.
/// </summary>
public sealed class ReadingPaneViewModel : ObservableObject
{
    /// <summary>
    /// Most rows in <see cref="Attachments"/>; a crafted mail can hold many thousand parts. The parts beyond it can be neither saved
    /// nor opened here, and <see cref="HiddenAttachmentsNotice"/> names their number.
    /// </summary>
    public const int MaxAttachmentRows = 500;

    // Parser, CSS parser and serializer recurse on what the mail holds, and a stack overflow ends the process without any
    // handler. The limits of the limiter come first; this leaves room in proportion to the input for what they do not foresee.
    // Only reserved, not committed; at most one build runs at a time.
    private const int BuildThreadStackBytes = 256 * 1024 * 1024;

    private readonly IMailboxReadModel readModel;
    private readonly ICriticalLog log;
    private readonly ShellBanner banner;
    private readonly TimeProvider timeProvider;
    private readonly RelayCommand loadRemoteImagesCommand;
    private readonly Func<MailContent, bool, bool, ReadingTheme, ReadingDocument> documentBuilder;
    private readonly IAttachmentHost? attachmentHost;
    private readonly AttachmentFiles? attachmentFiles;
    private readonly Func<string, Task<bool>> confirmOpening;

    // Ends running attachment actions when the pane closes. The next mail does not: a save goes on for the mail it was started for.
    private readonly CancellationTokenSource attachmentCancellation = new();

    // Rows with an action running; a double click must not start a second one while the action runs. Touched on the UI thread only.
    private readonly HashSet<AttachmentRow> busyAttachmentRows = [];
    private ReadingState state = ReadingState.Empty;
    private string subject = string.Empty;
    private string from = string.Empty;
    private string to = string.Empty;
    private string cc = string.Empty;
    private string dateText = string.Empty;
    private IReadOnlyList<AttachmentRow> attachments = new List<AttachmentRow>();
    private string? attachmentNotice;
    private string? hiddenAttachmentsNotice;
    private string? documentHtml;
    private int blockedRemoteResourceCount;
    private bool areRemoteImagesAllowed;
    private MailContent? currentContent;

    // How the shown document came about; a theme change repeats it. Only meaningful together with currentContent.
    private ImageApproval currentApproval;
    private bool isCurrentDocumentFallback;
    private ReadingTheme theme = ReadingTheme.Light;
    private int showGeneration;
    private CancellationTokenSource? showCancellation;
    private bool isClosed;
    private string? contentNotice;
    private string? hoverLinkText;

    // The newest build. It cannot be stopped once it runs, so it may outlive the message it was started for.
    private Task<ReadingDocument>? runningBuild;
    private long runningBuildMessageId;
    private ImageApproval runningBuildApproval;
    private ReadingTheme runningBuildTheme;

    internal ReadingPaneViewModel(
        IMailboxReadModel readModel,
        ICriticalLog log,
        ShellBanner banner,
        TimeProvider timeProvider,
        Func<MailContent, bool, bool, ReadingTheme, ReadingDocument> documentBuilder,
        IAttachmentHost? attachmentHost,
        string? attachmentOpenFolder,
        Func<string, Task<bool>> confirmOpening)
    {
        if ((attachmentHost is null) != (attachmentOpenFolder is null))
        {
            throw new ArgumentException("The attachment host and the open folder only work together.", nameof(attachmentHost));
        }

        this.readModel = readModel;
        this.log = log;
        this.banner = banner;
        this.timeProvider = timeProvider;
        this.documentBuilder = documentBuilder;
        this.attachmentHost = attachmentHost;
        attachmentFiles = attachmentOpenFolder is null ? null : new AttachmentFiles(attachmentOpenFolder);
        this.confirmOpening = confirmOpening;
        loadRemoteImagesCommand = new RelayCommand(LoadRemoteImages, () => CanLoadRemoteImages);
    }

    /// <summary>
    /// Longest wait for the document of a message in one theme; afterwards it is shown as plain text. It is the backstop for
    /// what the limits of the sanitizer do not foresee: its work cannot be interrupted. Measured on the time provider of the
    /// shell.
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
    /// One row per entry of <see cref="MailContent.Attachments"/> of the shown mail, in that order, at most
    /// <see cref="MaxAttachmentRows"/>; empty without a mail. It only changes with the shown mail.
    /// </summary>
    public IReadOnlyList<AttachmentRow> Attachments
    {
        get => attachments;
        private set
        {
            if (SetProperty(ref attachments, value))
            {
                OnPropertyChanged(nameof(HasAttachments));
            }
        }
    }

    /// <summary>True when <see cref="Attachments"/> has rows.</summary>
    public bool HasAttachments => attachments.Count > 0;

    /// <summary>Fixed text with the number of attachments beyond <see cref="MaxAttachmentRows"/>; null when all have a row.</summary>
    public string? HiddenAttachmentsNotice
    {
        get => hiddenAttachmentsNotice;
        private set
        {
            if (SetProperty(ref hiddenAttachmentsNotice, value))
            {
                OnPropertyChanged(nameof(HasHiddenAttachments));
            }
        }
    }

    /// <summary>True when <see cref="HiddenAttachmentsNotice"/> has a text.</summary>
    public bool HasHiddenAttachments => hiddenAttachmentsNotice is not null;

    /// <summary>
    /// Readable message of the action on an attachment of the shown mail: that a copy runs, that a save is done, or why it failed;
    /// null when there is none, also after a cancelled dialog and a file that has opened. A fixed text, the next action and the next
    /// mail clear it.
    /// </summary>
    public string? AttachmentNotice
    {
        get => attachmentNotice;
        private set
        {
            if (SetProperty(ref attachmentNotice, value))
            {
                OnPropertyChanged(nameof(HasAttachmentNotice));
            }
        }
    }

    /// <summary>True when <see cref="AttachmentNotice"/> has a text.</summary>
    public bool HasAttachmentNotice => attachmentNotice is not null;

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

    /// <summary>
    /// Target of the link under the mouse, as the browser reports it. The text comes from the mail, so the setter cleans and
    /// shortens it, at most 501 characters; empty or nothing left means no link. An absolute http or https target beyond the limit
    /// starts with its real host, because the cut could hide it. The next message clears it.
    /// </summary>
    public string? HoverLinkText
    {
        get => hoverLinkText;
        set
        {
            string shortened = MailDisplayText.LinkTarget(value ?? string.Empty);
            if (SetProperty(ref hoverLinkText, shortened.Length == 0 ? null : shortened))
            {
                OnPropertyChanged(nameof(HasHoverLink));
            }
        }
    }

    /// <summary>True when <see cref="HoverLinkText"/> has a text.</summary>
    public bool HasHoverLink => hoverLinkText is not null;

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
    /// <see cref="BuildTimeLimit"/> from this call or from the last theme change during it; only the newest call survives the
    /// wait and then builds normally. The same mail with the same image approval and theme waits for the running build instead
    /// of starting a second one. When a wait exceeds the limit, the plain text is shown with a notice, and a later result of
    /// that build is ignored.
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
            BuiltDocument document = await BuildForCurrentThemeAsync(message.Id, content, approval, linkedCancellation.Token);
            if (generation != showGeneration)
            {
                return;
            }

            currentContent = content;
            ShowContentHeader(content, message, folderRole);
            ShowDocument(document, content, approval);
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

    /// <summary>
    /// Takes over the theme the application actually shows. The shown document is built again from the content already held,
    /// off the UI thread and without a read from the store or the server; message, image approval and state stay, and the old
    /// document stays visible until the new one is there. Without a shown document nothing is built. A show or image load that
    /// is running builds for the new theme by itself. A document that replaced a build over the limit stays plain text. When
    /// the build fails, the readable document stays in <see cref="ReadingState.Ready"/> with a critical log line with the type
    /// name. The reported theme still counts: the next build of any kind uses it, the same report again starts none.
    /// </summary>
    /// <param name="newTheme">The theme the window reports.</param>
    /// <returns>A task that completes when the build has ended or a newer call replaced it; it never fails.</returns>
    public Task ApplyThemeAsync(ReadingTheme newTheme)
    {
        if (newTheme == theme)
        {
            return Task.CompletedTask;
        }

        theme = newTheme;
        bool isDocumentIdle = !isClosed && showCancellation is null && State == ReadingState.Ready;
        return isDocumentIdle && currentContent is MailContent content && CurrentMessage is MailMessage message
            ? RebuildAsync(message.Id, content, currentApproval, isThemeChange: true)
            : Task.CompletedTask;
    }

    /// <summary>
    /// Saves an attachment: the save dialog of Windows, then a stream copy of the decoded part. Without a target nothing is
    /// written. The save goes on when another mail is selected; the notice stays with the old mail then, and a failure goes to the
    /// banner of the shell, because the new mail must not show it. A row that is no longer shown, or that has no content, does nothing.
    /// </summary>
    /// <param name="row">A row of <see cref="Attachments"/>.</param>
    /// <returns>A task that completes when the action has ended; it never fails, a failure shows in <see cref="AttachmentNotice"/>.</returns>
    public async Task SaveAttachmentAsync(AttachmentRow row)
    {
        if (!TryBeginAttachmentAction(row, out IAttachmentHost? host, out AttachmentFiles? files))
        {
            return;
        }

        IReadOnlyList<AttachmentRow> rowsOfAction = attachments;
        CancellationToken ct = attachmentCancellation.Token;
        try
        {
            string? targetPath = await host.PickSaveTargetAsync(row.SafeFileName, ct);
            if (targetPath is null)
            {
                return;
            }

            ShowNoticeForRows(rowsOfAction, UiText.AttachmentCopying);
            await files.SaveAsync(ContentWriterOf(row), targetPath, ct);
            ShowNoticeForRows(rowsOfAction, UiText.AttachmentSaved);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The pane has closed.
        }
        catch (Exception exception)
        {
            // Also OutOfMemoryException: what happens to one attachment must not end a running mail program.
            ReportAttachmentFailure(rowsOfAction, "ReadingPaneViewModel.SaveAttachmentAsync", "Saving an attachment failed", exception, UiText.AttachmentSaveFailed);
            if (!isClosed && !ReferenceEquals(rowsOfAction, attachments))
            {
                banner.Set(ShellBanner.BannerSource.AttachmentSave, UiText.BannerAttachmentSaveFailed);
            }
        }
        finally
        {
            busyAttachmentRows.Remove(row);
        }
    }

    /// <summary>
    /// Opens an attachment: reads the role of the folder again, asks first where <see cref="AttachmentPolicy.MustAskBeforeOpening"/>
    /// says so for the role now, or when it differs from the role at showing, prepares the marked file and hands it to the program
    /// Windows assigns. A role that cannot be read counts as unknown. A row with <see cref="AttachmentRow.CanOpen"/> false does
    /// nothing, and neither does one that is no longer shown, also after the question. Another mail selected during the preparation
    /// opens nothing either: the file goes again, without a notice.
    /// </summary>
    /// <param name="row">A row of <see cref="Attachments"/>.</param>
    /// <returns>A task that completes when the action has ended; it never fails, a failure shows in <see cref="AttachmentNotice"/>.</returns>
    public async Task OpenAttachmentAsync(AttachmentRow row)
    {
        if (row is not { CanOpen: true } || !TryBeginAttachmentAction(row, out IAttachmentHost? host, out AttachmentFiles? files))
        {
            return;
        }

        IReadOnlyList<AttachmentRow> rowsOfAction = attachments;
        CancellationToken ct = attachmentCancellation.Token;
        try
        {
            FolderRole? currentRole = await ReadCurrentRoleAsync(row, ct);
            bool hasFolderChanged = currentRole != row.FolderRole;
            bool mustAsk = hasFolderChanged || AttachmentPolicy.MustAskBeforeOpening(row.Risk, currentRole);
            if (mustAsk
                && ReferenceEquals(rowsOfAction, attachments)
                && !await confirmOpening(UiText.ConfirmOpenAttachment(row.Risk, currentRole, hasFolderChanged)))
            {
                return;
            }

            // The answer was given for the mail on screen; another one may have come during the wait.
            if (isClosed || !ReferenceEquals(rowsOfAction, attachments))
            {
                return;
            }

            ShowNoticeForRows(rowsOfAction, UiText.AttachmentCopying);
            string filePath = await files.PrepareForOpeningAsync(ContentWriterOf(row), row.SafeFileName, ct);
            if (isClosed || !ReferenceEquals(rowsOfAction, attachments))
            {
                files.Discard(filePath);
                return;
            }

            // The host has logged a refusal: only it knows the error code.
            bool isOpened = await host.OpenWithDefaultProgramAsync(filePath);
            if (!isOpened)
            {
                files.Discard(filePath);
            }

            if (ReferenceEquals(rowsOfAction, attachments))
            {
                AttachmentNotice = isOpened ? null : UiText.AttachmentOpenRefused;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The pane has closed.
        }
        catch (Exception exception)
        {
            // Also OutOfMemoryException: what happens to one attachment must not end a running mail program.
            ReportAttachmentFailure(rowsOfAction, "ReadingPaneViewModel.OpenAttachmentAsync", "Opening an attachment failed", exception, UiText.AttachmentOpenFailed);
        }
        finally
        {
            busyAttachmentRows.Remove(row);
        }
    }

    /// <summary>Ends the running load and attachment actions without a further read or log line; later calls do nothing.</summary>
    internal void Close()
    {
        isClosed = true;
        showGeneration++;
        CancelPreviousShow();
        attachmentCancellation.Cancel();
    }

    // Fixed texts only: names and paths in the exceptions stay out of the window.
    private static string DescribeAttachmentFailure(Exception exception, string otherwise) => exception switch
    {
        FileNotFoundException => UiText.AttachmentMailGone,
        MailContentException or ArgumentOutOfRangeException => UiText.AttachmentChangedOrUnreadable,
        IOException { Data: var details } when details[AttachmentFiles.MarkFailureDataKey] is true => UiText.AttachmentMarkFailed,
        IOException { Data: var details } when details[AttachmentFiles.OpenFolderIsLinkDataKey] is true => UiText.AttachmentOpenFolderIsLink,
        _ => otherwise,
    };

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

    // A theme change during the build makes its result stale; it builds again, so the old theme is never shown. Each round has
    // its own time limit; the number of rounds is bounded by the changes of the user.
    private async Task<BuiltDocument> BuildForCurrentThemeAsync(long messageId, MailContent content, ImageApproval approval, CancellationToken ct)
    {
        while (true)
        {
            ReadingTheme requestedTheme = theme;
            BuiltDocument document = await BuildWithinLimitAsync(messageId, content, approval, requestedTheme, ct);
            if (requestedTheme == theme)
            {
                return document;
            }
        }
    }

    // Sanitizing a large mail takes noticeable time, so it leaves the UI thread (see StartBuild). The plain text is linear in
    // its length.
    private async Task<BuiltDocument> BuildWithinLimitAsync(long messageId, MailContent content, ImageApproval approval, ReadingTheme requestedTheme, CancellationToken ct)
    {
        bool isOwnBuildRunning = runningBuild is { IsCompleted: false }
            && runningBuildMessageId == messageId
            && runningBuildApproval == approval
            && runningBuildTheme == requestedTheme;
        if (!isOwnBuildRunning && !await WaitForOtherBuildAsync(ct))
        {
            return new BuiltDocument(ReadingDocumentBuilder.BuildFromPlainText(content, requestedTheme), IsPlainTextFallback: true);
        }

        Task<ReadingDocument> build = isOwnBuildRunning ? runningBuild! : StartBuild(messageId, content, approval, requestedTheme);
        using var limitCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task limit = Task.Delay(BuildTimeLimit, timeProvider, limitCancellation.Token);
        if (await Task.WhenAny(build, limit) == build)
        {
            await limitCancellation.CancelAsync();
            return new BuiltDocument(await build, IsPlainTextFallback: false);
        }

        // A cancelled wait ends here with its exception.
        await limit;
        return new BuiltDocument(ReadingDocumentBuilder.BuildFromPlainText(content, requestedTheme), IsPlainTextFallback: true);
    }

    // On a thread of its own with a large stack, in the background so that it does not keep the process alive. Not cancelled
    // together with its caller: the same mail selected again keeps waiting for this task. It may go on without a reader.
    private Task<ReadingDocument> StartBuild(long messageId, MailContent content, ImageApproval approval, ReadingTheme requestedTheme)
    {
        var completion = new TaskCompletionSource<ReadingDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildThread = new Thread(
            () =>
            {
                try
                {
                    completion.SetResult(documentBuilder(content, approval.Remote, approval.Embedded, requestedTheme));
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
        runningBuildTheme = requestedTheme;
        ObserveFault(build);
        return build;
    }

    // True when a build may start now, false when the running build does not end within the limit. A newer call cancels this
    // wait, so intermediate selections never build anything. A build with other image approvals or for another theme counts
    // as another mail.
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

    // True when the action may start. It is then marked as running; the caller ends that. The notice of the action before goes.
    private bool TryBeginAttachmentAction(
        AttachmentRow row,
        [NotNullWhen(true)] out IAttachmentHost? host,
        [NotNullWhen(true)] out AttachmentFiles? files)
    {
        host = attachmentHost;
        files = attachmentFiles;
        if (isClosed || row is not { HasContent: true } || !attachments.Contains(row))
        {
            return false;
        }

        if (host is null || files is null)
        {
            AttachmentNotice = UiText.AttachmentActionsUnavailable;
            return false;
        }

        if (!busyAttachmentRows.Add(row))
        {
            return false;
        }

        banner.Clear(ShellBanner.BannerSource.AttachmentSave);
        AttachmentNotice = null;
        return true;
    }

    // Whatever keeps the role from being read counts as an unknown folder, which asks. Only the closing pane ends the action here.
    private async Task<FolderRole?> ReadCurrentRoleAsync(AttachmentRow row, CancellationToken ct)
    {
        try
        {
            return await readModel.GetFolderRoleAsync(row.Message.FolderId, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private Func<Stream, CancellationToken, Task> ContentWriterOf(AttachmentRow row)
    {
        return (destination, ct) => readModel.CopyAttachmentAsync(row.Message, row.Index, row.Attachment, destination, ct);
    }

    // The type name and the code only: messages of file exceptions name the path, and that names the attachment.
    private void ReportAttachmentFailure(IReadOnlyList<AttachmentRow> rowsOfAction, string source, string description, Exception exception, string otherwise)
    {
        if (isClosed)
        {
            return;
        }

        log.Write(
            CriticalLogLevel.Critical,
            source,
            string.Create(CultureInfo.InvariantCulture, $"{description}: {exception.GetType().Name} (HResult 0x{exception.HResult:X8})"));
        ShowNoticeForRows(rowsOfAction, DescribeAttachmentFailure(exception, otherwise));
    }

    // The rows and the notice belong to the mail on screen; what comes from an action on the mail before is only logged.
    private void ShowNoticeForRows(IReadOnlyList<AttachmentRow> rowsOfAction, string notice)
    {
        if (ReferenceEquals(rowsOfAction, attachments))
        {
            AttachmentNotice = notice;
        }
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
        HoverLinkText = null;
        BlockedRemoteResourceCount = 0;
        AreRemoteImagesAllowed = false;
        Subject = message is null ? string.Empty : MailDisplayText.Subject(message.Subject);
        From = message is null ? string.Empty : MailDisplayText.SenderLine([new MailSender(message.FromName, message.FromAddress)]);
        To = string.Empty;
        Cc = string.Empty;
        DateText = message is null ? string.Empty : FormatDate(message.Date);
        Attachments = new List<AttachmentRow>();
        HiddenAttachmentsNotice = null;
        AttachmentNotice = null;
    }

    private void ShowContentHeader(MailContent content, MailMessage message, FolderRole? folderRole)
    {
        Subject = MailDisplayText.Subject(content.Subject);
        From = MailDisplayText.SenderLine(content.Senders ?? [new MailSender(string.Empty, content.From)]);
        To = MailDisplayText.Recipients(content.To);
        Cc = MailDisplayText.Recipients(content.Cc);
        DateText = FormatDate(content.Date ?? message.Date);
        Attachments = content.Attachments
            .Take(MaxAttachmentRows)
            .Select((attachment, index) => new AttachmentRow(message, folderRole, attachment, index))
            .ToList();
        HiddenAttachmentsNotice = content.Attachments.Count > MaxAttachmentRows
            ? UiText.AttachmentsNotListed(content.Attachments.Count - MaxAttachmentRows)
            : null;
    }

    private void ShowDocument(BuiltDocument built, MailContent content, ImageApproval approval)
    {
        currentApproval = approval;
        isCurrentDocumentFallback = built.IsPlainTextFallback;
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
            // At once, so the button is gone and cannot start a second build.
            AreRemoteImagesAllowed = true;
            _ = RebuildAsync(message.Id, content, new ImageApproval(Remote: true, Embedded: true), isThemeChange: false);
        }
    }

    // A new document for the message already shown, from the content already held. Same guard as ShowAsync: the next message
    // makes this result stale. Never throws, nobody has to await it.
    private async Task RebuildAsync(long messageId, MailContent content, ImageApproval approval, bool isThemeChange)
    {
        int generation = ++showGeneration;
        CancelPreviousShow();
        using var cancellation = new CancellationTokenSource();
        showCancellation = cancellation;
        try
        {
            // A mail that went over the limit once does not get a second long build for another color.
            BuiltDocument document = isThemeChange && isCurrentDocumentFallback
                ? new BuiltDocument(ReadingDocumentBuilder.BuildFromPlainText(content, theme), IsPlainTextFallback: true)
                : await BuildForCurrentThemeAsync(messageId, content, approval, cancellation.Token);
            if (generation == showGeneration)
            {
                ShowDocument(document, content, approval);
            }
        }
        catch (OperationCanceledException) when (generation != showGeneration)
        {
            // Replaced by the next message.
        }
        catch (Exception exception)
        {
            if (generation == showGeneration && isThemeChange)
            {
                // The document on screen is still readable, only in the previous theme. That is better than an error.
                log.Write(CriticalLogLevel.Critical, "ReadingPaneViewModel.ApplyThemeAsync", $"Building for the new theme failed: {exception.GetType().Name}");
            }
            else if (generation == showGeneration)
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
