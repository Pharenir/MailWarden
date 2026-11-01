using System.Text;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>AP-04a cases 87 and 88 (M6, M7): which parts count as opaque and which are ordinary attachments.</summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class OpaqueMultipartTests
{
    private const string Headers =
        "From: Sender A <sender_a@example.test>\r\n" +
        "To: Recipient B <recipient_b@example.test>\r\n" +
        "Subject: opaque_subject\r\n" +
        "Date: Mon, 15 Jun 2026 10:00:00 +0200\r\n" +
        "Message-ID: <opaque@example.test>\r\n" +
        "MIME-Version: 1.0\r\n";

    private static async Task<MailContent> ReadAsync(string message)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(message));
        return await MailContentReader.ReadAsync(stream);
    }

    // Case 87 (M6)
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("multipart/mixed")]
    [InlineData("multipart/mixed; boundary=\"never_occurs\"")]
    [InlineData("multipart/alternative")]
    public async Task Multipart_with_text_but_without_usable_boundary_is_opaque(string contentType)
    {
        MailContent content = await ReadAsync(Headers + $"Content-Type: {contentType}\r\n\r\ntext without any part\r\nmore text\r\n");

        Assert.True(content.HasOpaqueParts);
        Assert.Equal("opaque_subject", content.Subject);
        Assert.Empty(content.Attachments);
    }

    // Case 87 positive control: a multipart with real parts is not opaque.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Multipart_with_real_parts_is_not_opaque()
    {
        MailContent content = await ReadAsync(
            Headers +
            "Content-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n" +
            "--b1\r\nContent-Type: text/plain\r\n\r\nreal_part_text\r\n--b1--\r\n");

        Assert.False(content.HasOpaqueParts);
        Assert.Contains("real_part_text", content.PlainText, StringComparison.Ordinal);
    }

    // Case 87 positive control: a multipart without parts and without content is just empty, not opaque.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Multipart_without_parts_and_without_content_is_not_opaque()
    {
        MailContent content = await ReadAsync(Headers + "Content-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n");

        Assert.False(content.HasOpaqueParts);
    }

    // Case 87: a nested multipart without a boundary inside a normal mail is opaque, the normal text is kept.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Nested_multipart_without_boundary_inside_a_normal_mail_is_opaque_and_the_text_stays()
    {
        MailContent content = await ReadAsync(
            Headers +
            "Content-Type: multipart/mixed; boundary=\"outer\"\r\n\r\n" +
            "--outer\r\nContent-Type: text/plain\r\n\r\nharmless_text\r\n" +
            "--outer\r\nContent-Type: multipart/mixed\r\n\r\ninner text without parts\r\n" +
            "--outer--\r\n");

        Assert.True(content.HasOpaqueParts);
        Assert.Contains("harmless_text", content.PlainText, StringComparison.Ordinal);
        Assert.Empty(content.Attachments);
    }

    // Case 88 (M7): other message/* parts are ordinary attachments and no reason for a notice.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("message/feedback-report", "Feedback-Type: abuse\r\nUser-Agent: test\r\n")]
    [InlineData("message/delivery-status", "Reporting-MTA: dns; mail.example.test\r\n")]
    [InlineData("message/disposition-notification", "Reporting-UA: test\r\n")]
    public async Task Other_message_parts_are_listed_as_attachments_without_a_notice(string contentType, string body)
    {
        MailContent content = await ReadAsync(
            Headers +
            "Content-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n" +
            "--b1\r\nContent-Type: text/plain\r\n\r\nreport_text\r\n" +
            $"--b1\r\nContent-Type: {contentType}\r\n\r\n{body}" +
            "--b1--\r\n");

        Assert.False(content.HasOpaqueParts);
        MailAttachmentInfo attachment = Assert.Single(content.Attachments);
        Assert.Equal(contentType, attachment.ContentType);
        Assert.Contains("report_text", content.PlainText, StringComparison.Ordinal);
    }

    // Case 88 positive control: a real attachment next to it is listed too and nothing else is.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Feedback_report_and_a_named_attachment_are_both_listed()
    {
        MailContent content = await ReadAsync(
            Headers +
            "Content-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n" +
            "--b1\r\nContent-Type: text/plain\r\n\r\nreport_text\r\n" +
            "--b1\r\nContent-Type: message/feedback-report\r\n\r\nFeedback-Type: abuse\r\n" +
            "--b1\r\nContent-Type: application/pdf; name=\"report_a.pdf\"\r\nContent-Disposition: attachment; filename=\"report_a.pdf\"\r\n\r\npdfbytes\r\n" +
            "--b1--\r\n");

        Assert.False(content.HasOpaqueParts);
        Assert.Equal(2, content.Attachments.Count);
        Assert.Contains(content.Attachments, attachment => attachment.FileName == "report_a.pdf");
        Assert.Contains(content.Attachments, attachment => attachment.ContentType == "message/feedback-report");
    }
}
