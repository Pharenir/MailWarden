using System.Text;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>The four classifier outcomes the pass fixtures are built to reach with the real default signals.</summary>
public enum MailKind
{
    /// <summary>Sender is the contact <see cref="ClassificationTestEml.ContactAddress"/>, DMARC/SPF/DKIM pass.</summary>
    Trusted,

    /// <summary>Unknown sender, everything passes (score -20): LikelyLegit.</summary>
    Legit,

    /// <summary>Unknown sender, SPF and DKIM fail for the plausible domain (score 40): LikelySpam.</summary>
    LikelySpam,

    /// <summary>Random .top domain, everything fails, domain not in the body (score 110): CertainSpam.</summary>
    CertainSpam,
}

/// <summary>Builders for artificial raw messages used by the classification tests. All content is synthetic.</summary>
internal static class ClassificationTestEml
{
    /// <summary>Plausible, non-freemail domain whose label appears in the body (same as the Core test inputs).</summary>
    public const string PlausibleDomain = "paypal.test";

    /// <summary>The address that the harness stores as the one contact; the Trusted fixture is sent by it.</summary>
    public const string ContactAddress = "sender_a@" + PlausibleDomain;

    public const string DateHeader = "Mon, 15 Jun 2026 10:00:00 +0200";

    private const string PlausibleBody = "Your paypal statement is ready.";

    private const string PassHeader =
        "mx.google.com; dkim=pass header.i=@paypal.test header.s=s1; spf=pass smtp.mailfrom=bounce@paypal.test; " +
        "dmarc=pass header.from=paypal.test";

    private const string PartialFailHeader =
        "mx.test; spf=fail smtp.mailfrom=bounce@paypal.test; dkim=fail header.d=paypal.test";

    private const string AllFailHeader =
        "mx.example.test; dmarc=fail header.from=qzxkvb73.top; spf=fail smtp.mailfrom=bounce@qzxkvb73.top; " +
        "dkim=fail header.d=qzxkvb73.top";

    /// <summary>Raw message for a fixture kind.</summary>
    public static byte[] For(MailKind kind, string? received = null) => kind switch
    {
        MailKind.Trusted => Build("Sender A <" + ContactAddress + ">", [PassHeader], PlausibleBody, received: received),
        MailKind.Legit => Build("Sender A <stranger_a@" + PlausibleDomain + ">", [PassHeader], PlausibleBody, received: received),
        MailKind.LikelySpam => Build("Sender A <stranger_b@" + PlausibleDomain + ">", [PartialFailHeader], PlausibleBody, received: received),
        MailKind.CertainSpam => Build("Offer <offer@qzxkvb73.top>", [AllFailHeader], "Claim your prize now.", received: received),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown mail kind."),
    };

    /// <summary>
    /// A text/plain message. A null <paramref name="from"/> omits the From header. Each authentication header is
    /// written as its own Authentication-Results header line, top-most first.
    /// </summary>
    public static byte[] Build(
        string? from,
        IReadOnlyList<string> authenticationResults,
        string body,
        string? replyTo = null,
        string subject = "sample_subject",
        string? date = DateHeader,
        string? messageId = "<msg_default@example.test>",
        string? received = null)
    {
        var builder = new StringBuilder();
        if (received is not null)
        {
            builder.Append("Received: ").Append(received).Append("\r\n");
        }

        builder.Append("MIME-Version: 1.0\r\n");
        foreach (string header in authenticationResults)
        {
            builder.Append("Authentication-Results: ").Append(header).Append("\r\n");
        }

        if (from is not null)
        {
            builder.Append("From: ").Append(from).Append("\r\n");
        }

        if (replyTo is not null)
        {
            builder.Append("Reply-To: ").Append(replyTo).Append("\r\n");
        }

        builder.Append("To: recipient_a@example.test\r\n");
        builder.Append("Subject: ").Append(subject).Append("\r\n");
        if (date is not null)
        {
            builder.Append("Date: ").Append(date).Append("\r\n");
        }

        if (messageId is not null)
        {
            builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        }

        builder.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
        builder.Append(body);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>A message with the given Content-Type header and body, no authentication headers, fixed sender.</summary>
    public static byte[] BuildWithContentType(string contentType, string body, string from = "Sender A <sender_a@example.test>")
    {
        string text =
            "MIME-Version: 1.0\r\n" +
            "From: " + from + "\r\n" +
            "To: recipient_a@example.test\r\n" +
            "Subject: sample_subject\r\n" +
            "Date: " + DateHeader + "\r\n" +
            "Message-ID: <msg_typed@example.test>\r\n" +
            "Content-Type: " + contentType + "\r\n\r\n" + body;
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>multipart/alternative with a text and an HTML part.</summary>
    public static byte[] Alternative(string text, string html)
    {
        const string boundary = "alt_test_boundary";
        string body =
            "--" + boundary + "\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" + text + "\r\n" +
            "--" + boundary + "\r\nContent-Type: text/html; charset=utf-8\r\n\r\n" + html + "\r\n" +
            "--" + boundary + "--\r\n";
        return BuildWithContentType("multipart/alternative; boundary=\"" + boundary + "\"", body);
    }
}
