using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation.Tests;

/// <summary>
/// AP-04a cases 72, 73 and 74 (L3, L4, L5): the build guard of the reading pane (one build at a time, time limit,
/// plain-text fallback with a notice) and <see cref="ReadingPaneViewModel.ContentNotice"/>.
/// The build is replaced through the <c>documentBuilder</c> seam of <see cref="ShellViewModel"/> by one that blocks on a
/// gate for chosen subjects; the time limit is driven by a <see cref="ManualTimeProvider"/>.
/// </summary>
[Trait("Category", "feature:ui")]
public sealed class ReadingPaneBuildGuardTests
{
    /// <summary>Document builder that records its calls and blocks (on a pool thread) for the subjects it was told to.</summary>
    private sealed class BlockingBuilder
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ManualResetEventSlim> _blocks = [];
        private readonly List<string> _calls = [];
        private readonly List<string> _entered = [];

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public bool HasEntered(string subject)
        {
            lock (_gate)
            {
                return _entered.Contains(subject);
            }
        }

        public void BlockSubject(string subject)
        {
            lock (_gate)
            {
                _blocks[subject] = new ManualResetEventSlim(initialState: false);
            }
        }

        public void Release(string subject)
        {
            lock (_gate)
            {
                _blocks[subject].Set();
            }
        }

        public ReadingDocument Build(MailContent content, bool allowRemoteImages)
        {
            ManualResetEventSlim? block;
            lock (_gate)
            {
                _calls.Add(content.Subject);
                _entered.Add(content.Subject);
                _blocks.TryGetValue(content.Subject, out block);
            }

            if (block is not null && !block.Wait(TimeSpan.FromSeconds(25)))
            {
                throw new TimeoutException("test gate was never released");
            }

            return ReadingDocumentBuilder.Build(content, allowRemoteImages);
        }
    }

    private static (ShellHarness Harness, ManualTimeProvider Clock, BlockingBuilder Builder, MailFolder Inbox) NewHarness()
    {
        var clock = new ManualTimeProvider(ShellHarness.FixedNow);
        var builder = new BlockingBuilder();
        var harness = new ShellHarness(clock: clock, documentBuilder: builder.Build);
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        return (harness, clock, builder, inbox);
    }

    private static MailMessage AddMail(ShellHarness harness, MailFolder inbox, string subject)
    {
        MailMessage message = harness.ReadModel.AddMessage(inbox, subject);
        harness.ReadModel.SetContent(
            message.Id,
            TestContent.Html(subject, $"<p>{subject}_html_marker</p>") with { PlainText = $"{subject}_plain_marker" });
        return message;
    }

    // ---- Case 72 -------------------------------------------------------------------------------------------

    // Case 72
    [Fact]
    public void Build_time_limit_is_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ReadingPaneViewModel.BuildTimeLimit);
    }

    // Case 72 positive control: without any blocking the document is built by the builder, normally and without a notice.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Normal_build_uses_the_builder_and_shows_the_html_document_without_a_notice()
    {
        (ShellHarness harness, ManualTimeProvider clock, BlockingBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddMail(harness, inbox, "mail_a");
        ReadingPaneViewModel reading = harness.Shell.Reading;

        await reading.ShowAsync(mail);
        clock.Advance(TimeSpan.FromSeconds(60));
        await Wait.NegativeWindowAsync();

        Assert.Equal(new[] { "mail_a" }, builder.Calls.ToArray());
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Contains("mail_a_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("mail_a_plain_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Null(reading.ContentNotice);
        Assert.False(reading.HasContentNotice);
    }

    // Case 72: a build over the time limit gives the plain-text document and the notice.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Build_over_the_time_limit_shows_the_plain_text_document_with_a_notice()
    {
        (ShellHarness harness, ManualTimeProvider clock, BlockingBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddMail(harness, inbox, "mail_a");
        builder.BlockSubject("mail_a");
        ReadingPaneViewModel reading = harness.Shell.Reading;

        Task show = reading.ShowAsync(mail);
        await Wait.UntilAsync(() => builder.HasEntered("mail_a") && clock.ActiveTimerCount >= 1, "build running and time limit armed");
        clock.Advance(ReadingPaneViewModel.BuildTimeLimit + TimeSpan.FromSeconds(1));
        await show;

        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("mail_a", reading.Subject);
        Assert.Contains("mail_a_plain_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("mail_a_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));
        Assert.True(reading.HasContentNotice);

        builder.Release("mail_a");
    }

    // Case 72: the late result of the timed-out build is ignored.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Late_result_of_a_timed_out_build_is_ignored()
    {
        (ShellHarness harness, ManualTimeProvider clock, BlockingBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddMail(harness, inbox, "mail_a");
        builder.BlockSubject("mail_a");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Task show = reading.ShowAsync(mail);
        await Wait.UntilAsync(() => builder.HasEntered("mail_a") && clock.ActiveTimerCount >= 1, "build running and time limit armed");
        clock.Advance(ReadingPaneViewModel.BuildTimeLimit + TimeSpan.FromSeconds(1));
        await show;
        string? documentAtTimeout = reading.DocumentHtml;

        builder.Release("mail_a");
        for (int round = 0; round < 4; round++)
        {
            await Wait.NegativeWindowAsync();
        }

        Assert.Equal(documentAtTimeout, reading.DocumentHtml);
        Assert.DoesNotContain("mail_a_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));
    }

    // Case 72: after the abandoned build is over, the next mail is built normally again.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Next_mail_after_a_finished_fallback_is_built_normally()
    {
        (ShellHarness harness, ManualTimeProvider clock, BlockingBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage slow = AddMail(harness, inbox, "mail_slow");
        MailMessage next = AddMail(harness, inbox, "mail_next");
        builder.BlockSubject("mail_slow");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Task show = reading.ShowAsync(slow);
        await Wait.UntilAsync(() => builder.HasEntered("mail_slow") && clock.ActiveTimerCount >= 1, "build running and time limit armed");
        clock.Advance(ReadingPaneViewModel.BuildTimeLimit + TimeSpan.FromSeconds(1));
        await show;
        builder.Release("mail_slow");
        await Wait.NegativeWindowAsync();

        await reading.ShowAsync(next);

        Assert.Equal("mail_next", reading.Subject);
        Assert.Contains("mail_next_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Null(reading.ContentNotice);
    }

    // Case 72 (planner, L3): another mail during a running build waits, shows Loading, and is built normally when the
    // running build ends in time.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Other_mail_waits_for_the_running_build_and_is_then_built_normally()
    {
        (ShellHarness harness, ManualTimeProvider clock, BlockingBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage first = AddMail(harness, inbox, "mail_first");
        MailMessage second = AddMail(harness, inbox, "mail_second");
        builder.BlockSubject("mail_first");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Task firstShow = reading.ShowAsync(first);
        await Wait.UntilAsync(() => builder.HasEntered("mail_first") && clock.ActiveTimerCount >= 1, "first build running");

        Task secondShow = reading.ShowAsync(second);
        await Wait.UntilAsync(() => reading.State == ReadingState.Loading, "the pane shows Loading while it waits");
        Assert.False(secondShow.IsCompleted);
        builder.Release("mail_first");
        await secondShow;
        await Record.ExceptionAsync(() => firstShow);

        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("mail_second", reading.Subject);
        Assert.Contains("mail_second_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("mail_first_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Null(reading.ContentNotice);
        Assert.Equal(new[] { "mail_first", "mail_second" }, builder.Calls.ToArray());
    }

    // Case 72 (planner, L3): if the running build does not end within the limit, the waiting mail gets the text version.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Other_mail_gets_the_plain_text_document_when_the_running_build_does_not_end_in_time()
    {
        (ShellHarness harness, ManualTimeProvider clock, BlockingBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage first = AddMail(harness, inbox, "mail_first");
        MailMessage second = AddMail(harness, inbox, "mail_second");
        builder.BlockSubject("mail_first");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Task firstShow = reading.ShowAsync(first);
        await Wait.UntilAsync(() => builder.HasEntered("mail_first") && clock.ActiveTimerCount >= 1, "first build running");
        Task secondShow = reading.ShowAsync(second);
        await Wait.UntilAsync(() => reading.State == ReadingState.Loading, "the pane shows Loading while it waits");

        clock.Advance(ReadingPaneViewModel.BuildTimeLimit + TimeSpan.FromSeconds(1));
        await secondShow;
        await Record.ExceptionAsync(() => firstShow);

        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("mail_second", reading.Subject);
        Assert.Contains("mail_second_plain_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("mail_second_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));

        builder.Release("mail_first");
        for (int round = 0; round < 4; round++)
        {
            await Wait.NegativeWindowAsync();
        }

        Assert.Equal("mail_second", reading.Subject);
        Assert.DoesNotContain("mail_first_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
    }

    // Case 72 (planner, L3): intermediate selections build nothing; only the mail that is current when the slot is free.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Intermediate_selections_during_a_running_build_are_never_built()
    {
        (ShellHarness harness, ManualTimeProvider clock, BlockingBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage first = AddMail(harness, inbox, "mail_first");
        MailMessage skipped = AddMail(harness, inbox, "mail_skipped");
        MailMessage last = AddMail(harness, inbox, "mail_last");
        builder.BlockSubject("mail_first");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Task firstShow = reading.ShowAsync(first);
        await Wait.UntilAsync(() => builder.HasEntered("mail_first") && clock.ActiveTimerCount >= 1, "first build running");
        Task skippedShow = reading.ShowAsync(skipped);
        await Wait.UntilAsync(() => reading.State == ReadingState.Loading, "waiting");
        Task lastShow = reading.ShowAsync(last);

        builder.Release("mail_first");
        await Record.ExceptionAsync(() => Task.WhenAll(firstShow, skippedShow, lastShow));
        await Wait.NegativeWindowAsync();

        Assert.Equal("mail_last", reading.Subject);
        Assert.Contains("mail_last_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("mail_skipped", builder.Calls);
        Assert.Equal(new[] { "mail_first", "mail_last" }, builder.Calls.ToArray());
    }

    // Case 72 (planner, L3): the same mail again during its own build starts no second build.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Selecting_the_same_mail_again_during_its_own_build_starts_no_second_build()
    {
        (ShellHarness harness, ManualTimeProvider clock, BlockingBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddMail(harness, inbox, "mail_a");
        builder.BlockSubject("mail_a");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Task first = reading.ShowAsync(mail);
        await Wait.UntilAsync(() => builder.HasEntered("mail_a") && clock.ActiveTimerCount >= 1, "build running and time limit armed");

        Task second = reading.ShowAsync(mail);
        await Wait.NegativeWindowAsync();
        builder.Release("mail_a");
        await Record.ExceptionAsync(() => Task.WhenAll(first, second));
        await Wait.NegativeWindowAsync();

        Assert.Equal(new[] { "mail_a" }, builder.Calls.ToArray());
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Contains("mail_a_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
    }

    // Case 72 (planner, L3): at most one build at a time, whatever is selected.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Never_two_builds_run_at_the_same_time()
    {
        (ShellHarness harness, ManualTimeProvider clock, BlockingBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage first = AddMail(harness, inbox, "mail_first");
        MailMessage second = AddMail(harness, inbox, "mail_second");
        builder.BlockSubject("mail_first");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Task firstShow = reading.ShowAsync(first);
        await Wait.UntilAsync(() => builder.HasEntered("mail_first") && clock.ActiveTimerCount >= 1, "first build running");

        Task secondShow = reading.ShowAsync(second);
        await Wait.NegativeWindowAsync();

        Assert.False(builder.HasEntered("mail_second"), "second build started while the first one still runs");
        builder.Release("mail_first");
        await Record.ExceptionAsync(() => Task.WhenAll(firstShow, secondShow));
    }
    // ---- Case 73 (pane part) -------------------------------------------------------------------------------

    // Case 73
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reduced_document_sets_a_notice_and_a_normal_one_clears_it()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        string attributes = string.Concat(Enumerable.Range(0, 300).Select(index => $" a{index}=\"x\""));
        MailMessage reduced = harness.ReadModel.AddMessage(inbox, "reduced_subject");
        harness.ReadModel.SetContent(reduced.Id, TestContent.Html("reduced_subject", $"<p>keep_marker</p><div{attributes}>inside</div>"));
        MailMessage normal = AddMail(harness, inbox, "normal_subject");
        ReadingPaneViewModel reading = harness.Shell.Reading;

        await reading.ShowAsync(reduced);
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));
        Assert.Contains("keep_marker", reading.DocumentHtml, StringComparison.Ordinal);

        await reading.ShowAsync(normal);
        Assert.Null(reading.ContentNotice);

        await reading.ShowAsync(reduced);
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));
        await reading.ShowAsync(null);
        Assert.Null(reading.ContentNotice);
    }

    // Case 73: hitting the image budget sets the notice too.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Image_budget_hit_sets_a_notice()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        string dataUri = "data:image/png;base64," + Convert.ToBase64String(new byte[1_048_576]);
        MailMessage message = harness.ReadModel.AddMessage(inbox, "images_subject");
        harness.ReadModel.SetContent(
            message.Id,
            TestContent.Html("images_subject", string.Concat(Enumerable.Repeat("<img src=\"cid:big@example.test\">", 60))) with
            {
                InlineImages = new Dictionary<string, string> { ["big@example.test"] = dataUri },
            });

        await harness.Shell.Reading.ShowAsync(message);

        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Reading.ContentNotice));
    }

    // Case 73: plain-text mails and failures have no notice.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_mail_and_error_states_have_no_notice()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        MailMessage plain = harness.ReadModel.AddMessage(inbox, "plain_subject");
        harness.ReadModel.SetContent(plain.Id, TestContent.Plain("plain_subject", "plain body"));
        MailMessage missing = harness.ReadModel.AddMessage(inbox, emlPath: null);
        ReadingPaneViewModel reading = harness.Shell.Reading;

        await reading.ShowAsync(plain);
        Assert.Null(reading.ContentNotice);
        await reading.ShowAsync(missing);
        Assert.Equal(ReadingState.NotDownloaded, reading.State);
        Assert.Null(reading.ContentNotice);
    }

    // Case 73: "Bilder laden" on a reduced document keeps the notice (the reduction does not go away).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Loading_remote_images_of_a_reduced_document_keeps_the_notice()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        string attributes = string.Concat(Enumerable.Range(0, 300).Select(index => $" a{index}=\"x\""));
        MailMessage message = harness.ReadModel.AddMessage(inbox, "reduced_subject");
        harness.ReadModel.SetContent(
            message.Id,
            TestContent.Html("reduced_subject", $"<img src=\"https://tracker.test/a.gif\"><div{attributes}>inside</div>"));
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(message);
        Assert.True(reading.CanLoadRemoteImages);

        reading.LoadRemoteImagesCommand.Execute(null);
        await Wait.UntilAsync(() => reading.DocumentHtml?.Contains("tracker.test", StringComparison.Ordinal) == true, "document rebuilt");

        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));
    }

    // ---- Case 74 (pane part) -------------------------------------------------------------------------------

    // Case 74
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Opaque_parts_set_a_notice_even_for_a_clean_document()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        MailMessage opaque = harness.ReadModel.AddMessage(inbox, "opaque_subject");
        harness.ReadModel.SetContent(opaque.Id, TestContent.Html("opaque_subject", "<p>clean_marker</p>") with { HasOpaqueParts = true });
        MailMessage clean = AddMail(harness, inbox, "clean_subject");
        ReadingPaneViewModel reading = harness.Shell.Reading;

        await reading.ShowAsync(opaque);
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));
        Assert.Contains("clean_marker", reading.DocumentHtml, StringComparison.Ordinal);

        await reading.ShowAsync(clean);
        Assert.Null(reading.ContentNotice);
    }

    // Case 74: the same for a plain-text mail with an opaque part.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Opaque_parts_set_a_notice_for_plain_text_mails_too()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        MailMessage opaque = harness.ReadModel.AddMessage(inbox, "opaque_subject");
        harness.ReadModel.SetContent(opaque.Id, TestContent.Plain("opaque_subject", "plain body") with { HasOpaqueParts = true });

        await harness.Shell.Reading.ShowAsync(opaque);

        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Reading.ContentNotice));
    }
}
