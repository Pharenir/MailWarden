using MailWarden.Infrastructure.Mime;
using MimeKit;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>
/// AP-04a cases 49 (reader part) and 50 (K2): deeply nested MIME must neither end the process nor lose the message.
/// Own class on purpose: a crash of the test host can be reproduced by running just this class.
/// </summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class DeepNestingReaderTests
{
    // Case 50
    [Fact]
    public void Parser_defaults_have_depth_32()
    {
        ParserOptions options = MimeParserDefaults.Create();

        Assert.Equal(32, options.MaxMimeDepth);
    }

    // Case 50
    [Fact]
    public void Parser_defaults_are_a_new_instance_on_every_call()
    {
        ParserOptions first = MimeParserDefaults.Create();
        ParserOptions second = MimeParserDefaults.Create();

        Assert.NotSame(first, second);
        first.MaxMimeDepth = 5;
        Assert.Equal(32, second.MaxMimeDepth);
        Assert.Equal(32, MimeParserDefaults.Create().MaxMimeDepth);
    }

    // Case 49
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(1_023)]
    [InlineData(5_000)]
    public async Task Nested_multipart_is_read_without_ending_the_process(int depth)
    {
        using var stream = new MemoryStream(DeepMimeEml.NestedMultipart(depth));

        MailContent content = await MailContentReader.ReadAsync(stream);

        Assert.Equal(DeepMimeEml.Subject, content.Subject);
        Assert.Contains("sender_a@example.test", content.From, StringComparison.Ordinal);
    }

    // Case 49
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(1_023)]
    [InlineData(5_000)]
    public async Task Nested_message_rfc822_is_read_without_ending_the_process(int depth)
    {
        using var stream = new MemoryStream(DeepMimeEml.NestedRfc822(depth));

        MailContent content = await MailContentReader.ReadAsync(stream);

        Assert.Equal(DeepMimeEml.Subject, content.Subject);
        Assert.Contains("sender_a@example.test", content.From, StringComparison.Ordinal);
    }

    // Case 49 positive control: nesting within the limit is still read completely.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Shallow_nesting_within_the_limit_keeps_its_text()
    {
        using var stream = new MemoryStream(DeepMimeEml.NestedMultipart(10));

        MailContent content = await MailContentReader.ReadAsync(stream);

        Assert.Equal(DeepMimeEml.Subject, content.Subject);
        Assert.Contains(DeepMimeEml.Marker, content.PlainText, StringComparison.Ordinal);
    }
}
