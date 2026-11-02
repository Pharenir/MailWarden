using MailWarden.Infrastructure.Mime;
using MimeKit;

namespace MailWarden.Presentation.Tests;

/// <summary>
/// AP-04c case 7 and the row rules of A1, A4, A5, A7, A10-A12: the rows of the reading pane follow the shown mail and
/// carry name, size, risk and the permission to open. No file is written here.
/// </summary>
[Trait("Category", "feature:ui")]
public sealed class AttachmentRowTests : IDisposable
{
    private readonly List<ShellHarness> _harnesses = [];

    public void Dispose()
    {
        foreach (ShellHarness harness in _harnesses)
        {
            harness.Dispose();
        }
    }

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

        MailMessage message = harness.ReadModel.AddMessage(folder, "attachment_mail", hasAttachments: attachments.Length > 0);
        harness.ReadModel.SetContent(message.Id, TestContent.Plain("attachment_mail", "body", attachments));
        await harness.Shell.Reading.ShowAsync(message);
        return (harness, message);
    }

    // ---- Case 7 --------------------------------------------------------------------------------------------

    // Case 7: attachment, inline image and nested mail, read by the real reader; rows match MailContent.Attachments.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Rows_match_the_attachments_of_the_content_and_the_inline_image_has_no_row()
    {
        var builder = new BodyBuilder { TextBody = "text part", HtmlBody = "<p>html part</p><img src=\"cid:logo_1@example.test\">" };
        MimeEntity inline = builder.LinkedResources.Add("inline_logo.png", TinyPngBytes(), ContentType.Parse("image/png"));
        inline.ContentId = "logo_1@example.test";
        builder.Attachments.Add("report_a.pdf", new byte[20], ContentType.Parse("application/pdf"));
        builder.Attachments.Add("notes_b.txt", new byte[5], ContentType.Parse("text/plain"));
        var forwarded = new MimeMessage { Subject = "forwarded_subject" };
        forwarded.From.Add(new MailboxAddress("Sender A", "sender_a@example.test"));
        forwarded.To.Add(new MailboxAddress("Recipient B", "recipient_b@example.test"));
        forwarded.Body = new TextPart("plain") { Text = "forwarded body" };
        var outer = new MimeMessage { Subject = "outer_subject", Date = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero) };
        outer.From.Add(new MailboxAddress("Sender A", "sender_a@example.test"));
        outer.To.Add(new MailboxAddress("Recipient B", "recipient_b@example.test"));
        outer.Body = new Multipart("mixed") { builder.ToMessageBody(), new MessagePart("rfc822") { Message = forwarded } };
        using var stream = new MemoryStream();
        outer.WriteTo(stream);
        stream.Position = 0;
        MailContent content = await MailContentReader.ReadAsync(stream);
        Assert.DoesNotContain(content.Attachments, info => info.FileName == "inline_logo.png");
        Assert.True(content.Attachments.Count >= 3, "the reader should list two files and the nested message");

        var harness = new ShellHarness();
        _harnesses.Add(harness);
        Account account = harness.ReadModel.AddAccount("Account_A");
        MailFolder inbox = harness.ReadModel.AddFolder(account, FolderRole.Inbox, "INBOX");
        MailMessage message = harness.ReadModel.AddMessage(inbox, "outer_subject", hasAttachments: true);
        harness.ReadModel.SetContent(message.Id, content);

        await harness.Shell.Reading.ShowAsync(message);

        IReadOnlyList<AttachmentRow> rows = harness.Shell.Reading.Attachments;
        Assert.Equal(content.Attachments.Count, rows.Count);
        for (int position = 0; position < rows.Count; position++)
        {
            Assert.Equal(position, rows[position].Index);
            Assert.Equal(content.Attachments[position].FileName, rows[position].Name);
        }

        Assert.DoesNotContain(rows, row => row.Name.Contains("inline_logo", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.SafeFileName.Contains("inline_logo", StringComparison.Ordinal));
    }

    // Case 7 / A1: the names go through the display cleaning, sizes are text or empty.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Rows_show_cleaned_names_and_a_size_text_that_is_empty_when_the_size_is_unknown()
    {
        (ShellHarness harness, _) = await ShowAsync(
            FolderRole.Inbox,
            new MailAttachmentInfo("report_a.pdf", "application/pdf", 20),
            new MailAttachmentInfo("na‮me.txt", "text/plain", null),
            new MailAttachmentInfo("big_c.zip", "application/zip", 5_000_000));

        IReadOnlyList<AttachmentRow> rows = harness.Shell.Reading.Attachments;

        Assert.Equal(3, rows.Count);
        Assert.NotEqual(string.Empty, rows[0].SizeText);
        Assert.Equal(string.Empty, rows[1].SizeText);
        Assert.NotEqual(string.Empty, rows[2].SizeText);
        Assert.NotEqual(rows[0].SizeText, rows[2].SizeText);
        Assert.DoesNotContain("‮", rows[1].Name, StringComparison.Ordinal);
        Assert.DoesNotContain("‮", rows[1].SafeFileName, StringComparison.Ordinal);
    }

    // Case 7: no mail, no rows; a mail without attachments, no rows.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Without_a_mail_or_without_attachments_there_are_no_rows()
    {
        var empty = new ShellHarness();
        _harnesses.Add(empty);
        Assert.Empty(empty.Shell.Reading.Attachments);

        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox);

        Assert.Empty(harness.Shell.Reading.Attachments);
    }

    // Case 7 / A12: the rows change only with the shown mail.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Showing_another_mail_replaces_the_rows()
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, new MailAttachmentInfo("first_a.pdf", "application/pdf", 10));
        Account account = (await harness.ReadModel.ListAccountsAsync()).Single();
        MailFolder folder = (await harness.ReadModel.ListFoldersAsync(account.Id)).Single();
        MailMessage second = harness.ReadModel.AddMessage(folder, "second_mail", hasAttachments: true);
        harness.ReadModel.SetContent(
            second.Id,
            TestContent.Plain("second_mail", "body", [new MailAttachmentInfo("second_b.docx", "application/x", 30), new MailAttachmentInfo("second_c.png", "image/png", 40)]));

        await harness.Shell.Reading.ShowAsync(second);

        Assert.Equal(new[] { "second_b.docx", "second_c.png" }, harness.Shell.Reading.Attachments.Select(row => row.Name).ToArray());
        Assert.Equal(new[] { 0, 1 }, harness.Shell.Reading.Attachments.Select(row => row.Index).ToArray());
    }

    // ---- A4, A5, A7: risk from the safe name, never from the claimed type ------------------------------------

    // A7: the claimed MIME type decides nothing, only the extension.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_claimed_media_type_does_not_change_the_risk()
    {
        (ShellHarness harness, _) = await ShowAsync(
            FolderRole.Inbox,
            new MailAttachmentInfo("setup_a.exe", "image/png", 10),
            new MailAttachmentInfo("photo_b.jpg", "application/x-msdownload", 10),
            new MailAttachmentInfo("daten_c.xyz", "application/pdf", 10));

        IReadOnlyList<AttachmentRow> rows = harness.Shell.Reading.Attachments;

        Assert.Equal(AttachmentRisk.SaveOnly, rows[0].Risk);
        Assert.False(rows[0].CanOpen);
        Assert.Equal(AttachmentRisk.Harmless, rows[1].Risk);
        Assert.True(rows[1].CanOpen);
        Assert.Equal(AttachmentRisk.Ask, rows[2].Risk);
        Assert.True(rows[2].CanOpen);
    }

    // A4 / A5 / case 17: SafeFileName is the A4 name, the risk is its classification, CanOpen is false only for SaveOnly.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Row_fields_follow_the_safe_name_and_its_classification()
    {
        MailAttachmentInfo[] infos =
        [
            new("..\\..\\evil_a.exe", "application/octet-stream", 1),
            new("rechnung.pdf.exe", "application/pdf", 1),
            new("ok_c.PDF", "application/pdf", 1),
            new(string.Empty, "application/octet-stream", 1),
        ];
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, infos);

        IReadOnlyList<AttachmentRow> rows = harness.Shell.Reading.Attachments;

        Assert.Equal(infos.Length, rows.Count);
        for (int position = 0; position < rows.Count; position++)
        {
            AttachmentRow row = rows[position];
            Assert.Equal(AttachmentFileName.ToSafe(infos[position].FileName, position), row.SafeFileName);
            Assert.Equal(AttachmentPolicy.Classify(row.SafeFileName), row.Risk);
            Assert.Equal(row.Risk != AttachmentRisk.SaveOnly, row.CanOpen);
            Assert.True(row.HasContent);
        }

        Assert.Equal(AttachmentRisk.SaveOnly, rows[0].Risk);
        Assert.Equal(AttachmentRisk.SaveOnly, rows[1].Risk);
        Assert.Equal(AttachmentRisk.Harmless, rows[2].Risk);
        Assert.Equal("anhang-4", rows[3].SafeFileName);
    }

    // A10-free control: the role of the folder does not change the row, only the question later.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Junk)]
    [InlineData(FolderRole.Quarantine)]
    [InlineData(FolderRole.Screener)]
    [InlineData(null)]
    public async Task The_folder_role_does_not_change_the_rows(FolderRole? role)
    {
        (ShellHarness harness, _) = await ShowAsync(role, new MailAttachmentInfo("report_a.pdf", "application/pdf", 10));

        AttachmentRow row = Assert.Single(harness.Shell.Reading.Attachments);
        Assert.Equal(AttachmentRisk.Harmless, row.Risk);
        Assert.True(row.CanOpen);
        Assert.True(row.HasContent);
    }

    // ---- A11 -----------------------------------------------------------------------------------------------

    // A11: an attached message is saved or opened as .eml, level Ask. Named and unnamed variants.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("forwarded_a")]
    [InlineData("")]
    [InlineData("forwarded_b.eml")]
    public async Task An_attached_message_becomes_an_eml_file_that_asks_first(string name)
    {
        (ShellHarness harness, _) = await ShowAsync(FolderRole.Inbox, new MailAttachmentInfo(name, "message/rfc822", 300));

        AttachmentRow row = Assert.Single(harness.Shell.Reading.Attachments);

        Assert.EndsWith(".eml", row.SafeFileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AttachmentRisk.Ask, row.Risk);
        Assert.True(row.CanOpen);
        Assert.True(row.HasContent);
    }

    // A11: a message below the depth limit is not a row at all (the reader flags it as opaque instead).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_message_below_the_depth_limit_has_no_row()
    {
        using var stream = new MemoryStream(DeepEml.NestedRfc822(100));
        MailContent content = await MailContentReader.ReadAsync(stream);
        Assert.True(content.HasOpaqueParts);
        var harness = new ShellHarness();
        _harnesses.Add(harness);
        Account account = harness.ReadModel.AddAccount("Account_A");
        MailFolder inbox = harness.ReadModel.AddFolder(account, FolderRole.Inbox, "INBOX");
        MailMessage message = harness.ReadModel.AddMessage(inbox, "deep_mail", hasAttachments: true);
        harness.ReadModel.SetContent(message.Id, content);

        await harness.Shell.Reading.ShowAsync(message);

        Assert.Empty(harness.Shell.Reading.Attachments);
    }

    private static byte[] TinyPngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}

/// <summary>Raw EML with extreme nesting, built as text in a loop.</summary>
internal static class DeepEml
{
    private const string Headers =
        "From: Sender A <sender_a@example.test>\r\n" +
        "To: Recipient B <recipient_b@example.test>\r\n" +
        "Subject: deep_subject\r\n" +
        "Date: Mon, 15 Jun 2026 10:00:00 +0200\r\n" +
        "MIME-Version: 1.0\r\n";

    /// <summary><paramref name="depth"/> nested <c>message/rfc822</c> parts; the innermost message is plain text.</summary>
    public static byte[] NestedRfc822(int depth)
    {
        string inner = Headers + "Content-Type: text/plain\r\n\r\ndeep_marker\r\n";
        for (int level = 0; level < depth; level++)
        {
            inner = Headers + "Content-Type: message/rfc822\r\n\r\n" + inner;
        }

        return System.Text.Encoding.ASCII.GetBytes(inner);
    }
}
