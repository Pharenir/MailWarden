using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation.Tests;

/// <summary>
/// AP-04a cases 72, 73 and 74 (L3, L4, L5): the build guard of the reading pane (one build at a time, 5 s limit, plain-text
/// fallback with a notice) and <see cref="ReadingPaneViewModel.ContentNotice"/>.
/// The time limit is driven by a <see cref="ManualTimeProvider"/>; the "slow build" is a large document, so these tests
/// assume the build of the heavy document takes longer than the test needs to register and advance the clock.
/// If the product offers a build seam, replace <see cref="HeavyHtml"/> by a blocked build.
/// </summary>
[Trait("Category", "feature:ui")]
public sealed class ReadingPaneBuildGuardTests
{
    private static readonly string HeavyHtml =
        "<p>heavy_html_marker</p>" + string.Concat(Enumerable.Repeat("<p>filler paragraph <b>x</b></p>", 60_000));

    private static (ShellHarness Harness, ManualTimeProvider Clock, MailFolder Inbox) NewHarness()
    {
        var clock = new ManualTimeProvider(ShellHarness.FixedNow);
        var harness = new ShellHarness(clock: clock);
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        return (harness, clock, inbox);
    }

    private static MailMessage AddHeavy(ShellHarness harness, MailFolder inbox, string subject)
    {
        MailMessage message = harness.ReadModel.AddMessage(inbox, subject);
        harness.ReadModel.SetContent(message.Id, TestContent.Html(subject, HeavyHtml) with { PlainText = "heavy_plain_marker" });
        return message;
    }

    private static MailMessage AddSmall(ShellHarness harness, MailFolder inbox, string subject)
    {
        MailMessage message = harness.ReadModel.AddMessage(inbox, subject);
        harness.ReadModel.SetContent(message.Id, TestContent.Html(subject, "<p>small_html_marker</p>") with { PlainText = "small_plain_marker" });
        return message;
    }

    // ---- Case 72 -------------------------------------------------------------------------------------------

    // Case 72
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Build_over_the_time_limit_shows_the_plain_text_document_with_a_notice()
    {
        (ShellHarness harness, ManualTimeProvider clock, MailFolder inbox) = NewHarness();
        MailMessage message = AddHeavy(harness, inbox, "heavy_subject");
        ReadingPaneViewModel reading = harness.Shell.Reading;

        Task show = reading.ShowAsync(message);
        await Wait.UntilAsync(() => clock.ActiveTimerCount >= 1, "time limit armed");
        clock.Advance(TimeSpan.FromSeconds(6));
        await show;

        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Contains("heavy_plain_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("heavy_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("filler paragraph", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));
        Assert.Equal("heavy_subject", reading.Subject);
    }

    // Case 72: the late result of the timed-out build is ignored.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Late_result_of_a_timed_out_build_is_ignored()
    {
        (ShellHarness harness, ManualTimeProvider clock, MailFolder inbox) = NewHarness();
        MailMessage message = AddHeavy(harness, inbox, "heavy_subject");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Task show = reading.ShowAsync(message);
        await Wait.UntilAsync(() => clock.ActiveTimerCount >= 1, "time limit armed");
        clock.Advance(TimeSpan.FromSeconds(6));
        await show;
        string? documentAtTimeout = reading.DocumentHtml;

        for (int round = 0; round < 8; round++)
        {
            await Wait.NegativeWindowAsync();
        }

        Assert.Equal(documentAtTimeout, reading.DocumentHtml);
        Assert.DoesNotContain("heavy_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));
    }

    // Case 72: while a build runs, another mail gets the plain-text document and the notice at once.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Other_mail_during_a_running_build_gets_the_plain_text_document_with_a_notice()
    {
        (ShellHarness harness, ManualTimeProvider clock, MailFolder inbox) = NewHarness();
        MailMessage heavy = AddHeavy(harness, inbox, "heavy_subject");
        MailMessage small = AddSmall(harness, inbox, "small_subject");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Task first = reading.ShowAsync(heavy);
        await Wait.UntilAsync(() => clock.ActiveTimerCount >= 1, "time limit armed");

        await reading.ShowAsync(small);

        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("small_subject", reading.Subject);
        Assert.Contains("small_plain_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("small_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));

        clock.Advance(TimeSpan.FromSeconds(6));
        await Record.ExceptionAsync(() => first);
        for (int round = 0; round < 6; round++)
        {
            await Wait.NegativeWindowAsync();
        }

        Assert.Equal("small_subject", reading.Subject);
        Assert.DoesNotContain("heavy_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
    }

    // Case 72: selecting the same mail again during a run does not start a second build (no second time limit is armed).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Selecting_the_same_mail_again_during_a_run_starts_no_second_build()
    {
        (ShellHarness harness, ManualTimeProvider clock, MailFolder inbox) = NewHarness();
        MailMessage heavy = AddHeavy(harness, inbox, "heavy_subject");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Task first = reading.ShowAsync(heavy);
        await Wait.UntilAsync(() => clock.ActiveTimerCount >= 1, "time limit armed");

        Task second = reading.ShowAsync(heavy);
        await Wait.NegativeWindowAsync();

        Assert.Equal(1, clock.TimersCreated);
        clock.Advance(TimeSpan.FromSeconds(6));
        await Record.ExceptionAsync(() => Task.WhenAll(first, second));
        Assert.Equal(1, clock.TimersCreated);
    }

    // Case 72 positive control: a normal mail never sees the fallback, whatever the clock does later.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Normal_mail_is_built_normally_and_without_a_notice()
    {
        (ShellHarness harness, ManualTimeProvider clock, MailFolder inbox) = NewHarness();
        MailMessage small = AddSmall(harness, inbox, "small_subject");
        ReadingPaneViewModel reading = harness.Shell.Reading;

        await reading.ShowAsync(small);
        clock.Advance(TimeSpan.FromSeconds(60));
        await Wait.NegativeWindowAsync();

        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Contains("small_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("small_plain_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Null(reading.ContentNotice);
    }

    // Case 72 positive control: after a fallback, the next mail on an idle pane is built normally again.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Next_mail_after_a_finished_fallback_is_built_normally()
    {
        (ShellHarness harness, ManualTimeProvider clock, MailFolder inbox) = NewHarness();
        MailMessage heavy = AddHeavy(harness, inbox, "heavy_subject");
        MailMessage small = AddSmall(harness, inbox, "small_subject");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Task first = reading.ShowAsync(heavy);
        await Wait.UntilAsync(() => clock.ActiveTimerCount >= 1, "time limit armed");
        clock.Advance(TimeSpan.FromSeconds(6));
        await first;
        // The abandoned build may still occupy the single build slot; wait until it is over.
        for (int attempt = 0; attempt < 100 && (reading.ContentNotice is not null || reading.DocumentHtml?.Contains("small_html_marker", StringComparison.Ordinal) != true); attempt++)
        {
            await reading.ShowAsync(small);
            await Task.Delay(100);
        }

        Assert.Contains("small_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Null(reading.ContentNotice);
    }

    // ---- Case 73 (pane part) -------------------------------------------------------------------------------

    // Case 73
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reduced_document_sets_a_notice_and_a_normal_one_clears_it()
    {
        (ShellHarness harness, _, MailFolder inbox) = NewHarness();
        string attributes = string.Concat(Enumerable.Range(0, 300).Select(index => $" a{index}=\"x\""));
        MailMessage reduced = harness.ReadModel.AddMessage(inbox, "reduced_subject");
        harness.ReadModel.SetContent(reduced.Id, TestContent.Html("reduced_subject", $"<p>keep_marker</p><div{attributes}>inside</div>"));
        MailMessage normal = AddSmall(harness, inbox, "normal_subject");
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
        (ShellHarness harness, _, MailFolder inbox) = NewHarness();
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
        (ShellHarness harness, _, MailFolder inbox) = NewHarness();
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
        (ShellHarness harness, _, MailFolder inbox) = NewHarness();
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
        (ShellHarness harness, _, MailFolder inbox) = NewHarness();
        MailMessage opaque = harness.ReadModel.AddMessage(inbox, "opaque_subject");
        harness.ReadModel.SetContent(opaque.Id, TestContent.Html("opaque_subject", "<p>clean_marker</p>") with { HasOpaqueParts = true });
        MailMessage clean = AddSmall(harness, inbox, "clean_subject");
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
        (ShellHarness harness, _, MailFolder inbox) = NewHarness();
        MailMessage opaque = harness.ReadModel.AddMessage(inbox, "opaque_subject");
        harness.ReadModel.SetContent(opaque.Id, TestContent.Plain("opaque_subject", "plain body") with { HasOpaqueParts = true });

        await harness.Shell.Reading.ShowAsync(opaque);

        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Reading.ContentNotice));
    }
}
