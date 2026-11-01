using MimeKit;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>Builders for artificial EML messages used by the AP-04a reading tests. All content is synthetic.</summary>
internal static class ReadingEml
{
    /// <summary>Date header of every built message: 2026-06-15 10:00 +02:00.</summary>
    public static readonly DateTimeOffset MessageDate = new(2026, 6, 15, 10, 0, 0, TimeSpan.FromHours(2));

    /// <summary>1x1 transparent PNG (valid file).</summary>
    public static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    /// <summary>Creates a message with artificial headers and no body yet.</summary>
    public static MimeMessage NewMessage(string subject = "sample_subject")
    {
        var message = new MimeMessage { Subject = subject, Date = MessageDate };
        message.From.Add(new MailboxAddress("Sender A", "sender_a@example.test"));
        message.To.Add(new MailboxAddress("Recipient B", "recipient_b@example.test"));
        message.To.Add(new MailboxAddress(string.Empty, "recipient_c@example.test"));
        message.Cc.Add(new MailboxAddress("Copy D", "copy_d@example.test"));
        return message;
    }

    /// <summary>Serialises the message into a readable, seekable stream positioned at 0.</summary>
    public static MemoryStream ToStream(MimeMessage message)
    {
        var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        return stream;
    }

    /// <summary>Serialises the message into bytes.</summary>
    public static byte[] ToBytes(MimeMessage message)
    {
        using MemoryStream stream = ToStream(message);
        return stream.ToArray();
    }
}
