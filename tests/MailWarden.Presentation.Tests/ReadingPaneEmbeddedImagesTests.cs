namespace MailWarden.Presentation.Tests;

/// <summary>
/// AP-04a cases 99-102 (P3): mails in Junk, Quarantine or an unknown folder show no embedded images until approved. The
/// decision follows the role of the message's own folder, not the selected view.
/// </summary>
[Trait("Category", "feature:ui")]
public sealed class ReadingPaneEmbeddedImagesTests
{
    private const string MarkerSecret = "marker_secret_text";
    private const string EmbeddedCid = "logo@example.test";
    private const string DataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static MailMessage AddImageMail(ShellHarness harness, MailFolder folder, string subject, bool withRemoteImage = false)
    {
        MailMessage message = harness.ReadModel.AddMessage(folder, subject);
        string html =
            $"<p>{subject}_marker</p><img src=\"cid:{EmbeddedCid}\"><img src=\"{DataUri}\">" +
            (withRemoteImage ? "<img src=\"https://tracker.test/a.gif\">" : string.Empty);
        harness.ReadModel.SetContent(
            message.Id,
            TestContent.Html(subject, html) with { InlineImages = new Dictionary<string, string> { [EmbeddedCid] = DataUri } });
        return message;
    }

    private static bool ShowsEmbeddedImages(ReadingPaneViewModel reading) =>
        reading.DocumentHtml?.Contains("data:image/png;base64,iVBOR", StringComparison.Ordinal) == true;

    private static bool ShowsRemoteImage(ReadingPaneViewModel reading) =>
        reading.DocumentHtml?.Contains("tracker.test", StringComparison.Ordinal) == true;

    private static (ShellHarness Harness, Account Account) NewHarness()
    {
        var harness = new ShellHarness();
        Account account = harness.ReadModel.AddAccount("Account_A");
        return (harness, account);
    }

    // ---- Case 99 -------------------------------------------------------------------------------------------

    // Case 99
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Junk)]
    [InlineData(FolderRole.Quarantine)]
    public async Task Mail_in_junk_or_quarantine_shows_no_embedded_images_and_offers_them(FolderRole role)
    {
        (ShellHarness harness, Account account) = NewHarness();
        MailFolder folder = harness.ReadModel.AddFolder(account, role);
        MailMessage message = AddImageMail(harness, folder, "risky");

        await harness.Shell.Reading.ShowAsync(message);

        ReadingPaneViewModel reading = harness.Shell.Reading;
        Assert.Equal(ReadingState.Ready, reading.State);
        Assert.False(ShowsEmbeddedImages(reading), "embedded images are shown in a risky folder");
        Assert.Contains("risky_marker", reading.DocumentHtml, StringComparison.Ordinal);
        Assert.Equal(2, reading.BlockedRemoteResourceCount);
        Assert.True(reading.CanLoadRemoteImages);
    }

    // Case 99
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mail_in_an_unknown_folder_is_treated_like_junk()
    {
        (ShellHarness harness, Account account) = NewHarness();
        MailFolder folder = harness.ReadModel.AddFolder(account, FolderRole.Inbox);
        harness.ReadModel.SetFolderRole(folder.Id, null);
        MailMessage message = AddImageMail(harness, folder, "unknown");

        await harness.Shell.Reading.ShowAsync(message);

        Assert.False(ShowsEmbeddedImages(harness.Shell.Reading));
        Assert.Equal(2, harness.Shell.Reading.BlockedRemoteResourceCount);
        Assert.True(harness.Shell.Reading.CanLoadRemoteImages);
    }

    // Case 99 positive control: every other role shows embedded images at once and counts nothing.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Inbox)]
    [InlineData(FolderRole.Sent)]
    [InlineData(FolderRole.Archive)]
    [InlineData(FolderRole.Trash)]
    [InlineData(FolderRole.Drafts)]
    [InlineData(FolderRole.None)]
    public async Task Mail_in_an_ordinary_folder_shows_embedded_images_at_once(FolderRole role)
    {
        (ShellHarness harness, Account account) = NewHarness();
        MailFolder folder = harness.ReadModel.AddFolder(account, role);
        MailMessage message = AddImageMail(harness, folder, "ordinary");

        await harness.Shell.Reading.ShowAsync(message);

        ReadingPaneViewModel reading = harness.Shell.Reading;
        Assert.True(ShowsEmbeddedImages(reading), "embedded images are missing");
        Assert.Equal(0, reading.BlockedRemoteResourceCount);
        Assert.False(reading.CanLoadRemoteImages);
    }

    // Case 99: a remote image is counted on top of the embedded ones.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Count_in_junk_includes_embedded_and_remote_images()
    {
        (ShellHarness harness, Account account) = NewHarness();
        MailFolder junk = harness.ReadModel.AddFolder(account, FolderRole.Junk);
        MailMessage message = AddImageMail(harness, junk, "risky", withRemoteImage: true);

        await harness.Shell.Reading.ShowAsync(message);

        Assert.Equal(3, harness.Shell.Reading.BlockedRemoteResourceCount);
        Assert.False(ShowsRemoteImage(harness.Shell.Reading));
    }

    // Case 99: plain-text mails in Junk are unaffected (nothing to block, nothing to offer).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_text_mail_in_junk_is_shown_normally()
    {
        (ShellHarness harness, Account account) = NewHarness();
        MailFolder junk = harness.ReadModel.AddFolder(account, FolderRole.Junk);
        MailMessage message = harness.ReadModel.AddMessage(junk, "plain");
        harness.ReadModel.SetContent(message.Id, TestContent.Plain("plain", "plain_marker_text"));

        await harness.Shell.Reading.ShowAsync(message);

        Assert.Equal(ReadingState.Ready, harness.Shell.Reading.State);
        Assert.Contains("plain_marker_text", harness.Shell.Reading.DocumentHtml, StringComparison.Ordinal);
        Assert.False(harness.Shell.Reading.CanLoadRemoteImages);
    }

    // ---- Case 100 ------------------------------------------------------------------------------------------

    // Case 100
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Inbox_mail_shows_embedded_images_at_once_and_counts_only_remote_ones()
    {
        (ShellHarness harness, Account account) = NewHarness();
        MailFolder inbox = harness.ReadModel.AddFolder(account, FolderRole.Inbox, "INBOX");
        MailMessage message = AddImageMail(harness, inbox, "inbox", withRemoteImage: true);

        await harness.Shell.Reading.ShowAsync(message);

        Assert.True(ShowsEmbeddedImages(harness.Shell.Reading));
        Assert.False(ShowsRemoteImage(harness.Shell.Reading));
        Assert.Equal(1, harness.Shell.Reading.BlockedRemoteResourceCount);
        Assert.True(harness.Shell.Reading.CanLoadRemoteImages);
    }

    // Case 100: the same inbox mail selected through the group view of the Inbox behaves the same.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Inbox_mail_selected_in_the_inbox_group_view_shows_embedded_images()
    {
        (ShellHarness harness, Account account) = NewHarness();
        MailFolder inbox = harness.ReadModel.AddFolder(account, FolderRole.Inbox, "INBOX");
        AddImageMail(harness, inbox, "inbox", withRemoteImage: true);
        await harness.Shell.InitializeAsync();
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 1, "inbox listed");

        harness.Shell.Messages.SelectedRow = harness.Shell.Messages.Items[0];

        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Ready, "mail shown");
        Assert.True(ShowsEmbeddedImages(harness.Shell.Reading));
        Assert.Equal(1, harness.Shell.Reading.BlockedRemoteResourceCount);
    }

    // Case 100: a Junk mail shown through the group view Junk is blocked.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Junk_mail_selected_in_the_junk_group_view_is_blocked()
    {
        (ShellHarness harness, Account account) = NewHarness();
        harness.ReadModel.AddFolder(account, FolderRole.Inbox, "INBOX");
        MailFolder junk = harness.ReadModel.AddFolder(account, FolderRole.Junk, "Junk");
        MailMessage junkMail = AddImageMail(harness, junk, "junkmail");
        await harness.Shell.InitializeAsync();
        harness.Shell.Navigation.SelectedItem = harness.Shell.Navigation.Items[4];
        await Wait.UntilAsync(
            () => harness.Shell.Messages.Items.Count == 1 && harness.Shell.Messages.Items[0].Message.Id == junkMail.Id,
            "junk listed");

        harness.Shell.Messages.SelectedRow = harness.Shell.Messages.Items[0];

        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Ready, "mail shown");
        Assert.False(ShowsEmbeddedImages(harness.Shell.Reading));
        Assert.True(harness.Shell.Reading.CanLoadRemoteImages);
    }

    // Case 100: the decision uses the folder of the mail, not the view: a Junk mail in a view of another folder is blocked.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Junk_mail_shown_directly_is_blocked_whatever_view_is_selected()
    {
        (ShellHarness harness, Account account) = NewHarness();
        harness.ReadModel.AddFolder(account, FolderRole.Inbox, "INBOX");
        MailFolder junk = harness.ReadModel.AddFolder(account, FolderRole.Junk, "Junk");
        MailMessage junkMail = AddImageMail(harness, junk, "junkmail");
        await harness.Shell.InitializeAsync();

        await harness.Shell.Reading.ShowAsync(junkMail);

        Assert.False(ShowsEmbeddedImages(harness.Shell.Reading));
    }

    // ---- Case 101 ------------------------------------------------------------------------------------------

    // Case 101
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Approval_in_junk_shows_both_kinds_of_images_for_this_mail_only()
    {
        (ShellHarness harness, Account account) = NewHarness();
        MailFolder junk = harness.ReadModel.AddFolder(account, FolderRole.Junk);
        MailMessage first = AddImageMail(harness, junk, "first", withRemoteImage: true);
        MailMessage next = AddImageMail(harness, junk, "next", withRemoteImage: true);
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(first);
        Assert.False(ShowsEmbeddedImages(reading));
        Assert.True(reading.CanLoadRemoteImages);

        reading.LoadRemoteImagesCommand.Execute(null);
        await Wait.UntilAsync(() => ShowsEmbeddedImages(reading) && ShowsRemoteImage(reading), "both kinds shown after approval");
        Assert.False(reading.CanLoadRemoteImages);

        await reading.ShowAsync(next);
        Assert.False(ShowsEmbeddedImages(reading), "the approval carried over to the next mail");
        Assert.False(ShowsRemoteImage(reading));
        Assert.True(reading.CanLoadRemoteImages);

        await reading.ShowAsync(first);
        Assert.False(ShowsEmbeddedImages(reading), "the approval came back with the first mail");
        Assert.False(ShowsRemoteImage(reading));
        Assert.True(reading.CanLoadRemoteImages);
    }

    // Case 101 positive control: in the Inbox the approval still only releases the remote images.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Approval_in_the_inbox_releases_the_remote_images_as_before()
    {
        (ShellHarness harness, Account account) = NewHarness();
        MailFolder inbox = harness.ReadModel.AddFolder(account, FolderRole.Inbox);
        MailMessage message = AddImageMail(harness, inbox, "inbox", withRemoteImage: true);
        ReadingPaneViewModel reading = harness.Shell.Reading;
        await reading.ShowAsync(message);
        Assert.True(ShowsEmbeddedImages(reading));

        reading.LoadRemoteImagesCommand.Execute(null);
        await Wait.UntilAsync(() => ShowsRemoteImage(reading), "remote image shown after approval");

        Assert.True(ShowsEmbeddedImages(reading));
        Assert.False(reading.CanLoadRemoteImages);
    }

    // ---- Case 102 ------------------------------------------------------------------------------------------

    // Case 102
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failure_while_reading_the_folder_role_ends_in_error_without_leaking_the_message()
    {
        (ShellHarness harness, Account account) = NewHarness();
        MailFolder folder = harness.ReadModel.AddFolder(account, FolderRole.Inbox);
        MailMessage message = AddImageMail(harness, folder, "subject_of_the_mail");
        harness.ReadModel.FolderRoleFailure = new InvalidOperationException(MarkerSecret);

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Reading.ShowAsync(message));

        Assert.Null(failure);
        ReadingPaneViewModel reading = harness.Shell.Reading;
        Assert.Equal(ReadingState.Error, reading.State);
        Assert.Null(reading.DocumentHtml);
        foreach (string text in new[] { reading.Subject, reading.From, reading.To, reading.Cc, reading.DateText, reading.ContentNotice ?? string.Empty })
        {
            Assert.DoesNotContain(MarkerSecret, text, StringComparison.Ordinal);
        }

        Assert.Contains("InvalidOperationException", harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerSecret, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("subject_of_the_mail", harness.Log.AllText, StringComparison.Ordinal);
    }

    // Case 102 positive control: without the failure the same mail is shown, and the pane recovers afterwards.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Same_mail_is_shown_once_the_folder_role_can_be_read_again()
    {
        (ShellHarness harness, Account account) = NewHarness();
        MailFolder folder = harness.ReadModel.AddFolder(account, FolderRole.Inbox);
        MailMessage message = AddImageMail(harness, folder, "recovering");
        harness.ReadModel.FolderRoleFailure = new InvalidOperationException(MarkerSecret);
        await harness.Shell.Reading.ShowAsync(message);
        Assert.Equal(ReadingState.Error, harness.Shell.Reading.State);

        harness.ReadModel.FolderRoleFailure = null;
        await harness.Shell.Reading.ShowAsync(message);

        Assert.Equal(ReadingState.Ready, harness.Shell.Reading.State);
        Assert.True(ShowsEmbeddedImages(harness.Shell.Reading));
    }
}
