using System.Text;
using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>
/// AP-07c fix.4 J6 / case 55: deeply nested MIME structures must not end the process (stack overflow); the reader
/// still returns the top-level headers. A crash here takes the whole test run down, which is the failure signal.
/// </summary>
[Trait("Category", "feature:classifier")]
public sealed class NestedMimeReaderTests
{
    private const string TopReceived = "from top.example.test by mx.example.test id 55";

    private const string TopHeaders =
        "Received: " + TopReceived + "\r\n" +
        "MIME-Version: 1.0\r\n" +
        "From: Sender A <sender_a@example.test>\r\n" +
        "To: recipient_a@example.test\r\n" +
        "Subject: sample_subject\r\n" +
        "Date: Mon, 15 Jun 2026 10:00:00 +0200\r\n" +
        "Message-ID: <nested@example.test>\r\n";

    private static byte[] NestedMultipart(int depth)
    {
        var text = new StringBuilder(TopHeaders);
        text.Append("Content-Type: multipart/mixed; boundary=\"b0\"\r\n\r\n");
        for (int level = 0; level < depth; level++)
        {
            text.Append("--b").Append(level).Append("\r\nContent-Type: multipart/mixed; boundary=\"b").Append(level + 1).Append("\"\r\n\r\n");
        }

        text.Append("--b").Append(depth).Append("\r\nContent-Type: text/plain\r\n\r\ninnermost text\r\n--b").Append(depth).Append("--\r\n");
        for (int level = depth - 1; level >= 0; level--)
        {
            text.Append("--b").Append(level).Append("--\r\n");
        }

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static byte[] NestedMessages(int depth)
    {
        var text = new StringBuilder(TopHeaders);
        text.Append("Content-Type: message/rfc822\r\n\r\n");
        for (int level = 0; level < depth; level++)
        {
            text.Append("From: inner_a@example.test\r\nSubject: inner\r\nContent-Type: message/rfc822\r\n\r\n");
        }

        text.Append("From: inner_a@example.test\r\nSubject: innermost\r\nContent-Type: text/plain\r\n\r\ninnermost text\r\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    public static TheoryData<string, int> NestedMessagesData() => new()
    {
        { "multipart", 1023 },
        { "multipart", 5000 },
        { "rfc822", 1023 },
        { "rfc822", 5000 },
    };

    private static byte[] Build(string kind, int depth) => kind == "multipart" ? NestedMultipart(depth) : NestedMessages(depth);

    // Case 55
    [Theory(Timeout = 60_000)]
    [MemberData(nameof(NestedMessagesData))]
    public async Task Deeply_nested_message_is_read_without_ending_the_process(string kind, int depth)
    {
        using var stream = new MemoryStream(Build(kind, depth));

        ClassificationMessage message = await ClassificationInputReader.ReadMessageAsync(stream);

        Assert.True(message.Parsed);
        Assert.Equal(TopReceived, message.TopReceived);
        Assert.NotNull(message.Input);
        Assert.Equal("sender_a@example.test", message.Input.FromAddress);
        Assert.Equal("Sender A", message.Input.FromName);
    }

    // Case 55
    [Theory(Timeout = 60_000)]
    [MemberData(nameof(NestedMessagesData))]
    public async Task Headers_only_reader_survives_deep_nesting(string kind, int depth)
    {
        using var stream = new MemoryStream(Build(kind, depth));

        string? received = await ClassificationInputReader.ReadTopReceivedAsync(stream);

        Assert.Equal(TopReceived, received);
    }

    // Case 55, positive control: the same structure with a few levels reads the same values
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("multipart")]
    [InlineData("rfc822")]
    public async Task Shallow_nesting_reads_the_same_values(string kind)
    {
        using var stream = new MemoryStream(Build(kind, 3));

        ClassificationMessage message = await ClassificationInputReader.ReadMessageAsync(stream);

        Assert.True(message.Parsed);
        Assert.Equal(TopReceived, message.TopReceived);
        Assert.Equal("sender_a@example.test", message.Input!.FromAddress);
    }
}
