using MailWarden.Infrastructure.Mime;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>AP-04a case 74 (L5): <see cref="MailContent.HasOpaqueParts"/> for bodies below the parser's depth limit.</summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class OpaquePartsTests
{
    private static async Task<MailContent> ReadAsync(byte[] eml)
    {
        using var stream = new MemoryStream(eml);
        return await MailContentReader.ReadAsync(stream);
    }

    // Case 74
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(40)]
    [InlineData(100)]
    [InlineData(1_023)]
    public async Task Multipart_below_the_depth_limit_is_flagged_and_not_listed_as_attachment(int depth)
    {
        MailContent content = await ReadAsync(DeepMimeEml.HarmlessTextThenNestedMultipart(depth));

        Assert.True(content.HasOpaqueParts);
        Assert.Contains(DeepMimeEml.HarmlessText, content.PlainText, StringComparison.Ordinal);
        Assert.Empty(content.Attachments);
    }

    // Case 74
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(40)]
    [InlineData(1_023)]
    public async Task Message_rfc822_below_the_depth_limit_is_flagged_and_not_listed_as_attachment(int depth)
    {
        MailContent content = await ReadAsync(DeepMimeEml.NestedRfc822(depth));

        Assert.True(content.HasOpaqueParts);
        Assert.Empty(content.Attachments);
        Assert.Equal(DeepMimeEml.Subject, content.Subject);
    }

    // Case 74 positive control (against false alarms): nesting well inside the limit is not opaque and is read fully.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task Shallow_multipart_is_not_flagged_and_its_text_is_read(int depth)
    {
        MailContent content = await ReadAsync(DeepMimeEml.HarmlessTextThenNestedMultipart(depth));

        Assert.False(content.HasOpaqueParts);
        Assert.Contains(DeepMimeEml.HarmlessText, content.PlainText, StringComparison.Ordinal);
        Assert.Empty(content.Attachments);
    }

    // Case 74 control: ordinary mails and real attachments are never flagged.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Ordinary_mail_with_an_attachment_is_not_flagged_and_keeps_its_attachment()
    {
        byte[] eml = SyncTestEml.WithAttachment("subject_a", "body_marker");

        MailContent content = await ReadAsync(eml);

        Assert.False(content.HasOpaqueParts);
        Assert.Equal("data.bin", Assert.Single(content.Attachments).FileName);
    }

    // Case 74 control: an ordinary attached message/rfc822 (shallow) is read as a normal part and is not opaque.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Shallow_message_rfc822_is_not_flagged()
    {
        MailContent content = await ReadAsync(DeepMimeEml.NestedRfc822(3));

        Assert.False(content.HasOpaqueParts);
    }
}
