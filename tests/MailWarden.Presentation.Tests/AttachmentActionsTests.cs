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
    public async Task Saving_from_a_risky_folder_asks_nothing(FolderRole role)
    {
        (ShellHarness harness, _) = await ShowAsync(role, ThreeAttachments());
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);

        await harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Empty(harness.ConfirmationTexts);
        Assert.True(File.Exists(SaveTargetIn(harness)));
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

    // Case 15: answer no.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task An_ask_type_with_the_answer_no_is_not_opened()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ConfirmationAnswer = false;

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, AskName));

        Assert.Single(harness.ConfirmationTexts);
        Assert.False(string.IsNullOrWhiteSpace(harness.ConfirmationTexts[0]));
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
    }

    // Case 18: decoding fails while opening.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_decoding_failure_while_opening_sets_a_notice_logs_the_type_and_leaves_no_file()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, ThreeAttachments());
        harness.ReadModel.CopyBehavior = (_, _, stream, _) =>
        {
            stream.WriteByte(1);
            throw new MailContentException("decoding failed");
        };

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName)));

        Assert.Null(failure);
        Assert.Empty(harness.AttachmentHost.OpenedPaths);
        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Reading.AttachmentNotice));
        Assert.Contains(nameof(MailContentException), harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("report_a", harness.Log.AllText, StringComparison.Ordinal);
        Assert.Empty(FilesInOpenFolder(harness));
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
            throw new MailContentException("decoding failed");
        };

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName)));

        Assert.Null(failure);
        Assert.False(File.Exists(target), "a half or empty file stays under the chosen name");
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!));
        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Reading.AttachmentNotice));
        Assert.Contains(nameof(MailContentException), harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("report_a", harness.Log.AllText, StringComparison.Ordinal);
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
    }

    // Case 19: a failure of the old save is not shown at the new mail.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_failure_of_the_old_save_shows_no_notice_at_the_new_mail()
    {
        (ShellHarness harness, MailMessage second, TaskCompletionSource entered, TaskCompletionSource release) = await ArrangeSlowCopyAsync();
        harness.AttachmentHost.SaveTarget = SaveTargetIn(harness);
        Func<MailMessage, int, Stream, CancellationToken, Task> slow = harness.ReadModel.CopyBehavior!;
        harness.ReadModel.CopyBehavior = async (message, index, stream, ct) =>
        {
            await slow(message, index, stream, ct);
            throw new MailContentException("decoding failed");
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
        harness.ReadModel.CopyBehavior = (_, _, _, _) => throw new MailContentException("decoding failed");

        await harness.Shell.Reading.OpenAttachmentAsync(RowNamed(harness, HarmlessName));
        await harness.Shell.Reading.SaveAttachmentAsync(RowNamed(harness, HarmlessName));

        Assert.Empty(harness.Actions.Calls);
        Assert.Empty(harness.Controller.Calls);
        Assert.Equal(MessageFlags.None, harness.ReadModel.Find(message.Id)!.Flags);
    }
}
