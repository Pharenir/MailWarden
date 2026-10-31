using System.Text;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>Builders for artificial raw RFC 5322 messages used as sync fixtures. All content is synthetic.</summary>
internal static class SyncTestEml
{
    /// <summary>Date header used by default: 2026-06-15 10:00 +02:00.</summary>
    public const string DefaultDateHeader = "Mon, 15 Jun 2026 10:00:00 +0200";

    public static readonly DateTimeOffset DefaultDate = new(2026, 6, 15, 10, 0, 0, TimeSpan.FromHours(2));

    /// <summary>A text/plain message. A null header argument omits that header.</summary>
    public static byte[] Plain(
        string? subject,
        string body,
        string? messageId = "<msg_default@example.test>",
        string? from = "Sender A <sender_a@example.test>",
        string? date = DefaultDateHeader) =>
        Build(
            Headers(subject, messageId, from, date, "Content-Type: text/plain; charset=utf-8"),
            body);

    /// <summary>A text/html-only message.</summary>
    public static byte[] HtmlOnly(
        string subject,
        string html,
        string? messageId = "<msg_html@example.test>",
        string? from = "Sender A <sender_a@example.test>",
        string? date = DefaultDateHeader) =>
        Build(
            Headers(subject, messageId, from, date, "Content-Type: text/html; charset=utf-8"),
            html);

    /// <summary>multipart/mixed with a text part and one small attachment.</summary>
    public static byte[] WithAttachment(
        string subject,
        string body,
        string? messageId = "<msg_attach@example.test>",
        string? from = "Sender A <sender_a@example.test>",
        string? date = DefaultDateHeader)
    {
        const string boundary = "sync_test_boundary";
        string multipartBody =
            $"--{boundary}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\n" +
            body + "\r\n" +
            $"--{boundary}\r\n" +
            "Content-Type: application/octet-stream; name=\"data.bin\"\r\n" +
            "Content-Disposition: attachment; filename=\"data.bin\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\n" +
            "AAECAw==\r\n" +
            $"--{boundary}--\r\n";
        return Build(
            Headers(subject, messageId, from, date, $"Content-Type: multipart/mixed; boundary=\"{boundary}\""),
            multipartBody);
    }

    /// <summary>Bytes that are not an RFC 5322 message (no header section at all).</summary>
    public static byte[] Garbage() => Encoding.ASCII.GetBytes("this is not an e-mail at all, just words\r\nand a second line without any header\r\n");

    private static string Headers(string? subject, string? messageId, string? from, string? date, string contentType)
    {
        var builder = new StringBuilder();
        builder.Append("MIME-Version: 1.0\r\n");
        if (from is not null)
        {
            builder.Append("From: ").Append(from).Append("\r\n");
        }

        builder.Append("To: recipient_a@example.test\r\n");
        if (subject is not null)
        {
            builder.Append("Subject: ").Append(subject).Append("\r\n");
        }

        if (date is not null)
        {
            builder.Append("Date: ").Append(date).Append("\r\n");
        }

        if (messageId is not null)
        {
            builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        }

        builder.Append(contentType).Append("\r\n");
        return builder.ToString();
    }

    private static byte[] Build(string headers, string body) => Encoding.UTF8.GetBytes(headers + "\r\n" + body);
}
