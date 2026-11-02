using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation.Tests;

/// <summary>
/// AP-04c cases 13-20 (A2, A3, A5, A6, A8, A10, A12): saving and opening through the reading pane. The save dialog and
/// the launch are fakes; files are written for real below the test output (<see cref="ShellHarness.WorkRoot"/>).
/// The Zone.Identifier mark is checked in AttachmentFilesTests, not here.
/// </summary>
[Trait("Category", "feature:ui")]
public sealed class AttachmentActionsTests : IDisposable
{
    private const string HarmlessName = "report_a.pdf";
    private const string AskName = "makro_b.docm";
    private const string BlockedName = "setup_c.exe";

    private readonly List<ShellHarness> _harnesses = [];

    public void Dispose()
    {
        foreach (ShellHarness harness in _harnesses)
        {
            harness.Dispose();
        }
    }

    private static MailAttachmentInfo[] ThreeAttachments() =>
    [
        new(HarmlessName, "application/pdf", 20),
        new(AskName, "application/octet-stream", 30),
        new(BlockedName, "application/octet-stream", 40),
    ];

    private async Task<(ShellHarness Harness, MailMessage Message)> ShowAsync(
        FolderRole? role, params MailAttachmentInfo[] attachments)
    {
        var harness = new ShellHarness();
        _harnesses.Add(harness);
        Account account = harness.ReadModel.AddAccount("Account_A");
        MailFolder folder = harness.ReadModel.AddFolder(account, role ?? FolderRole.Inbox);
        if (role is null)
        {
            harness.ReadModel.SetFolderRole(folder.Id, null);
        }

        MailMessage message = harness.ReadModel.AddMessage(folder, "attachment_mail", hasAttachments: true);
        harness.ReadModel.SetContent(message.Id, TestContent.Plain("attachment_mail", "body", attachments));
        await harness.Shell.Reading.ShowAsync(message);
        return (harness, message);
    }

    private static AttachmentRow RowNamed(ShellHarness harness, string name) =>
        harness.Shell.Reading.Attachments.Single(row => row.Name == name);

    private static string SaveTargetIn(ShellHarness harness, string fileName = "saved_a.bin") =>
        Path.Combine(harness.WorkRoot, "save", fileName);

    /// <summary>Files below the open folder (the folder may not exist).</summary>
    private static string[] FilesInOpenFolder(ShellHarness harness) =>
        Directory.Exists(harness.AttachmentOpenFolder)
            ? Directory.GetFiles(harness.AttachmentOpenFolder, "*", SearchOption.AllDirectories)
            : [];

    // ---- Save (A2, case 13, case 21 prerequisite) -----------------------------------------------------------

    // A2: the dialog proposes the safe name, the target holds the decoded bytes, nothing else is left beside it.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Save_writes_the_decoded_part_to_the_chosen_target_after_the_dialog()
    {
        (ShellHarness harness, MailMessage message) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        AttachmentRow row = RowNamed(harness, HarmlessName);
        string target = SaveTargetIn(harness);
        harness.AttachmentHost.SaveTarget = target;
        harness.ReadModel.SetAttachmentBytes(message.Id, row.Index, [1, 2, 3, 0, 255, 254]);

        await harness.Shell.Reading.SaveAttachmentAsync(row);

        Assert.Equal(row.SafeFileName, Assert.Single(harness.AttachmentHost.ProposedNames));
        Assert.Equal((message.Id, row.Index), Assert.Single(harness.ReadModel.CopyCalls));
        Assert.Equal(new byte[] { 1, 2, 3, 0, 255, 254 }, await File.ReadAllBytesAsync(target));
        Assert.Equal(new[] { target }, Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    // A2 / A5: a blocked type can be saved (and only saved).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_blocked_type_can_be_saved()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        AttachmentRow row = RowNamed(harness, BlockedName);
        string target = SaveTargetIn(harness, "setup_c.exe");
        harness.AttachmentHost.SaveTarget = target;

        await harness.Shell.Reading.SaveAttachmentAsync(row);

        Assert.Equal(FakeMailboxReadModel.DefaultAttachmentBytes(row.Index), await File.ReadAllBytesAsync(target));
        Assert.Empty(harness.ConfirmationTexts);
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
    }

    // A2 / A6: saving never asks, in no folder.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Junk)]
    [InlineData(FolderRole.Quarantine)]
    [InlineData(FolderRole.Screener)]
    [InlineData(FolderRole.Trash)]
    public async Task Saving_from_a_risky_folder_asks_nothing(FolderRole role)
    {
        (ShellHarness harness, _) = await ShowAsync(role, ThreeAttachments());
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);

        await harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Empty(harness.ConfirmationTexts);
        Assert.True(File.Exists(SaveTargetIn(harness)));
    }

    // Contract: host and open folder come together or not at all.
    [Fact]
    public void The_shell_takes_the_attachment_host_and_the_open_folder_together_or_not_at_all()
    {
        var readModel = new FakeMailboxReadModel();

        ShellViewModel Build(IAttachmentHost? host, string? folder) => new(
            readModel, new FakeMailboxController(), new FakeMailboxActions(readModel), new FakeUiDispatcher(),
            new FakeUiSettingsStore { Stored = UiSettings.Default }, new FakeCriticalLog(), new FixedClock(ShellHarness.FixedNow),
            null, host, folder);

        string folderPath = Path.Combine(AppContext.BaseDirectory, "ap04c-ui", "ctor-check");
        Assert.Throws<ArgumentException>(() => Build(new FakeAttachmentHost(), null));
        Assert.Throws<ArgumentException>(() => Build(null, folderPath));
        Assert.Null(Record.Exception(() => Build(null, null)));
        Assert.Null(Record.Exception(() => Build(new FakeAttachmentHost(), folderPath)));
    }

    // Case 13: cancelled dialog.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_cancelled_save_dialog_writes_nothing_and_shows_no_notice()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.AttachmentHost.SaveTarget = null;

        await harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Single(harness.AttachmentHost.ProposedNames);
        Assert.Empty(harness.ReadModel.CopyCalls);
        Assert.Null(harness.Shell.Reading.AttachmentNotice);
        Assert.False(Directory.Exists(Path.Combine(harness.WorkRoot, "save")));
        Assert.Empty(harness.Log.Entries);
    }

    // ---- Open: harmless, ask, blocked (cases 14-17) ----------------------------------------------------------

    // Case 14
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_harmless_type_from_the_inbox_opens_at_once_with_the_prepared_path()
    {
        (ShellHarness harness, MailMessage message) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        AttachmentRow row = RowNamed(harness, HarmlessName);
        harness.ReadModel.SetAttachmentBytes(message.Id, row.Index, [9, 8, 7, 6]);

        await harness.Shell.Reading.OpenAttachmentAsync(row);

        Assert.Empty(harness.ConfirmationTexts);
        string opened = Assert.Single(harness.AttachmentHost.OpenedPaths);
        Assert.True(Path.IsPathFullyQualified(opened));
        Assert.StartsWith(Path.GetFullPath(harness.AttachmentOpenFolder), opened, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(row.SafeFileName, Path.GetFileName(opened));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, Assert.Single(harness.AttachmentHost.OpenedContents));
        Assert.Null(harness.Shell.Reading.AttachmentNotice);
    }

    // A11 (fix.2): a part the parser made a message is proposed and opened as .eml, always, and always after a question.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("forwarded_a", "forwarded_a.eml")]
    [InlineData("", "anhang-1.eml")]
    [InlineData("forwarded_b.eml", "forwarded_b.eml")]
    [InlineData("evil.exe", "evil.exe.eml")]
    public async Task An_attached_message_is_proposed_and_opened_as_an_eml_file(string name, string expectedName)
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, new MailAttachmentInfo(name, "message/rfc822", 300));
        AttachmentRow row = Assert.Single(harness.Shell.Reading.Attachments);
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness, "forwarded.eml");
        harness.ConfirmationAnswer = true;

        await harness.Shell.Reading.SaveAttachmentAsync(row);
        await harness.Shell.Reading.OpenAttachmentAsync(row);

        Assert.Equal(expectedName, Assert.Single(harness.AttachmentHost.ProposedNames));
        Assert.Equal(expectedName, Path.GetFileName(Assert.Single(harness.AttachmentHost.OpenedPaths)));
        Assert.Single(harness.ConfirmationTexts);
    }

    // Case 15: answer no.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task An_ask_type_with_the_answer_no_is_not_opened()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ConfirmationAnswer = false;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, AskName));

        Assert.Single(harness.ConfirmationTexts);
        Assert.False(string.IsNullOrWhiteSpace(harness.ConfirmationTexts[0]));
        Assert.DoesNotContain("makro_b", harness.ConfirmationTexts[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docm", harness.ConfirmationTexts[0], StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
        Assert.Empty(FilesInOpenFolder(harness));
    }

    // Case 15: answer yes.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task An_ask_type_with_the_answer_yes_is_opened_once()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ConfirmationAnswer = true;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, AskName));

        Assert.Single(harness.ConfirmationTexts);
        string opened = Assert.Single(harness.AttachmentHost.OpenedPaths);
        Assert.Equal(AskName, Path.GetFileName(opened));
    }

    // Case 15 / A5: the question comes before anything is written or decoded.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Nothing_is_decoded_before_the_question_is_answered()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ConfirmationAnswer = null;

        Task opening = harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, AskName));
        await Wait.UntilAsync(() => harness.ConfirmationTexts.Count == 1, "the question");
        await Wait.NegativeWindowAsync();

        Assert.False(opening.IsCompleted);
        Assert.Empty(harness.ReadModel.CopyCalls);
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
        Assert.Empty(FilesInOpenFolder(harness));
    }

    // Case 16 (A6): harmless types are asked from Junk, Quarantine and Screener.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Junk)]
    [InlineData(FolderRole.Quarantine)]
    [InlineData(FolderRole.Screener)]
    [InlineData(FolderRole.Trash)]
    public async Task A_harmless_type_from_a_risky_folder_asks_first(FolderRole role)
    {
        (ShellHarness harness, _) = await ShowAsync(role, ThreeAttachments());
        harness.ConfirmationAnswer = false;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Single(harness.ConfirmationTexts);
        Assert.False(string.IsNullOrWhiteSpace(harness.ConfirmationTexts[0]));
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
    }

    // Case 16: yes opens exactly once.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Junk)]
    [InlineData(FolderRole.Quarantine)]
    [InlineData(FolderRole.Screener)]
    [InlineData(FolderRole.Trash)]
    public async Task A_harmless_type_from_a_risky_folder_opens_after_yes(FolderRole role)
    {
        (ShellHarness harness, _) = await ShowAsync(role, ThreeAttachments());
        harness.ConfirmationAnswer = true;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Single(harness.ConfirmationTexts);
        Assert.Single(harness.AttachmentHost.OpenedPaths);
    }

    // Case 16 (A10): an unknown folder role counts as Junk.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_harmless_type_from_an_unknown_folder_asks_first()
    {
        (ShellHarness harness, _) = await ShowAsync(role: null, ThreeAttachments());
        harness.ConfirmationAnswer = false;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Single(harness.ConfirmationTexts);
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
    }

    // Case 16 positive control (A6): the role decides, never the folder name.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_folder_named_junk_with_the_role_inbox_asks_nothing()
    {
        var harness = new ShellHarness();
        _harnesses.Add(harness);
        Account account = harness.ReadModel.AddAccount("Account_A");
        MailFolder folder = harness.ReadModel.AddFolder(account, FolderRole.Inbox, "Junk");
        MailMessage message = harness.ReadModel.AddMessage(folder, "named_junk", hasAttachments: true);
        harness.ReadModel.SetContent(message.Id, TestContent.Plain("named_junk", "body", ThreeAttachments()));
        await harness.Shell.Reading.ShowAsync(message);

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Empty(harness.ConfirmationTexts);
        Assert.Single(harness.AttachmentHost.OpenedPaths);
    }

    // Case 17: SaveOnly.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Inbox)]
    [InlineData(FolderRole.Junk)]
    public async Task A_blocked_type_cannot_be_opened_even_when_called_directly(FolderRole role)
    {
        (ShellHarness harness, _) = await ShowAsync(role, ThreeAttachments());
        AttachmentRow row = RowNamed(harness, BlockedName);
        harness.ConfirmationAnswer = true;

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.OpenAttachmentAsync(row));

        Assert.False(row.CanOpen);
        Assert.Null(failure);
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
        Assert.Empty(FilesInOpenFolder(harness));
        Assert.Empty(harness.ReadModel.CopyCalls);
    }

    // Case 17 / A7: the double extension is blocked too.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_double_extension_with_a_blocked_end_cannot_be_opened()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, new MailAttachmentInfo("rechnung.pdf.exe", "application/pdf", 10));
        AttachmentRow row = Assert.Single(harness.Shell.Reading.Attachments);

        await harness.Shell.Reading.OpenAttachmentAsync(row);

        Assert.False(row.CanOpen);
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
    }

    // ---- Failures (case 18, A8) -----------------------------------------------------------------------------

    // Case 18: Windows refused to launch.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_refusing_host_sets_a_notice_without_exception_and_logs_no_file_name()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.AttachmentHost.OpenResult = false;

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName)));

        Assert.Null(failure);
        Assert.Single(harness.AttachmentHost.OpenedPaths);
        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Reading.AttachmentNotice));
        Assert.DoesNotContain("report_a", harness.Log.AllText, StringComparison.Ordinal);
        Assert.Empty(harness.Log.Entries);
        Assert.True(string.IsNullOrWhiteSpace(harness.Shell.Banner));
    }

    // Case 18: decoding fails while opening.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_decoding_failure_while_opening_sets_a_notice_logs_the_type_and_leaves_no_file()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ReadModel.CopyBehavior = (_, _, stream, _) =>
        {
            stream.WriteByte(1);
            throw new MailContentException();
        };

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName)));

        Assert.Null(failure);
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Reading.AttachmentNotice));
        Assert.Contains(nameof(MailContentException), harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("report_a", harness.Log.AllText, StringComparison.Ordinal);
        Assert.Empty(FilesInOpenFolder(harness));
        Assert.True(string.IsNullOrWhiteSpace(harness.Shell.Banner));
    }

    // Case 18 (A8): decoding fails while saving; the empty file of the dialog does not stay under the chosen name.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_decoding_failure_while_saving_sets_a_notice_and_leaves_no_half_file()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        string target = SaveTargetIn(harness);
        harness.AttachmentHost.SaveTarget = target;
        harness.ReadModel.CopyBehavior = (_, _, stream, _) =>
        {
            stream.Write([1, 2, 3]);
            throw new MailContentException();
        };

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName)));

        Assert.Null(failure);
        Assert.False(File.Exists(target), "a half or empty file stays under the chosen name");
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!));
        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Reading.AttachmentNotice));
        Assert.Contains(nameof(MailContentException), harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("report_a", harness.Log.AllText, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(harness.Shell.Banner), "without a change of mail the failure shows as a notice, not in the banner");
    }

    // Case 18 (A8): the mail is gone (no EML any more).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_mail_that_vanished_meanwhile_gives_a_notice()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);
        harness.ReadModel.CopyBehavior = (_, _, _, _) => throw new FileNotFoundException("gone");

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName)));

        Assert.Null(failure);
        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Reading.AttachmentNotice));
        Assert.False(File.Exists(SaveTargetIn(harness)));
    }

    // Case 18: the dialog itself fails.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_failing_dialog_sets_a_notice_without_exception()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.AttachmentHost.PickBehavior = _ => throw new InvalidOperationException("dialog failed");

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName)));

        Assert.Null(failure);
        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Reading.AttachmentNotice));
        Assert.Empty(harness.ReadModel.CopyCalls);
    }

    // Case 18: the notice belongs to the last action; a later successful action clears it.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_later_successful_action_clears_the_old_notice()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.AttachmentHost.OpenResult = false;
        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));
        Assert.NotNull(harness.Shell.Reading.AttachmentNotice);
        harness.AttachmentHost.OpenResult = true;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Null(harness.Shell.Reading.AttachmentNotice);
    }

    // ---- Case 19 -------------------------------------------------------------------------------------------

    private async Task<(ShellHarness Harness, MailMessage Second, TaskCompletionSource Entered, TaskCompletionSource Release)>
        ArrangeSlowCopyAsync()
    {
        (ShellHarness harness, MailMessage first) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        Account account = (await harness.ReadModel.ListAccountsAsync()).Single();
        MailFolder folder = (await harness.ReadModel.ListFoldersAsync(account.Id)).Single();
        MailMessage second = harness.ReadModel.AddMessage(folder, "second_mail", hasAttachments: true);
        harness.ReadModel.SetContent(second.Id, TestContent.Plain("second_mail", "body", [new MailAttachmentInfo("second_d.png", "image/png", 5)]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.CopyBehavior = async (message, _, stream, ct) =>
        {
            if (message.Id != first.Id)
            {
                return;
            }

            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            stream.Write([1, 2, 3]);
        };
        return (harness, second, entered, release);
    }

    // Case 19: the save of the old mail ends; rows and notice belong to the new mail.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Selecting_another_mail_while_saving_keeps_rows_and_notice_of_the_new_mail()
    {
        (ShellHarness harness, MailMessage second, TaskCompletionSource entered, TaskCompletionSource release) = await ArrangeSlowCopyAsync();
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);
        Task saving = harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await harness.Shell.Reading.ShowAsync(second);
        release.SetResult();
        Exception? failure = await Record.ExceptionAsync(() => saving.WaitAsync(TimeSpan.FromSeconds(10)));
        await Wait.NegativeWindowAsync();

        Assert.Null(failure);
        Assert.Equal(new[] { "second_d.png" }, harness.Shell.Reading.Attachments.Select(row => row.Name).ToArray());
        Assert.Null(harness.Shell.Reading.AttachmentNotice);
        Assert.True(string.IsNullOrWhiteSpace(harness.Shell.Banner), "a save that succeeded reports nothing");
    }

    // Case 19 (fix.2): a failure of the old save goes to the banner of the shell, not to the new mail.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_failure_of_the_old_save_shows_in_the_banner_and_not_at_the_new_mail()
    {
        (ShellHarness harness, MailMessage second, TaskCompletionSource entered, TaskCompletionSource release) = await ArrangeSlowCopyAsync();
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);
        Func<MailMessage, int, Stream, CancellationToken, Task> slow = harness.ReadModel.CopyBehavior!;
        harness.ReadModel.CopyBehavior = async (message, index, stream, ct) =>
        {
            await slow(message, index, stream, ct);
            throw new MailContentException();
        };
        Task saving = harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await harness.Shell.Reading.ShowAsync(second);
        release.SetResult();
        Exception? failure = await Record.ExceptionAsync(() => saving.WaitAsync(TimeSpan.FromSeconds(10)));
        await Wait.NegativeWindowAsync();

        Assert.Null(failure);
        Assert.Equal(new[] { "second_d.png" }, harness.Shell.Reading.Attachments.Select(row => row.Name).ToArray());
        Assert.Null(harness.Shell.Reading.AttachmentNotice);
        Assert.False(harness.Shell.Reading.HasAttachmentNotice);
        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Banner), "the failure of the save has no place but the banner");
        Assert.Contains(UiText.BannerAttachmentSaveFailed, harness.Shell.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain("report_a", harness.Shell.Banner, StringComparison.Ordinal);
    }

    // Case 19 (fix.2): the next attachment action clears the banner of the failed save, whichever action it is.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("open")]
    [InlineData("save")]
    [InlineData("cancelled save")]
    public async Task The_next_attachment_action_clears_the_banner_of_the_failed_save(string nextAction)
    {
        (ShellHarness harness, MailMessage second, TaskCompletionSource entered, TaskCompletionSource release) = await ArrangeSlowCopyAsync();
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);
        Func<MailMessage, int, Stream, CancellationToken, Task> slow = harness.ReadModel.CopyBehavior!;
        harness.ReadModel.CopyBehavior = async (message, index, stream, ct) =>
        {
            await slow(message, index, stream, ct);
            throw new MailContentException();
        };
        Task saving = harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Shell.Reading.ShowAsync(second);
        release.SetResult();
        await saving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Banner));
        harness.ReadModel.CopyBehavior = null;
        AttachmentRow next = harness.Shell.Reading.Attachments.Single();
        harness.AttachmentHost.SaveTarget = nextAction == "cancelled save" ? null : SaveTargetIn(harness, "second_d.png");

        if (nextAction == "open")
        {
            await harness.Shell.Reading.OpenAttachmentAsync(next);
        }
        else
        {
            await harness.Shell.Reading.SaveAttachmentAsync(next);
        }

        Assert.True(string.IsNullOrWhiteSpace(harness.Shell.Banner), $"the banner stays after: {harness.Shell.Banner}");
    }

    // Case 19 (decisions after fix.2): a failed opening after a change of mail is only logged, the user is not waiting for a file.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_failed_opening_after_a_change_of_mail_is_logged_and_shows_nothing()
    {
        (ShellHarness harness, MailMessage second, TaskCompletionSource entered, TaskCompletionSource release) = await ArrangeSlowCopyAsync();
        Func<MailMessage, int, Stream, CancellationToken, Task> slow = harness.ReadModel.CopyBehavior!;
        harness.ReadModel.CopyBehavior = async (message, index, stream, ct) =>
        {
            await slow(message, index, stream, ct);
            throw new MailContentException();
        };
        Task opening = harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Shell.Reading.ShowAsync(second);
        release.SetResult();

        Exception? failure = await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        await Wait.NegativeWindowAsync();

        Assert.Null(failure);
        Assert.True(string.IsNullOrWhiteSpace(harness.Shell.Banner));
        Assert.Null(harness.Shell.Reading.AttachmentNotice);
        Assert.Contains(nameof(MailContentException), harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("report_a", harness.Log.AllText, StringComparison.Ordinal);
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
    }

    // Case 19: the same for opening; the old mail's file is not launched at the new mail's expense.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Selecting_another_mail_while_opening_keeps_rows_and_notice_of_the_new_mail()
    {
        (ShellHarness harness, MailMessage second, TaskCompletionSource entered, TaskCompletionSource release) = await ArrangeSlowCopyAsync();
        harness.AttachmentHost.OpenResult = false;
        Task opening = harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await harness.Shell.Reading.ShowAsync(second);
        release.SetResult();
        Exception? failure = await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        await Wait.NegativeWindowAsync();

        Assert.Null(failure);
        Assert.Equal(new[] { "second_d.png" }, harness.Shell.Reading.Attachments.Select(row => row.Name).ToArray());
        Assert.Null(harness.Shell.Reading.AttachmentNotice);
        Assert.True(string.IsNullOrWhiteSpace(harness.Shell.Banner), "a refused opening after a change of mail shows nothing");
    }

    // ---- Case 19a (fix.2): the notice while a copy runs ---------------------------------------------------------

    // Case 19a / A2: "Anhang wird kopiert ..." during the copy of a save, "Anhang gespeichert." afterwards.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_save_shows_the_copy_notice_during_the_copy_and_the_saved_notice_afterwards()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.CopyBehavior = async (_, _, stream, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            stream.Write([1, 2, 3]);
        };
        Assert.Null(harness.Shell.Reading.AttachmentNotice);

        Task saving = harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(UiText.AttachmentCopying, harness.Shell.Reading.AttachmentNotice);
        Assert.Contains("kopiert", harness.Shell.Reading.AttachmentNotice!, StringComparison.Ordinal);
        Assert.True(harness.Shell.Reading.HasAttachmentNotice);

        release.SetResult();
        await saving.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(UiText.AttachmentSaved, harness.Shell.Reading.AttachmentNotice);
        Assert.Contains("gespeichert", harness.Shell.Reading.AttachmentNotice!, StringComparison.Ordinal);
    }

    // Case 19a / A3: the copy notice during the copy of an opening, null after a successful start.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task An_opening_shows_the_copy_notice_during_the_copy_and_no_notice_after_the_start()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.CopyBehavior = async (_, _, stream, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            stream.Write([1, 2, 3]);
        };

        Task opening = harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(UiText.AttachmentCopying, harness.Shell.Reading.AttachmentNotice);

        release.SetResult();
        await opening.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(harness.AttachmentHost.OpenedPaths);
        Assert.Null(harness.Shell.Reading.AttachmentNotice);
        Assert.False(harness.Shell.Reading.HasAttachmentNotice);
    }

    // Case 19a: a cancelled dialog after a saved file clears the old notice, a refusal replaces the copy notice.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_cancelled_save_after_a_saved_one_leaves_no_notice()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);
        await harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));
        Assert.Equal(UiText.AttachmentSaved, harness.Shell.Reading.AttachmentNotice);
        harness.AttachmentHost.SaveTarget = null;

        await harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Null(harness.Shell.Reading.AttachmentNotice);
    }

    // Case 19a: after an answer of no there is no notice either.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task An_answer_of_no_leaves_no_notice()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ConfirmationAnswer = false;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, AskName));

        Assert.Null(harness.Shell.Reading.AttachmentNotice);
    }

    // ---- Case 19b (fix.2): the role is read again before opening --------------------------------------------

    private static async Task<long> OnlyFolderIdAsync(ShellHarness harness)
    {
        Account account = (await harness.ReadModel.ListAccountsAsync()).Single();
        return (await harness.ReadModel.ListFoldersAsync(account.Id)).Single().Id;
    }

    // Case 19b / A6: a role that differs from the one at showing asks first, also for a harmless type and also when the new
    // role asks for none.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Inbox, FolderRole.Archive)]
    [InlineData(FolderRole.Inbox, FolderRole.Sent)]
    [InlineData(FolderRole.Inbox, FolderRole.Junk)]
    [InlineData(FolderRole.Inbox, FolderRole.Trash)]
    [InlineData(FolderRole.Inbox, null)]
    [InlineData(FolderRole.Archive, FolderRole.Inbox)]
    [InlineData(FolderRole.Junk, FolderRole.Inbox)]
    [InlineData(FolderRole.Junk, FolderRole.Quarantine)]
    public async Task A_role_that_changed_before_opening_asks_first(FolderRole shownRole, FolderRole? roleNow)
    {
        (ShellHarness harness, _) = await ShowAsync(shownRole, ThreeAttachments());
        harness.ReadModel.SetFolderRole(await OnlyFolderIdAsync(harness), roleNow);
        harness.ConfirmationAnswer = false;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Single(harness.ConfirmationTexts);
        Assert.False(string.IsNullOrWhiteSpace(harness.ConfirmationTexts[0]));
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
        Assert.Empty(harness.ReadModel.CopyCalls);
    }

    // Case 19b: after yes the file opens, once.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_changed_role_followed_by_yes_opens_the_file_once()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ReadModel.SetFolderRole(await OnlyFolderIdAsync(harness), FolderRole.Archive);
        harness.ConfirmationAnswer = true;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Single(harness.ConfirmationTexts);
        Assert.Single(harness.AttachmentHost.OpenedPaths);
    }

    // Case 19b: the role cannot be read -> a question, also for a harmless type from the inbox.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_role_that_cannot_be_read_asks_first(bool answer)
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ReadModel.FolderRoleFailure = new IOException("role read failed");
        harness.ConfirmationAnswer = answer;

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName)));

        Assert.Null(failure);
        Assert.Single(harness.ConfirmationTexts);
        Assert.Equal(answer ? 1 : 0, harness.AttachmentHost.OpenedPaths.Count);
    }

    // Case 19b positive control: the same role, read again, asks nothing for a harmless type from the inbox.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task An_unchanged_readable_role_asks_nothing_for_a_harmless_type()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ReadModel.SetFolderRole(await OnlyFolderIdAsync(harness), FolderRole.Inbox);

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Empty(harness.ConfirmationTexts);
        Assert.Single(harness.AttachmentHost.OpenedPaths);
    }

    // Case 19b: saving does not depend on the role and does not read it as a reason to ask.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Saving_asks_nothing_even_when_the_role_cannot_be_read()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ReadModel.FolderRoleFailure = new IOException("role read failed");
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);

        await harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Empty(harness.ConfirmationTexts);
        Assert.True(File.Exists(SaveTargetIn(harness)));
    }

    // Case 19b: a blocked type stays blocked whatever the role says.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_blocked_type_stays_blocked_when_the_role_changes()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Junk, ThreeAttachments());
        harness.ReadModel.SetFolderRole(await OnlyFolderIdAsync(harness), FolderRole.Inbox);
        harness.ConfirmationAnswer = true;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, BlockedName));

        Assert.Empty(harness.ConfirmationTexts);
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
    }

    // ---- Case 19c (fix.2): another mail during the question ---------------------------------------------------

    // Case 19c / A6: the row must still belong to the shown mail after the question; a yes after a change of mail opens nothing.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_yes_after_another_mail_was_selected_opens_nothing()
    {
        (ShellHarness harness, MailMessage first) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        Account account = (await harness.ReadModel.ListAccountsAsync()).Single();
        MailFolder folder = (await harness.ReadModel.ListFoldersAsync(account.Id)).Single();
        MailMessage second = harness.ReadModel.AddMessage(folder, "second_mail", hasAttachments: true);
        harness.ReadModel.SetContent(second.Id, TestContent.Plain("second_mail", "body", [new MailAttachmentInfo("second_d.png", "image/png", 5)]));
        harness.ConfirmationAnswer = null;

        Task opening = harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, AskName));
        await Wait.UntilAsync(() => harness.ConfirmationRequests.Count == 1, "the question");
        await harness.Shell.Reading.ShowAsync(second);
        harness.ConfirmationRequests[0].Answer.TrySetResult(true);
        Exception? failure = await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        await Wait.NegativeWindowAsync();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(failure);
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
        Assert.Empty(harness.ReadModel.CopyCalls);
        Assert.Empty(FilesInOpenFolder(harness));
        Assert.Equal(new[] { "second_d.png" }, harness.Shell.Reading.Attachments.Select(row => row.Name).ToArray());
        Assert.Null(harness.Shell.Reading.AttachmentNotice);
        Assert.True(string.IsNullOrWhiteSpace(harness.Shell.Banner));
    }

    // Case 19c positive control: the same flow without a change of mail opens the file.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_yes_for_the_still_shown_mail_opens_the_file()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ConfirmationAnswer = null;

        Task opening = harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, AskName));
        await Wait.UntilAsync(() => harness.ConfirmationRequests.Count == 1, "the question");
        harness.ConfirmationRequests[0].Answer.TrySetResult(true);
        await opening.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(harness.AttachmentHost.OpenedPaths);
    }

    // ---- Case 20 -------------------------------------------------------------------------------------------

    // Case 20 (A12): open and save never queue a command, never touch the controller and never change a flag.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Opening_and_saving_change_no_flag_and_queue_no_command()
    {
        (ShellHarness harness, MailMessage message) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        MessageFlags flagsBefore = harness.ReadModel.Find(message.Id)!.Flags;
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);
        harness.ConfirmationAnswer = true;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));
        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, AskName));
        await harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));
        await harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, BlockedName));
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Actions.Calls);
        Assert.Empty(harness.Controller.Calls);
        Assert.Equal(flagsBefore, harness.ReadModel.Find(message.Id)!.Flags);
        Assert.Equal(MessageFlags.None, harness.ReadModel.Find(message.Id)!.Flags & MessageFlags.Seen);
    }

    // Case 20: also when the actions fail.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failing_actions_change_no_flag_and_queue_no_command_either()
    {
        (ShellHarness harness, MailMessage message) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.AttachmentHost.OpenResult = false;
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);
        harness.ReadModel.CopyBehavior = (_, _, _, _) => throw new MailContentException();

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));
        await harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Empty(harness.Actions.Calls);
        Assert.Empty(harness.Controller.Calls);
        Assert.Equal(MessageFlags.None, harness.ReadModel.Find(message.Id)!.Flags);
    }
}
