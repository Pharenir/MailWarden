using System.ComponentModel;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04a cases 35-37 (D10): reading pane states, stale-result guard and remote-image approval.</summary>
[Trait("Category", "feature:ui")]
public sealed class ReadingPaneViewModelTests
{
    private const string MarkerSecret = "marker_secret_text";

    private const string RemoteImageHtml =
        "<p>keep_marker</p><img src=\"https://tracker.test/a.gif\"><img src=\"https://tracker.test/b.gif\">";

    private static (ShellHarness Harness, MailFolder Inbox) NewHarness()
    {
        var harness = new ShellHarness();
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        return (harness, inbox);
    }

    // Case 36
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Initial_state_is_empty_and_showing_no_message_resets_to_empty()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Assert.Equal(ReadingState.Empty, reading.State);

        await reading.ShowAsync(harness.ReadModel.AddMessage(inbox, "subject_shown"));
        Assert.Equal(ReadingState.Ready, reading.State);
        await reading.ShowAsync(null);

        Assert.Equal(ReadingState.Empty, reading.State);
        Assert.Null(reading.DocumentHtml);
        Assert.False(reading.CanLoadRemoteImages);
        Assert.Equal(0, reading.BlockedRemoteResourceCount);
    }

    // Case 36
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Ready_state_shows_header_fields_attachment_names_and_the_document()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox, "list_subject");
        harness.ReadModel.SetContent(
            message.Id,
            TestContent.Html(
                "content_subject",
                "<p>html_marker</p>",
                [new MailAttachmentInfo("report_a.pdf", "application/pdf", 20), new MailAttachmentInfo("na\u202Eme.txt", "text/plain", null)]));

        await harness.Shell.Reading.ShowAsync(message);

        ReadingPaneViewModel reading = harness.Shell.Reading;
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("content_subject", reading.Subject);
        Assert.Contains("sender_a@example.test", reading.From, StringComparison.Ordinal);
        Assert.Contains("recipient_b@example.test", reading.To, StringComparison.Ordinal);
        Assert.Contains("copy_d@example.test", reading.Cc, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(reading.DateText));
        Assert.Equal(new[] { "report_a.pdf", "name.txt" }, reading.AttachmentNames.ToArray());
        Assert.NotNull(reading.DocumentHtml);
        Assert.Contains("html_marker", reading.DocumentHtml, StringComparison.Ordinal);
    }

    // Case 36: header texts of the mail are cleaned again before display.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Header_texts_are_cleaned_of_bidi_and_control_characters()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        MailContent dirty = TestContent.Plain("Re:\u202E subject\u0007", "text") with
        {
            From = "Se\u202Ender <sender_a@example.test>",
            To = ["Rec\u2066ipient <recipient_b@example.test>"],
        };
        harness.ReadModel.SetContent(message.Id, dirty);

        await harness.Shell.Reading.ShowAsync(message);

        ReadingPaneViewModel reading = harness.Shell.Reading;
        Assert.Equal("Re: subject", reading.Subject);
        Assert.StartsWith("Sender", reading.From, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", reading.From, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2066", reading.To, StringComparison.Ordinal);
    }

    // Case 36
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_mail_is_ready_with_an_encoded_document()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        harness.ReadModel.SetContent(message.Id, TestContent.Plain("plain_subject", "line <script>alert(1)</script>"));

        await harness.Shell.Reading.ShowAsync(message);

        Assert.Equal(ReadingState.Ready, harness.Shell.Reading.State);
        Assert.DoesNotContain("<script>alert(1)</script>", harness.Shell.Reading.DocumentHtml, StringComparison.Ordinal);
        Assert.False(harness.Shell.Reading.CanLoadRemoteImages);
    }

    // Case 36
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Message_without_eml_is_not_downloaded()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox, emlPath: null);

        await harness.Shell.Reading.ShowAsync(message);

        Assert.Equal(ReadingState.NotDownloaded, harness.Shell.Reading.State);
        Assert.Null(harness.Shell.Reading.DocumentHtml);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 36
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Missing_eml_file_reported_by_the_read_model_is_not_downloaded()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        harness.ReadModel.SetContent(message.Id, null);

        await harness.Shell.Reading.ShowAsync(message);

        Assert.Equal(ReadingState.NotDownloaded, harness.Shell.Reading.State);
    }

    // Case 36
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reader_failure_gives_error_state_and_a_log_line_with_the_type_name_but_never_the_message()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox, "subject_of_failing_mail");
        harness.ReadModel.ReadBehavior = (_, _) => throw new InvalidOperationException(MarkerSecret);

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.ShowAsync(message));

        Assert.Null(failure);
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Assert.Equal(ReadingState.Error, reading.State);
        Assert.Null(reading.DocumentHtml);
        foreach (string text in new[] { reading.Subject, reading.From, reading.To, reading.Cc, reading.DateText, string.Join("|", reading.AttachmentNames) })
        {
            Assert.DoesNotContain(MarkerSecret, text, StringComparison.Ordinal);
        }

        Assert.NotEmpty(harness.Log.Entries);
        Assert.Contains("InvalidOperationException", harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerSecret, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("subject_of_failing_mail", harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sender_a@example.test", harness.Log.AllText, StringComparison.Ordinal);
    }

    // Case 36: the error state ends with the next message.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_good_message_after_an_error_is_shown_normally()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage bad = harness.ReadModel.AddMessage(inbox, "bad_subject");
        MailMessage good = harness.ReadModel.AddMessage(inbox, "good_subject");
        harness.ReadModel.ReadBehavior = (message, _) =>
            message.Id == bad.Id
                ? throw new IOException(MarkerSecret)
                : Task.FromResult<MailContent?>(TestContent.Plain("good_subject", "good body"));

        await harness.Shell.Reading.ShowAsync(bad);
        Assert.Equal(ReadingState.Error, harness.Shell.Reading.State);
        await harness.Shell.Reading.ShowAsync(good);

        Assert.Equal(ReadingState.Ready, harness.Shell.Reading.State);
        Assert.Equal("good_subject", harness.Shell.Reading.Subject);
    }

    // Case 36 (planner decision): the caller's cancellation propagates; the state does not become Error, nothing is logged.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cancellation_of_the_callers_token_propagates_and_is_neither_an_error_state_nor_a_log_entry()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        using var cancellation = new CancellationTokenSource();
        harness.ReadModel.ReadBehavior = (_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Shell.Reading.ShowAsync(message, cancellation.Token));

        Assert.NotEqual(ReadingState.Error, harness.Shell.Reading.State);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 35
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Slow_first_message_finishing_after_a_second_one_does_not_overwrite_it()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage first = harness.ReadModel.AddMessage(inbox, "first_message");
        MailMessage second = harness.ReadModel.AddMessage(inbox, "second_message");
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.ReadBehavior = async (message, _) =>
        {
            if (message.Id == first.Id)
            {
                await releaseFirst.Task;
                return TestContent.Html("subject_first", "<p>first_marker</p>");
            }

            return TestContent.Html("subject_second", "<p>second_marker</p>");
        };
        ReadingPaneViewModel reading = harness.Shell.Reading;

        Task firstShow = reading.ShowAsync(first);
        await Wait.UntilAsync(() => harness.ReadModel.ReadCalls >= 1, "first read started");
        await reading.ShowAsync(second);
        Assert.Equal("subject_second", reading.Subject);

        releaseFirst.SetResult();
        Exception? failure = await Record.ExceptionAsync(() => firstShow);
        await Wait.NegativeWindowAsync();

        Assert.True(failure is null or OperationCanceledException, $"unexpected {failure}");
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("subject_second", reading.Subject);
        Assert.Contains("second_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("first_marker", reading.DocumentHtml, StringComparison.Ordinal);
    }

    // Case 35: showing "nothing" while a slow read is pending also wins over the late result.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Late_result_does_not_overwrite_a_later_empty_state()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.ReadBehavior = async (_, _) =>
        {
            await release.Task;
            return TestContent.Html("late_subject", "<p>late_marker</p>");
        };
        ReadingPaneViewModel reading = harness.Shell.Reading;

        Task pending = reading.ShowAsync(message);
        await Wait.UntilAsync(() => harness.ReadModel.ReadCalls >= 1, "read started");
        await reading.ShowAsync(null);
        release.SetResult();
        await Record.ExceptionAsync(() => pending);
        await Wait.NegativeWindowAsync();

        Assert.Equal(ReadingState.Empty, reading.State);
        Assert.Null(reading.DocumentHtml);
    }

    // Case 37
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Blocked_remote_images_are_counted_and_offered()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        harness.ReadModel.SetContent(message.Id, TestContent.Html("newsletter", RemoteImageHtml));

        await harness.Shell.Reading.ShowAsync(message);

        ReadingPaneViewModel reading = harness.Shell.Reading;
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal(2, reading.BlockedRemoteResourceCount);
        Assert.True(reading.CanLoadRemoteImages);
        Assert.True(reading.LoadRemoteImagesCommand.CanExecute(null));
        Assert.DoesNotContain("tracker.test", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Contains("keep_marker", reading.DocumentHtml, StringComparison.Ordinal);
    }

    // Case 37
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Load_remote_images_rebuilds_the_document_without_reading_the_eml_again()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        harness.ReadModel.SetContent(message.Id, TestContent.Html("newsletter", RemoteImageHtml));
        await harness.Shell.Reading.ShowAsync(message);
        ReadingPaneViewModel reading = harness.Shell.Reading;
        List<string?> changed = [];
        ((INotifyPropertyChanged)reading).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        reading.LoadRemoteImagesCommand.Execute(null);
        await Wait.UntilAsync(() => !reading.CanLoadRemoteImages, "remote images approved");

        Assert.Contains("tracker.test", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Contains("keep_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Equal(0, reading.BlockedRemoteResourceCount);
        Assert.Equal(1, harness.ReadModel.ReadCalls);
        Assert.Contains(nameof(ReadingPaneViewModel.DocumentHtml), changed);
        Assert.Contains(nameof(ReadingPaneViewModel.CanLoadRemoteImages), changed);
    }

    // Case 37: the approval holds only for the current message.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Approval_is_reset_by_the_next_message()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage first = harness.ReadModel.AddMessage(inbox, "newsletter_one");
        MailMessage second = harness.ReadModel.AddMessage(inbox, "newsletter_two");
        harness.ReadModel.SetContent(first.Id, TestContent.Html("newsletter_one", RemoteImageHtml));
        harness.ReadModel.SetContent(second.Id, TestContent.Html("newsletter_two", RemoteImageHtml));
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(first);
        reading.LoadRemoteImagesCommand.Execute(null);
        await Wait.UntilAsync(() => !reading.CanLoadRemoteImages, "remote images approved");

        await reading.ShowAsync(second);

        Assert.Equal(2, harness.ReadModel.ReadCalls);
        Assert.True(reading.CanLoadRemoteImages);
        Assert.Equal(2, reading.BlockedRemoteResourceCount);
        Assert.DoesNotContain("tracker.test", reading.DocumentHtml, StringComparison.Ordinal);
    }

    // Case 37: no remote images, nothing to offer.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mail_without_remote_images_offers_nothing()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        harness.ReadModel.SetContent(message.Id, TestContent.Html("clean", "<p>clean_marker</p>"));

        await harness.Shell.Reading.ShowAsync(message);

        Assert.False(harness.Shell.Reading.CanLoadRemoteImages);
        Assert.Equal(0, harness.Shell.Reading.BlockedRemoteResourceCount);
    }

    // ---- Case 54 (K7) --------------------------------------------------------------------------------------

    private static MailContent WithAttachmentCount(int count) =>
        TestContent.Plain(
            "attachment_subject",
            "text",
            [.. Enumerable.Range(0, count).Select(index => new MailAttachmentInfo($"file_{index}.bin", "application/octet-stream", 1))]);

    // Case 54
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Huge_header_texts_are_shortened()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        MailContent huge = TestContent.Plain(new string('s', 365 * 1024), "text") with
        {
            From = "N" + new string(' ', 5_000) + "M" + new string('x', 5_000) + " <sender_a@example.test>",
            To = [.. Enumerable.Range(0, 5_000).Select(index => $"Recipient {index} <recipient_{index}@example.test>")],
            Cc = [.. Enumerable.Range(0, 5_000).Select(index => $"Copy {index} <copy_{index}@example.test>")],
        };
        harness.ReadModel.SetContent(message.Id, huge);

        await harness.Shell.Reading.ShowAsync(message);

        ReadingPaneViewModel reading = harness.Shell.Reading;
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.True(reading.Subject.Length <= 501, $"subject has {reading.Subject.Length} characters");
        Assert.True(reading.From.Length < 300, $"from has {reading.From.Length} characters");
        Assert.Contains("sender_a@example.test", reading.From, StringComparison.Ordinal);
        Assert.StartsWith("N M", reading.From, StringComparison.Ordinal);
        Assert.True(reading.To.Length <= 2_001, $"to has {reading.To.Length} characters");
        Assert.True(reading.Cc.Length <= 2_001, $"cc has {reading.Cc.Length} characters");
        Assert.Contains("recipient_0@example.test", reading.To, StringComparison.Ordinal);
        Assert.Contains("copy_0@example.test", reading.Cc, StringComparison.Ordinal);
    }

    // Case 54
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task One_hundred_thousand_attachments_give_fifty_names_and_one_summary_entry()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        harness.ReadModel.SetContent(message.Id, WithAttachmentCount(100_000));

        await harness.Shell.Reading.ShowAsync(message);

        string[] names = [.. harness.Shell.Reading.AttachmentNames];
        Assert.Equal(51, names.Length);
        Assert.Equal(Enumerable.Range(0, 50).Select(index => $"file_{index}.bin").ToArray(), names.Take(50).ToArray());
        Assert.Contains("99950", names[50], StringComparison.Ordinal);
        Assert.Contains("weitere", names[50], StringComparison.Ordinal);
    }

    // Case 54 positive control: 50 attachments are all listed, 51 give one summary entry for the last one.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(50, 50)]
    [InlineData(51, 51)]
    public async Task Attachment_list_is_cut_after_fifty_names(int attachmentCount, int expectedEntries)
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        harness.ReadModel.SetContent(message.Id, WithAttachmentCount(attachmentCount));

        await harness.Shell.Reading.ShowAsync(message);

        string[] names = [.. harness.Shell.Reading.AttachmentNames];
        Assert.Equal(expectedEntries, names.Length);
        if (attachmentCount <= 50)
        {
            Assert.Equal(Enumerable.Range(0, attachmentCount).Select(index => $"file_{index}.bin").ToArray(), names);
        }
        else
        {
            Assert.Contains("1 weitere", names[50], StringComparison.Ordinal);
        }
    }

    // ---- Case 64 (K6) --------------------------------------------------------------------------------------

    // Case 64: the rebuild of an old message (large document) finishes after the next message was shown.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Slow_remote_image_rebuild_does_not_overwrite_the_next_message()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage first = harness.ReadModel.AddMessage(inbox, "newsletter_one");
        MailMessage second = harness.ReadModel.AddMessage(inbox, "newsletter_two");
        string bigBody = string.Concat(Enumerable.Repeat("<p>filler paragraph first_marker</p>", 60_000));
        harness.ReadModel.SetContent(first.Id, TestContent.Html("newsletter_one", bigBody + RemoteImageHtml));
        harness.ReadModel.SetContent(second.Id, TestContent.Html("newsletter_two", "<p>second_marker</p><img src=\"https://tracker.test/c.gif\">"));
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(first);

        reading.LoadRemoteImagesCommand.Execute(null);
        await reading.ShowAsync(second);
        await Wait.NegativeWindowAsync();
        await Wait.NegativeWindowAsync();
        await Wait.NegativeWindowAsync();

        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("newsletter_two", reading.Subject);
        Assert.Contains("second_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("first_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker.test", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.True(reading.CanLoadRemoteImages, "the approval of the old message must not carry over");
        Assert.Equal(1, reading.BlockedRemoteResourceCount);
    }

    // Case 64: showing nothing while a rebuild runs also wins.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Slow_remote_image_rebuild_does_not_overwrite_an_empty_pane()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        string bigBody = string.Concat(Enumerable.Repeat("<p>filler paragraph first_marker</p>", 60_000));
        harness.ReadModel.SetContent(message.Id, TestContent.Html("newsletter", bigBody + RemoteImageHtml));
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(message);

        reading.LoadRemoteImagesCommand.Execute(null);
        await reading.ShowAsync(null);
        await Wait.NegativeWindowAsync();
        await Wait.NegativeWindowAsync();
        await Wait.NegativeWindowAsync();

        Assert.Equal(ReadingState.Empty, reading.State);
        Assert.Null(reading.DocumentHtml);
        Assert.False(reading.CanLoadRemoteImages);
    }

    // Case 64 positive control: without a newer message the slow rebuild does arrive.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Slow_remote_image_rebuild_arrives_when_nothing_newer_was_shown()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        string bigBody = string.Concat(Enumerable.Repeat("<p>filler paragraph first_marker</p>", 60_000));
        harness.ReadModel.SetContent(message.Id, TestContent.Html("newsletter", bigBody + RemoteImageHtml));
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(message);

        reading.LoadRemoteImagesCommand.Execute(null);
        await Wait.UntilAsync(() => !reading.CanLoadRemoteImages, "rebuild finished");

        Assert.Contains("tracker.test", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Equal(1, harness.ReadModel.ReadCalls);
    }

    // ---- Case 65 (K5) --------------------------------------------------------------------------------------

    // Case 65
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Out_of_memory_in_the_reader_ends_in_error_never_in_loading_and_logs_the_type_name()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox, "subject_of_big_mail");
        harness.ReadModel.ReadBehavior = (_, _) => throw new OutOfMemoryException(MarkerSecret);

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.ShowAsync(message));

        Assert.Null(failure);
        Assert.Equal(ReadingState.Error, harness.Shell.Reading.State);
        Assert.NotEqual(ReadingState.Loading, harness.Shell.Reading.State);
        Assert.Null(harness.Shell.Reading.DocumentHtml);
        Assert.Contains("OutOfMemoryException", harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerSecret, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("subject_of_big_mail", harness.Log.AllText, StringComparison.Ordinal);
    }

    // Case 65: the same when the failure happens while the document is built (a hostile document).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Out_of_memory_after_a_slow_read_still_ends_in_error()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        harness.ReadModel.ReadBehavior = async (_, _) =>
        {
            await Task.Delay(50);
            throw new OutOfMemoryException();
        };

        await harness.Shell.Reading.ShowAsync(message);

        Assert.Equal(ReadingState.Error, harness.Shell.Reading.State);
        Assert.Contains("OutOfMemoryException", harness.Log.AllText, StringComparison.Ordinal);
    }

    // Case 65 positive control: the next message after an out-of-memory error is shown normally.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Next_message_after_an_out_of_memory_error_is_shown()
    {
        (ShellHarness harness, MailFolder inbox) = NewHarness();
        MailMessage bad = harness.ReadModel.AddMessage(inbox, "bad_subject");
        MailMessage good = harness.ReadModel.AddMessage(inbox, "good_subject");
        harness.ReadModel.ReadBehavior = (message, _) =>
            message.Id == bad.Id
                ? throw new OutOfMemoryException()
                : Task.FromResult<MailContent?>(TestContent.Plain("good_subject", "good body"));

        await harness.Shell.Reading.ShowAsync(bad);
        await harness.Shell.Reading.ShowAsync(good);

        Assert.Equal(ReadingState.Ready, harness.Shell.Reading.State);
        Assert.Equal("good_subject", harness.Shell.Reading.Subject);
    }}
