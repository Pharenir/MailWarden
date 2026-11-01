using System.ComponentModel;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation.Tests;

/// <summary>
/// AP-04d cases 10 and 11 (D1, D7): <see cref="ReadingPaneViewModel.ApplyThemeAsync"/>. The document builder is replaced
/// through the <c>documentBuilder</c> seam of <see cref="ShellViewModel"/> by one that records every call, wraps the real
/// <see cref="ReadingDocumentBuilder"/> and can hold back one chosen call on a gate.
/// </summary>
[Trait("Category", "feature:ui")]
public sealed class ReadingPaneThemeTests
{
    private const string LightMarker = "html,body{background:#ffffff;color:#1a1a1a;}";
    private const string DarkSchemeMarker = "color-scheme:dark";
    private const string RemoteImageHtml = "<p>keep_marker</p><img src=\"https://tracker.test/a.gif\">";

    private sealed record BuildCall(string Subject, bool AllowRemoteImages, bool AllowEmbeddedImages, ReadingTheme Theme);

    private sealed class ThemeBuilder
    {
        private readonly object _gate = new();
        private readonly List<BuildCall> _calls = [];
        private Func<BuildCall, bool>? _holdWhen;
        private ManualResetEventSlim? _hold;
        private bool _holdEntered;

        /// <summary>When set, every build throws it (a failing builder).</summary>
        public Exception? FailWith { get; set; }

        public IReadOnlyList<BuildCall> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public bool HoldEntered
        {
            get
            {
                lock (_gate)
                {
                    return _holdEntered;
                }
            }
        }

        /// <summary>Holds back the next call that matches <paramref name="when"/> until <see cref="Release"/>.</summary>
        public void HoldNext(Func<BuildCall, bool> when)
        {
            lock (_gate)
            {
                _holdWhen = when;
                _hold = new ManualResetEventSlim(initialState: false);
                _holdEntered = false;
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _hold?.Set();
            }
        }

        public ReadingDocument Build(MailContent content, bool allowRemoteImages, bool allowEmbeddedImages, ReadingTheme theme)
        {
            var call = new BuildCall(content.Subject, allowRemoteImages, allowEmbeddedImages, theme);
            ManualResetEventSlim? hold = null;
            Exception? failure;
            lock (_gate)
            {
                _calls.Add(call);
                failure = FailWith;
                if (_holdWhen is not null && _holdWhen(call))
                {
                    hold = _hold;
                    _holdWhen = null;
                    _holdEntered = true;
                }
            }

            if (hold is not null && !hold.Wait(TimeSpan.FromSeconds(25)))
            {
                throw new TimeoutException("test gate was never released");
            }

            if (failure is not null)
            {
                throw failure;
            }

            return ReadingDocumentBuilder.Build(content, allowRemoteImages, allowEmbeddedImages, theme);
        }
    }

    private static (ShellHarness Harness, ManualTimeProvider Clock, ThemeBuilder Builder, MailFolder Inbox) NewHarness(FolderRole role = FolderRole.Inbox)
    {
        var clock = new ManualTimeProvider(ShellHarness.FixedNow);
        var builder = new ThemeBuilder();
        var harness = new ShellHarness(clock: clock, documentBuilder: builder.Build);
        Account account = harness.ReadModel.AddAccount("Account_A");
        MailFolder folder = harness.ReadModel.AddFolder(account, role, role == FolderRole.Inbox ? "INBOX" : role.ToString());
        return (harness, clock, builder, folder);
    }

    private static MailMessage AddHtml(ShellHarness harness, MailFolder folder, string subject, string html)
    {
        MailMessage message = harness.ReadModel.AddMessage(folder, subject);
        harness.ReadModel.SetContent(message.Id, TestContent.Html(subject, html) with { PlainText = $"{subject}_plain_marker" });
        return message;
    }

    private static bool IsDark(ReadingPaneViewModel reading) =>
        reading.DocumentHtml is { } html
        && html.Contains(DarkSchemeMarker, StringComparison.Ordinal)
        && !html.Contains(LightMarker, StringComparison.Ordinal);

    private static bool IsLight(ReadingPaneViewModel reading) =>
        reading.DocumentHtml is { } html
        && html.Contains(LightMarker, StringComparison.Ordinal)
        && !html.Contains(DarkSchemeMarker, StringComparison.Ordinal);

    // ---- Case 10 -------------------------------------------------------------------------------------------

    // Case 10 control: before any report the pane is light (D5).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Without_a_reported_theme_the_pane_builds_light()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");

        await harness.Shell.Reading.ShowAsync(mail);

        Assert.Equal(ReadingTheme.Light, Assert.Single(builder.Calls).Theme);
        Assert.True(IsLight(harness.Shell.Reading));
    }

    // Case 10: new document from the content already held; no read from the store; state kept.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_with_an_open_mail_rebuilds_from_the_held_content_without_reading_again()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        string lightDocument = reading.DocumentHtml!;
        int readsBefore = harness.ReadModel.ReadCalls;
        int controllerCallsBefore = harness.Controller.Calls.Count;

        await reading.ApplyThemeAsync(ReadingTheme.Dark);

        Assert.Equal(readsBefore, harness.ReadModel.ReadCalls);
        Assert.Equal(controllerCallsBefore, harness.Controller.Calls.Count);
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("mail_a", reading.Subject);
        Assert.True(IsDark(reading));
        Assert.Contains("mail_a_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Equal(ReadingTheme.Dark, builder.Calls[^1].Theme);
        Assert.Null(reading.ContentNotice);

        await reading.ApplyThemeAsync(ReadingTheme.Light);

        Assert.Equal(readsBefore, harness.ReadModel.ReadCalls);
        Assert.Equal(lightDocument, reading.DocumentHtml);
    }

    // Case 10: plain-text mail follows the theme too.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_mail_follows_the_theme()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        MailMessage mail = harness.ReadModel.AddMessage(inbox, "plain_a");
        harness.ReadModel.SetContent(mail.Id, TestContent.Plain("plain_a", "plain_marker_text"));
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        Assert.True(IsLight(reading));

        await reading.ApplyThemeAsync(ReadingTheme.Dark);

        Assert.True(IsDark(reading));
        Assert.Contains("plain_marker_text", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Equal(1, harness.ReadModel.ReadCalls);
    }

    // Case 10: the theme is remembered, the next mail is built in it.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Next_mail_after_a_theme_change_is_built_in_the_new_theme()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage first = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        MailMessage second = AddHtml(harness, inbox, "mail_b", "<p>mail_b_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(first);
        await reading.ApplyThemeAsync(ReadingTheme.Dark);

        await reading.ShowAsync(second);

        Assert.Equal("mail_b", reading.Subject);
        Assert.True(IsDark(reading));
        Assert.Equal(ReadingTheme.Dark, builder.Calls[^1].Theme);
    }

    // Case 10: the selection in the list survives.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_keeps_the_selection_in_the_list()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        await harness.Shell.InitializeAsync();
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 1, "inbox listed");
        harness.Shell.Messages.SelectedRow = harness.Shell.Messages.Items[0];
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Ready, "mail shown");
        object? selected = harness.Shell.Messages.SelectedRow;

        await harness.Shell.Reading.ApplyThemeAsync(ReadingTheme.Dark);

        Assert.Same(selected, harness.Shell.Messages.SelectedRow);
        Assert.Equal("mail_a", harness.Shell.Reading.Subject);
        Assert.Equal(ReadingState.Ready, harness.Shell.Reading.State);
        Assert.True(IsDark(harness.Shell.Reading));
    }

    // Case 10 / D7: the approval of remote images survives a theme change.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_keeps_the_approval_of_remote_images()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", RemoteImageHtml);
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        Assert.True(reading.CanLoadRemoteImages);
        reading.LoadRemoteImagesCommand.Execute(null);
        await Wait.UntilAsync(() => reading.DocumentHtml?.Contains("tracker.test", StringComparison.Ordinal) == true, "images shown");
        int readsBefore = harness.ReadModel.ReadCalls;

        await reading.ApplyThemeAsync(ReadingTheme.Dark);

        Assert.Equal(readsBefore, harness.ReadModel.ReadCalls);
        Assert.Contains("tracker.test", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.False(reading.CanLoadRemoteImages);
        Assert.Equal(0, reading.BlockedRemoteResourceCount);
        Assert.Equal(new BuildCall("mail_a", true, true, ReadingTheme.Dark), builder.Calls[^1]);
    }

    // Case 10 / D7: without approval a theme change must not show the images.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_without_approval_still_blocks_remote_images()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", RemoteImageHtml);
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);

        await reading.ApplyThemeAsync(ReadingTheme.Dark);

        Assert.DoesNotContain("tracker.test", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.True(reading.CanLoadRemoteImages);
        Assert.Equal(1, reading.BlockedRemoteResourceCount);
        Assert.All(builder.Calls, call => Assert.False(call.AllowRemoteImages));
    }

    // Case 10: a mail with colours of its own keeps its document; nothing is announced.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mail_with_own_colours_gives_the_same_html_in_both_themes_and_no_document_change()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p style=\"color:#000\">mail_a_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        string before = reading.DocumentHtml!;
        var changed = new List<string?>();
        ((INotifyPropertyChanged)reading).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        await reading.ApplyThemeAsync(ReadingTheme.Dark);

        Assert.Equal(before, reading.DocumentHtml);
        Assert.DoesNotContain(nameof(ReadingPaneViewModel.DocumentHtml), changed);
        Assert.True(IsLight(reading));
        Assert.Equal(ReadingState.Ready, reading.State);
    }

    // Case 10 control: a mail without colours does announce the new document.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mail_without_colours_announces_the_new_document()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)reading).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        await reading.ApplyThemeAsync(ReadingTheme.Dark);

        Assert.Contains(nameof(ReadingPaneViewModel.DocumentHtml), changed);
    }

    // Same theme again: nothing breaks, document stays.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reporting_the_same_theme_twice_changes_nothing()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        await reading.ApplyThemeAsync(ReadingTheme.Dark);
        string dark = reading.DocumentHtml!;

        await reading.ApplyThemeAsync(ReadingTheme.Dark);

        Assert.Equal(dark, reading.DocumentHtml);
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal(1, harness.ReadModel.ReadCalls);
    }

    // ---- Case 11 -------------------------------------------------------------------------------------------

    // Case 11
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_without_an_open_mail_does_nothing_and_does_not_fail()
    {
        (ShellHarness harness, _, ThemeBuilder builder, _) = NewHarness();
        ReadingPaneViewModel reading = harness.Shell.Reading;
        ReadingState stateBefore = reading.State;
        string? documentBefore = reading.DocumentHtml;

        Exception? failure = await Record.ExceptionAsync(() => reading.ApplyThemeAsync(ReadingTheme.Dark));
        Exception? failureBack = await Record.ExceptionAsync(() => reading.ApplyThemeAsync(ReadingTheme.Light));

        Assert.Null(failure);
        Assert.Null(failureBack);
        Assert.Empty(builder.Calls);
        Assert.Equal(0, harness.ReadModel.ReadCalls);
        Assert.Equal(stateBefore, reading.State);
        Assert.Equal(documentBefore, reading.DocumentHtml);
    }

    // Case 11: after the mail was closed, there is nothing to rebuild.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_after_the_mail_was_closed_does_not_resurrect_it()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        await reading.ShowAsync(null);
        ReadingState stateAfterClose = reading.State;
        string? documentAfterClose = reading.DocumentHtml;
        int callsBefore = builder.Calls.Count;

        Exception? failure = await Record.ExceptionAsync(() => reading.ApplyThemeAsync(ReadingTheme.Dark));

        Assert.Null(failure);
        Assert.Equal(callsBefore, builder.Calls.Count);
        Assert.Equal(stateAfterClose, reading.State);
        Assert.Equal(documentAfterClose, reading.DocumentHtml);
    }

    // Case 11: a mail that is not downloaded stays in its state.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_with_a_not_downloaded_mail_keeps_that_state()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage missing = harness.ReadModel.AddMessage(inbox, emlPath: null);
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(missing);
        Assert.Equal(ReadingState.NotDownloaded, reading.State);

        Exception? failure = await Record.ExceptionAsync(() => reading.ApplyThemeAsync(ReadingTheme.Dark));

        Assert.Null(failure);
        Assert.Equal(ReadingState.NotDownloaded, reading.State);
        Assert.Empty(builder.Calls);
    }

    // "never fails": a failing builder during the theme rebuild is not thrown at the caller.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failing_builder_during_a_theme_change_does_not_throw()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        builder.FailWith = new InvalidOperationException("builder_failure_marker");

        Exception? failure = await Record.ExceptionAsync(() => reading.ApplyThemeAsync(ReadingTheme.Dark));

        Assert.Null(failure);
    }

    // ---- Races (D1, "ends on the newest theme") ------------------------------------------------------------

    // Theme change during ShowAsync.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_during_show_ends_on_the_newest_theme()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        builder.HoldNext(_ => true);
        Task show = reading.ShowAsync(mail);
        await Wait.UntilAsync(() => builder.HoldEntered, "first build running");

        Task apply = reading.ApplyThemeAsync(ReadingTheme.Dark);
        await Wait.NegativeWindowAsync();
        builder.Release();
        Exception? showFailure = await Record.ExceptionAsync(() => show);
        Exception? applyFailure = await Record.ExceptionAsync(() => apply);
        await Wait.UntilAsync(() => IsDark(reading), "dark document shown");
        await Wait.NegativeWindowAsync();

        Assert.Null(showFailure);
        Assert.Null(applyFailure);
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal("mail_a", reading.Subject);
        Assert.True(IsDark(reading));
        Assert.Equal(1, harness.ReadModel.ReadCalls);
    }

    // Theme change during ShowAsync, reversed: dark first, then light while the dark build runs.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Two_theme_changes_during_show_end_on_the_last_one()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ApplyThemeAsync(ReadingTheme.Dark);
        builder.HoldNext(_ => true);
        Task show = reading.ShowAsync(mail);
        await Wait.UntilAsync(() => builder.HoldEntered, "first build running");

        Task toLight = reading.ApplyThemeAsync(ReadingTheme.Light);
        Task toDark = reading.ApplyThemeAsync(ReadingTheme.Dark);
        Task toLightAgain = reading.ApplyThemeAsync(ReadingTheme.Light);
        await Wait.NegativeWindowAsync();
        builder.Release();
        await Record.ExceptionAsync(() => Task.WhenAll(show, toLight, toDark, toLightAgain));
        await Wait.UntilAsync(() => IsLight(reading), "light document shown");
        await Wait.NegativeWindowAsync();

        Assert.True(IsLight(reading));
        Assert.Equal(ReadingState.Ready, reading.State);
    }

    // Theme change during "Bilder laden".
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_during_load_images_ends_on_the_newest_theme_with_the_images()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", RemoteImageHtml);
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        builder.HoldNext(call => call.AllowRemoteImages);
        reading.LoadRemoteImagesCommand.Execute(null);
        await Wait.UntilAsync(() => builder.HoldEntered, "image build running");

        Task apply = reading.ApplyThemeAsync(ReadingTheme.Dark);
        await Wait.NegativeWindowAsync();
        builder.Release();
        await Record.ExceptionAsync(() => apply);
        await Wait.UntilAsync(
            () => reading.DocumentHtml?.Contains("tracker.test", StringComparison.Ordinal) == true,
            "images shown");
        for (int round = 0; round < 3; round++)
        {
            await Wait.NegativeWindowAsync();
        }

        BuildCall last = builder.Calls[^1];
        Assert.True(last.AllowRemoteImages);
        Assert.Equal(ReadingTheme.Dark, last.Theme);
        Assert.False(reading.CanLoadRemoteImages);
        Assert.Contains("tracker.test", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Equal(ReadingState.Ready, reading.State);
    }

    // Theme change during a running theme rebuild.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_during_a_running_theme_rebuild_ends_on_the_newest_theme()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        builder.HoldNext(call => call.Theme == ReadingTheme.Dark);
        Task toDark = reading.ApplyThemeAsync(ReadingTheme.Dark);
        await Wait.UntilAsync(() => builder.HoldEntered, "dark rebuild running");

        Task toLight = reading.ApplyThemeAsync(ReadingTheme.Light);
        await Wait.NegativeWindowAsync();
        builder.Release();
        Exception? darkFailure = await Record.ExceptionAsync(() => toDark);
        Exception? lightFailure = await Record.ExceptionAsync(() => toLight);
        for (int round = 0; round < 3; round++)
        {
            await Wait.NegativeWindowAsync();
        }

        Assert.Null(darkFailure);
        Assert.Null(lightFailure);
        Assert.True(IsLight(reading), "the late dark result replaced the newer light theme");
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.Equal(1, harness.ReadModel.ReadCalls);
    }

    // Theme change during a running theme rebuild, three changes: dark, light, dark.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Dark_light_dark_during_one_running_rebuild_ends_on_dark()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        builder.HoldNext(call => call.Theme == ReadingTheme.Dark);
        Task first = reading.ApplyThemeAsync(ReadingTheme.Dark);
        await Wait.UntilAsync(() => builder.HoldEntered, "dark rebuild running");

        Task second = reading.ApplyThemeAsync(ReadingTheme.Light);
        Task third = reading.ApplyThemeAsync(ReadingTheme.Dark);
        await Wait.NegativeWindowAsync();
        builder.Release();
        await Record.ExceptionAsync(() => Task.WhenAll(first, second, third));
        await Wait.UntilAsync(() => IsDark(reading), "dark document shown");
        for (int round = 0; round < 3; round++)
        {
            await Wait.NegativeWindowAsync();
        }

        Assert.True(IsDark(reading));
        Assert.Equal(ReadingState.Ready, reading.State);
    }

    // Another mail selected while a theme rebuild runs: the late result of the old mail does not replace it.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Late_theme_rebuild_of_an_old_mail_does_not_replace_the_next_mail()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage first = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        MailMessage second = AddHtml(harness, inbox, "mail_b", "<p>mail_b_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(first);
        builder.HoldNext(call => call.Theme == ReadingTheme.Dark && call.Subject == "mail_a");
        Task apply = reading.ApplyThemeAsync(ReadingTheme.Dark);
        await Wait.UntilAsync(() => builder.HoldEntered, "dark rebuild of mail_a running");

        Task show = reading.ShowAsync(second);
        await Wait.NegativeWindowAsync();
        builder.Release();
        await Record.ExceptionAsync(() => Task.WhenAll(apply, show));
        for (int round = 0; round < 3; round++)
        {
            await Wait.NegativeWindowAsync();
        }

        Assert.Equal("mail_b", reading.Subject);
        Assert.Contains("mail_b_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("mail_a_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.True(IsDark(reading));
    }

    // ---- Junk and fallback ---------------------------------------------------------------------------------

    // A mail in Junk keeps "no images" for every theme.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Junk_mail_keeps_no_images_in_every_theme()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder junk) = NewHarness(FolderRole.Junk);
        MailMessage mail = AddHtml(harness, junk, "junk_a", RemoteImageHtml);
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);

        await reading.ApplyThemeAsync(ReadingTheme.Dark);
        await reading.ApplyThemeAsync(ReadingTheme.Light);

        Assert.Equal(
            new[]
            {
                new BuildCall("junk_a", false, false, ReadingTheme.Light),
                new BuildCall("junk_a", false, false, ReadingTheme.Dark),
                new BuildCall("junk_a", false, false, ReadingTheme.Light),
            },
            builder.Calls.ToArray().Where(call => call.Subject == "junk_a").ToArray()[..3]);
        Assert.All(builder.Calls, call => Assert.False(call.AllowRemoteImages || call.AllowEmbeddedImages));
        Assert.DoesNotContain("tracker.test", reading.DocumentHtml, StringComparison.Ordinal);
    }

    // Junk control: an Inbox mail shows embedded images, so the flags differ.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Inbox_mail_builds_with_embedded_images_in_every_theme()
    {
        (ShellHarness harness, _, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", RemoteImageHtml);
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);

        await reading.ApplyThemeAsync(ReadingTheme.Dark);

        Assert.All(builder.Calls, call => Assert.True(call.AllowEmbeddedImages));
        Assert.All(builder.Calls, call => Assert.False(call.AllowRemoteImages));
    }

    // The plain-text fallback is no build: the theme change calls no builder and the notice stays.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_in_the_plain_text_fallback_builds_nothing_and_keeps_the_notice()
    {
        (ShellHarness harness, ManualTimeProvider clock, ThemeBuilder builder, MailFolder inbox) = NewHarness();
        MailMessage mail = AddHtml(harness, inbox, "mail_a", "<p>mail_a_html_marker</p>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        builder.HoldNext(_ => true);
        Task show = reading.ShowAsync(mail);
        await Wait.UntilAsync(() => builder.HoldEntered && clock.ActiveTimerCount >= 1, "build running and time limit armed");
        clock.Advance(ReadingPaneViewModel.BuildTimeLimit + TimeSpan.FromSeconds(1));
        await show;
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));
        Assert.Contains("mail_a_plain_marker", reading.DocumentHtml, StringComparison.Ordinal);
        int callsBefore = builder.Calls.Count;

        Exception? failure = await Record.ExceptionAsync(() => reading.ApplyThemeAsync(ReadingTheme.Dark));
        builder.Release();
        for (int round = 0; round < 3; round++)
        {
            await Wait.NegativeWindowAsync();
        }

        Assert.Null(failure);
        Assert.Equal(callsBefore, builder.Calls.Count);
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));
        Assert.True(reading.HasContentNotice);
        Assert.Contains("mail_a_plain_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("mail_a_html_marker", reading.DocumentHtml, StringComparison.Ordinal);
    }

    // A theme change keeps the notice of a reduced document too (the reduction does not go away).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Theme_change_keeps_the_notice_of_a_reduced_document()
    {
        (ShellHarness harness, _, _, MailFolder inbox) = NewHarness();
        string attributes = string.Concat(Enumerable.Range(0, 300).Select(index => $" a{index}=\"x\""));
        MailMessage mail = AddHtml(harness, inbox, "reduced_a", $"<p>keep_marker</p><div{attributes}>inside</div>");
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(mail);
        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));

        await reading.ApplyThemeAsync(ReadingTheme.Dark);

        Assert.False(string.IsNullOrWhiteSpace(reading.ContentNotice));
        Assert.Contains("keep_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.True(IsDark(reading));
    }
}
