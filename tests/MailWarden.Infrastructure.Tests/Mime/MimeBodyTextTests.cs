using MailWarden.Infrastructure.Mime;
using MimeKit;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>AP-03a case 25: <see cref="MimeBodyText.Extract"/>.</summary>
[Trait("Category", "feature:sync")]
public sealed class MimeBodyTextTests
{
    private static MimeMessage PlainMessage(string text) =>
        new() { Body = new TextPart("plain") { Text = text } };

    private static MimeMessage HtmlMessage(string html) =>
        new() { Body = new TextPart("html") { Text = html } };

    private static MimeMessage AlternativeMessage(string text, string html)
    {
        var builder = new BodyBuilder { TextBody = text, HtmlBody = html };
        return new MimeMessage { Body = builder.ToMessageBody() };
    }

    // Case 25
    [Fact]
    public void Plain_text_body_is_returned()
    {
        Assert.Equal("Hello plain world", MimeBodyText.Extract(PlainMessage("Hello plain world"), 1000));
    }

    // Case 25
    [Fact]
    public void Whitespace_runs_collapse_to_one_space_and_the_result_is_trimmed()
    {
        string result = MimeBodyText.Extract(PlainMessage("  alpha  \r\n\r\n beta\t\tgamma \r\n"), 1000);

        Assert.Equal("alpha beta gamma", result);
    }

    // Case 25
    [Fact]
    public void Text_body_wins_over_html_body_when_it_has_content()
    {
        string result = MimeBodyText.Extract(AlternativeMessage("plain version wins", "<p>html version loses</p>"), 1000);

        Assert.Equal("plain version wins", result);
    }

    // Case 25
    [Fact]
    public void Whitespace_only_text_body_falls_back_to_the_html_body()
    {
        string result = MimeBodyText.Extract(AlternativeMessage("   \r\n  ", "<p>html fallback text</p>"), 1000);

        Assert.Equal("html fallback text", result);
    }

    // Case 25
    [Fact]
    public void Html_only_message_has_tags_removed_scripts_and_styles_dropped_and_entities_decoded()
    {
        const string html =
            "<html><head><style>p { color: red; }</style></head><body>" +
            "<script>alert('scriptcontent');</script><p>Tom &amp; Jerry</p></body></html>";

        string result = MimeBodyText.Extract(HtmlMessage(html), 1000);

        Assert.Equal("Tom & Jerry", result);
    }

    // Case 25: entities are decoded after the tags are removed
    [Fact]
    public void Escaped_angle_brackets_stay_as_literal_text()
    {
        string result = MimeBodyText.Extract(HtmlMessage("<p>use &lt;b&gt; for bold</p>"), 1000);

        Assert.Equal("use <b> for bold", result);
    }

    // Case 25: separated blocks keep their words apart
    [Fact]
    public void Words_of_separate_html_blocks_do_not_run_together()
    {
        string result = MimeBodyText.Extract(HtmlMessage("<div>first</div>\r\n<div>second</div>"), 1000);

        Assert.Equal("first second", result);
    }

    // Case 25
    [Fact]
    public void Message_without_body_yields_empty_string()
    {
        Assert.Equal(string.Empty, MimeBodyText.Extract(new MimeMessage(), 1000));
        Assert.Equal(string.Empty, MimeBodyText.Extract(PlainMessage(string.Empty), 1000));
        Assert.Equal(string.Empty, MimeBodyText.Extract(HtmlMessage("<script>only();</script>"), 1000));
    }

    // Case 25
    [Fact]
    public void Result_is_cut_to_max_chars()
    {
        Assert.Equal("abcde", MimeBodyText.Extract(PlainMessage("abcdefghij"), 5));
        Assert.Equal("abcdefghij", MimeBodyText.Extract(PlainMessage("abcdefghij"), 10));
        Assert.Equal("abcdefghij", MimeBodyText.Extract(PlainMessage("abcdefghij"), 11));
    }

    // Case 25: the sync uses 200 for the preview and 100000 for the index
    [Fact]
    public void Long_body_is_cut_to_exactly_the_requested_length()
    {
        MimeMessage message = PlainMessage(new string('x', 150_000));

        Assert.Equal(200, MimeBodyText.Extract(message, 200).Length);
        Assert.Equal(100_000, MimeBodyText.Extract(message, 100_000).Length);
    }

    // Case 25
    [Fact]
    public void Result_never_exceeds_max_chars_for_spaced_text()
    {
        string result = MimeBodyText.Extract(PlainMessage(string.Concat(Enumerable.Repeat("ab    ", 100))), 7);

        Assert.True(result.Length <= 7);
        Assert.StartsWith("ab ab", result, StringComparison.Ordinal);
    }

    // Case 25
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Max_chars_of_zero_or_less_throws_ArgumentOutOfRangeException(int maxChars)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MimeBodyText.Extract(PlainMessage("text"), maxChars));
    }

    // Case 25
    [Fact]
    public void Non_ascii_text_is_kept()
    {
        Assert.Equal("Grüße äöü", MimeBodyText.Extract(PlainMessage("Grüße äöü"), 100));
    }
}
