using System.Text;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>
/// Builds raw EML bytes with extreme MIME nesting. Built as text in loops (no recursion), so building never
/// overflows the stack. All content is synthetic.
/// </summary>
internal static class DeepMimeEml
{
    public const string Subject = "deep_subject";

    public const string Marker = "deep_marker";

    private const string Headers =
        "From: Sender A <sender_a@example.test>\r\n" +
        "To: Recipient B <recipient_b@example.test>\r\n" +
        "Subject: " + Subject + "\r\n" +
        "Date: Mon, 15 Jun 2026 10:00:00 +0200\r\n" +
        "Message-ID: <deep@example.test>\r\n" +
        "MIME-Version: 1.0\r\n";

    /// <summary><paramref name="depth"/> nested <c>multipart/mixed</c> parts with one text part inside the innermost.</summary>
    public static byte[] NestedMultipart(int depth)
    {
        var text = new StringBuilder(Headers);
        text.Append("Content-Type: multipart/mixed; boundary=\"b0\"\r\n\r\n");
        for (int level = 0; level < depth; level++)
        {
            text.Append("--b").Append(level).Append("\r\n");
            if (level < depth - 1)
            {
                text.Append("Content-Type: multipart/mixed; boundary=\"b").Append(level + 1).Append("\"\r\n\r\n");
            }
            else
            {
                text.Append("Content-Type: text/plain\r\n\r\n").Append(Marker).Append("\r\n");
            }
        }

        for (int level = depth - 1; level >= 0; level--)
        {
            text.Append("--b").Append(level).Append("--\r\n");
        }

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary><paramref name="depth"/> nested <c>message/rfc822</c> parts; the innermost message is plain text.</summary>
    public static byte[] NestedRfc822(int depth)
    {
        string inner = Headers + "Content-Type: text/plain\r\n\r\n" + Marker + "\r\n";
        for (int level = 0; level < depth; level++)
        {
            inner = Headers + "Content-Type: message/rfc822\r\n\r\n" + inner;
        }

        return Encoding.ASCII.GetBytes(inner);
    }
}
