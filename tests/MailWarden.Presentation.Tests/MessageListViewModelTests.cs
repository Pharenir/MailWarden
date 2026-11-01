namespace MailWarden.Presentation.Tests;

/// <summary>AP-04a cases 31-34 (D10): message rows, paging, reload and the stale-result guard.</summary>
[Trait("Category", "feature:ui")]
public sealed class MessageListViewModelTests
{
    private static readonly MailView InboxView = MailView.Group(FolderRole.Inbox);

    private static MessageRowViewModel RowOf(ShellHarness harness, long messageId) =>
        harness.Shell.Messages.Items.Single(row => row.Message.Id == messageId);

    private static string LocalText(DateTimeOffset moment, string format) =>
        TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.Local).ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    // Row texts are shortened with SafeDisplayText.Shorten (K7): whitespace runs collapse to one space.

    // ---- Case 31 -------------------------------------------------------------------------------------------

    // Case 31
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Row_shows_sender_name_subject_preview_and_account_label()
    {
        var harness = new ShellHarness();
        (Account account, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        MailMessage message = harness.ReadModel.AddMessage(inbox, "subject_marker", fromName: "Sender A", preview: "preview_marker");

        await harness.Shell.Messages.LoadAsync(InboxView);

        MessageRowViewModel row = Assert.Single(harness.Shell.Messages.Items);
        Assert.Equal(message, row.Message);
        Assert.Equal("Sender A", row.Sender);
        Assert.Equal("subject_marker", row.Subject);
        Assert.Equal("preview_marker", row.Preview);
        Assert.Equal(account.DisplayName, row.AccountLabel);
    }

    // Case 31
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Sender_falls_back_to_the_address_when_the_name_is_empty()
    {
        var harness = new ShellHarness();
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        MailMessage message = harness.ReadModel.AddMessage(inbox, fromName: string.Empty, fromAddress: "sender_b@example.test");

        await harness.Shell.Messages.LoadAsync(InboxView);

        Assert.Equal("sender_b@example.test", RowOf(harness, message.Id).Sender);
    }

    // Case 31
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_subject_shows_the_placeholder_text()
    {
        var harness = new ShellHarness();
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        MailMessage message = harness.ReadModel.AddMessage(inbox, subject: string.Empty);

        await harness.Shell.Messages.LoadAsync(InboxView);

        Assert.Equal("(Kein Betreff)", RowOf(harness, message.Id).Subject);
    }

    // Case 31: every mail-derived text goes through the display-text rule.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Row_texts_are_cleaned_of_control_and_bidi_characters()
    {
        var harness = new ShellHarness();
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        MailMessage message = harness.ReadModel.AddMessage(
            inbox,
            subject: "Re:\u202E x\r\ny\u0007",
            fromName: "Se\u202Ender\u2066 A",
            preview: "pre\u0007view\tx\nz");

        await harness.Shell.Messages.LoadAsync(InboxView);

        MessageRowViewModel row = RowOf(harness, message.Id);
        Assert.Equal("Re: x y", row.Subject);
        Assert.Equal("Sender A", row.Sender);
        Assert.Equal("preview x z", row.Preview);
    }

    // Case 31
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Account_label_is_the_cleaned_display_name_or_the_address_when_that_is_empty()
    {
        var harness = new ShellHarness();
        Account named = harness.ReadModel.AddAccount("Account\u202E N");
        Account unnamed = harness.ReadModel.AddAccount(string.Empty, "unnamed_user@example.test");
        MailMessage fromNamed = harness.ReadModel.AddMessage(harness.ReadModel.AddFolder(named, FolderRole.Inbox, "INBOX"));
        MailMessage fromUnnamed = harness.ReadModel.AddMessage(harness.ReadModel.AddFolder(unnamed, FolderRole.Inbox, "INBOX"));

        await harness.Shell.Messages.LoadAsync(InboxView);

        Assert.Equal("Account N", RowOf(harness, fromNamed.Id).AccountLabel);
        Assert.Equal("unnamed_user@example.test", RowOf(harness, fromUnnamed.Id).AccountLabel);
    }

    // Case 31
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unread_flagged_and_attachment_markers_follow_the_message()
    {
        var harness = new ShellHarness();
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        MailMessage plainUnread = harness.ReadModel.AddMessage(inbox, flags: MessageFlags.None);
        MailMessage seen = harness.ReadModel.AddMessage(inbox, flags: MessageFlags.Seen);
        MailMessage flaggedUnread = harness.ReadModel.AddMessage(inbox, flags: MessageFlags.Flagged);
        MailMessage seenFlagged = harness.ReadModel.AddMessage(inbox, flags: MessageFlags.Seen | MessageFlags.Flagged, hasAttachments: true);

        await harness.Shell.Messages.LoadAsync(InboxView);

        MessageRowViewModel first = RowOf(harness, plainUnread.Id);
        Assert.True(first.IsUnread);
        Assert.False(first.IsFlagged);
        Assert.False(first.HasAttachments);
        Assert.False(RowOf(harness, seen.Id).IsUnread);
        MessageRowViewModel third = RowOf(harness, flaggedUnread.Id);
        Assert.True(third.IsUnread);
        Assert.True(third.IsFlagged);
        MessageRowViewModel fourth = RowOf(harness, seenFlagged.Id);
        Assert.False(fourth.IsUnread);
        Assert.True(fourth.IsFlagged);
        Assert.True(fourth.HasAttachments);
    }

    // Case 31 (fake clock 2026-10-31 12:00 UTC; expectations use the machine's local zone like the view model)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Date_text_is_time_today_day_and_month_this_year_and_full_date_otherwise()
    {
        var harness = new ShellHarness();
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        DateTimeOffset today = ShellHarness.FixedNow.AddHours(-1);
        DateTimeOffset thisYear = new(2026, 3, 5, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset older = new(2025, 12, 24, 12, 0, 0, TimeSpan.Zero);
        MailMessage todayMessage = harness.ReadModel.AddMessage(inbox, date: today);
        MailMessage thisYearMessage = harness.ReadModel.AddMessage(inbox, date: thisYear);
        MailMessage olderMessage = harness.ReadModel.AddMessage(inbox, date: older);

        await harness.Shell.Messages.LoadAsync(InboxView);

        Assert.Equal(LocalText(today, "HH:mm"), RowOf(harness, todayMessage.Id).DateText);
        Assert.Equal(LocalText(thisYear, "dd.MM."), RowOf(harness, thisYearMessage.Id).DateText);
        Assert.Equal(LocalText(older, "dd.MM.yyyy"), RowOf(harness, olderMessage.Id).DateText);
    }

    // ---- Case 32 -------------------------------------------------------------------------------------------

    // Case 32
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Paging_loads_pages_of_one_hundred_newest_first_without_duplicates()
    {
        var harness = new ShellHarness();
        harness.SeedInbox("Account_A", 250);
        MessageListViewModel list = harness.Shell.Messages;

        await list.LoadAsync(InboxView);
        Assert.Equal(100, list.Items.Count);
        Assert.True(list.HasMore);
        Assert.False(list.IsLoading);
        Assert.Equal(250L, list.Items[0].Message.Id);

        await list.LoadMoreAsync();
        Assert.Equal(200, list.Items.Count);
        Assert.True(list.HasMore);

        await list.LoadMoreAsync();
        Assert.Equal(250, list.Items.Count);
        Assert.False(list.HasMore);

        long[] ids = [.. list.Items.Select(row => row.Message.Id)];
        Assert.Equal(250, ids.Distinct().Count());
        Assert.Equal(ids.OrderByDescending(id => id).ToArray(), ids);
        Assert.Equal(new[] { 0, 100, 200 }, harness.ReadModel.ListQueries.Select(query => query.Offset).ToArray());
        Assert.All(harness.ReadModel.ListQueries, query => Assert.Equal(MessageListViewModel.PageSize, query.Limit));
    }

    // Case 32
    [Fact]
    public void Page_size_is_one_hundred()
    {
        Assert.Equal(100, MessageListViewModel.PageSize);
    }

    // Case 32
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Load_more_without_more_messages_does_not_query_again()
    {
        var harness = new ShellHarness();
        harness.SeedInbox("Account_A", 30);
        await harness.Shell.Messages.LoadAsync(InboxView);
        Assert.False(harness.Shell.Messages.HasMore);
        int callsBefore = harness.ReadModel.ListMessagesCalls;

        await harness.Shell.Messages.LoadMoreAsync();

        Assert.Equal(30, harness.Shell.Messages.Items.Count);
        Assert.Equal(callsBefore, harness.ReadModel.ListMessagesCalls);
    }

    // Case 32: a full last page is the only hint for "more", so exactly one page ends with an empty follow-up page.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Exactly_one_full_page_reports_more_until_the_next_page_is_empty()
    {
        var harness = new ShellHarness();
        harness.SeedInbox("Account_A", 100);
        MessageListViewModel list = harness.Shell.Messages;

        await list.LoadAsync(InboxView);
        Assert.True(list.HasMore);

        await list.LoadMoreAsync();
        Assert.Equal(100, list.Items.Count);
        Assert.False(list.HasMore);
    }

    // Case 32
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_view_has_no_rows_and_no_more()
    {
        var harness = new ShellHarness();
        harness.SeedInbox("Account_A", 0);

        await harness.Shell.Messages.LoadAsync(InboxView);

        Assert.Empty(harness.Shell.Messages.Items);
        Assert.False(harness.Shell.Messages.HasMore);
        Assert.False(harness.Shell.Messages.IsLoading);
    }

    // Case 32: loading another view replaces the rows and restarts paging at offset 0.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Loading_another_view_replaces_the_rows_and_restarts_at_offset_zero()
    {
        var harness = new ShellHarness();
        (Account account, _) = harness.SeedInbox("Account_A", 150);
        MailFolder junk = harness.ReadModel.AddFolder(account, FolderRole.Junk, "Junk");
        MailMessage junkMessage = harness.ReadModel.AddMessage(junk, "junk_subject");
        await harness.Shell.Messages.LoadAsync(InboxView);
        await harness.Shell.Messages.LoadMoreAsync();

        await harness.Shell.Messages.LoadAsync(MailView.Group(FolderRole.Junk));

        Assert.Equal(junkMessage.Id, Assert.Single(harness.Shell.Messages.Items).Message.Id);
        Assert.Equal(0, harness.ReadModel.ListQueries.Last().Offset);
        Assert.Equal(FolderRole.Junk, harness.ReadModel.ListQueries.Last().Role);
        Assert.False(harness.Shell.Messages.HasMore);
    }

    // ---- Case 33 -------------------------------------------------------------------------------------------

    // Case 33
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reload_shows_as_many_rows_as_before_and_keeps_the_selection_by_message_id()
    {
        var harness = new ShellHarness();
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 250);
        MessageListViewModel list = harness.Shell.Messages;
        await list.LoadAsync(InboxView);
        await list.LoadMoreAsync();
        long selectedId = list.Items[5].Message.Id;
        list.SelectedRow = list.Items[5];
        MailMessage newest = harness.ReadModel.AddMessage(inbox, "brand_new_subject");

        await list.ReloadAsync();

        Assert.Equal(200, list.Items.Count);
        Assert.Equal(newest.Id, list.Items[0].Message.Id);
        Assert.Equal(selectedId, list.SelectedRow?.Message.Id);
        Assert.Contains(list.Items, row => ReferenceEquals(row, list.SelectedRow));
    }

    // Case 33
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reload_clears_the_selection_when_the_message_is_gone()
    {
        var harness = new ShellHarness();
        harness.SeedInbox("Account_A", 20);
        MessageListViewModel list = harness.Shell.Messages;
        await list.LoadAsync(InboxView);
        list.SelectedRow = list.Items[3];
        harness.ReadModel.RemoveMessage(list.Items[3].Message.Id);

        await list.ReloadAsync();

        Assert.Equal(19, list.Items.Count);
        Assert.Null(list.SelectedRow);
    }

    // Case 33: "at least one page" - a short list that grows is reloaded with a full page size.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reload_of_a_short_list_loads_at_least_one_page_so_new_messages_appear()
    {
        var harness = new ShellHarness();
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 30);
        MessageListViewModel list = harness.Shell.Messages;
        await list.LoadAsync(InboxView);
        for (int index = 0; index < 10; index++)
        {
            harness.ReadModel.AddMessage(inbox, $"late_{index}");
        }

        await list.ReloadAsync();

        Assert.Equal(40, list.Items.Count);
        Assert.False(list.HasMore);
    }

    // Case 33
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reload_without_a_selection_keeps_it_empty()
    {
        var harness = new ShellHarness();
        harness.SeedInbox("Account_A", 5);
        await harness.Shell.Messages.LoadAsync(InboxView);

        await harness.Shell.Messages.ReloadAsync();

        Assert.Null(harness.Shell.Messages.SelectedRow);
        Assert.Equal(5, harness.Shell.Messages.Items.Count);
    }

    // ---- Case 34 -------------------------------------------------------------------------------------------

    // Case 34
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Slow_first_load_finishing_after_a_second_one_does_not_overwrite_it()
    {
        var harness = new ShellHarness();
        (Account account, _) = harness.SeedInbox("Account_A", 4);
        MailFolder junk = harness.ReadModel.AddFolder(account, FolderRole.Junk, "Junk");
        MailMessage junkOne = harness.ReadModel.AddMessage(junk, "junk_one");
        MailMessage junkTwo = harness.ReadModel.AddMessage(junk, "junk_two");
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.ListGate = (_, callNumber) => callNumber == 1 ? releaseFirst.Task : Task.CompletedTask;
        MessageListViewModel list = harness.Shell.Messages;

        Task first = list.LoadAsync(InboxView);
        await Wait.UntilAsync(() => harness.ReadModel.ListMessagesCalls >= 1, "first load reached the read model");
        await list.LoadAsync(MailView.Group(FolderRole.Junk));
        Assert.Equal(2, list.Items.Count);

        releaseFirst.SetResult();
        Exception? failure = await Record.ExceptionAsync(() => first);
        await Wait.NegativeWindowAsync();

        Assert.True(failure is null or OperationCanceledException, $"unexpected {failure}");
        Assert.Equal(new[] { junkTwo.Id, junkOne.Id }, list.Items.Select(row => row.Message.Id).ToArray());
        Assert.False(list.IsLoading);
    }

    // Case 34: the newer request being the slow one is the normal case and must still complete.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Slow_second_load_still_wins_over_a_quick_first_one()
    {
        var harness = new ShellHarness();
        (Account account, _) = harness.SeedInbox("Account_A", 4);
        MailFolder junk = harness.ReadModel.AddFolder(account, FolderRole.Junk, "Junk");
        MailMessage junkOne = harness.ReadModel.AddMessage(junk, "junk_one");
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.ListGate = (_, callNumber) => callNumber == 2 ? releaseSecond.Task : Task.CompletedTask;
        MessageListViewModel list = harness.Shell.Messages;

        await list.LoadAsync(InboxView);
        Task second = list.LoadAsync(MailView.Group(FolderRole.Junk));
        await Wait.UntilAsync(() => harness.ReadModel.ListMessagesCalls >= 2, "second load reached the read model");
        releaseSecond.SetResult();
        await second;

        Assert.Equal(junkOne.Id, Assert.Single(list.Items).Message.Id);
    }

    // ---- Case 54 (K7) --------------------------------------------------------------------------------------

    private const string Ellipsis = "\u2026";

    // Case 54
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Huge_subject_sender_and_preview_are_shortened_in_the_row()
    {
        var harness = new ShellHarness();
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        MailMessage message = harness.ReadModel.AddMessage(
            inbox,
            subject: new string('s', 365 * 1024),
            fromName: "N" + new string(' ', 5_000) + "M" + new string('x', 5_000),
            preview: new string('p', 5_000));

        await harness.Shell.Messages.LoadAsync(InboxView);

        MessageRowViewModel row = RowOf(harness, message.Id);
        Assert.True(row.Subject.Length <= 501, $"subject has {row.Subject.Length} characters");
        Assert.EndsWith(Ellipsis, row.Subject, StringComparison.Ordinal);
        Assert.True(row.Sender.Length <= 101, $"sender has {row.Sender.Length} characters");
        Assert.StartsWith("N M", row.Sender, StringComparison.Ordinal);
        Assert.True(row.Preview.Length <= 201, $"preview has {row.Preview.Length} characters");
        Assert.EndsWith(Ellipsis, row.Preview, StringComparison.Ordinal);
    }

    // Case 54 positive control: texts within the limits stay as they are (after collapsing whitespace).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Texts_within_the_limits_are_not_cut()
    {
        var harness = new ShellHarness();
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        MailMessage atLimit = harness.ReadModel.AddMessage(
            inbox, subject: new string('s', 500), fromName: new string('n', 100), preview: new string('p', 200));
        MailMessage overLimit = harness.ReadModel.AddMessage(
            inbox, subject: new string('s', 501), fromName: new string('n', 101), preview: new string('p', 201));
        MailMessage spaced = harness.ReadModel.AddMessage(inbox, subject: "a   b", fromName: "Sender   A", preview: "p   q");

        await harness.Shell.Messages.LoadAsync(InboxView);

        MessageRowViewModel exact = RowOf(harness, atLimit.Id);
        Assert.Equal(new string('s', 500), exact.Subject);
        Assert.Equal(new string('n', 100), exact.Sender);
        Assert.Equal(new string('p', 200), exact.Preview);
        MessageRowViewModel cut = RowOf(harness, overLimit.Id);
        Assert.EndsWith(Ellipsis, cut.Subject, StringComparison.Ordinal);
        Assert.EndsWith(Ellipsis, cut.Sender, StringComparison.Ordinal);
        Assert.EndsWith(Ellipsis, cut.Preview, StringComparison.Ordinal);
        MessageRowViewModel collapsed = RowOf(harness, spaced.Id);
        Assert.Equal("a b", collapsed.Subject);
        Assert.Equal("Sender A", collapsed.Sender);
        Assert.Equal("p q", collapsed.Preview);
    }
    // ---- Selection drives the reading pane -----------------------------------------------------------------

    // Case 31/33 glue: a selected row shows its mail; no row or another view shows Empty (D10 "List loading").
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Selected_row_is_shown_and_clearing_or_changing_the_view_shows_empty()
    {
        var harness = new ShellHarness();
        (Account account, _) = harness.SeedInbox("Account_A", 3);
        harness.ReadModel.AddMessage(harness.ReadModel.AddFolder(account, FolderRole.Junk, "Junk"), "junk_subject");
        MessageListViewModel list = harness.Shell.Messages;
        await list.LoadAsync(InboxView);

        list.SelectedRow = list.Items[0];
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Ready, "selected message shown");
        Assert.Equal(list.Items[0].Message.Subject, harness.Shell.Reading.Subject);

        list.SelectedRow = null;
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Empty, "no selection shows empty");

        list.SelectedRow = list.Items[1];
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Ready, "second message shown");
        await list.LoadAsync(MailView.Group(FolderRole.Junk));
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Empty, "another view shows empty");
        Assert.Null(list.SelectedRow);
    }
}
