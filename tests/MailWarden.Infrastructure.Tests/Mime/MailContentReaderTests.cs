using MailWarden.Core.Text;
using MailWarden.Infrastructure.Mime;
using MimeKit;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>AP-04a cases 2-8 (D2): <see cref="MailContentReader"/>.</summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class MailContentReaderTests
{
    private const long OneMebibyte = 1024 * 1024;

    private static async Task<MailContent> ReadAsync(MimeMessage message)
    {
        using MemoryStream stream = ReadingEml.ToStream(message);
        return await MailContentReader.ReadAsync(stream);
    }

    private static byte[] PngOfSize(long length)
    {
        // A valid PNG header followed by filler: the reader must decide by media type and size, not by decoding.
        byte[] bytes = new byte[length];
        ReadingEml.TinyPng.CopyTo(bytes, 0);
        return bytes;
    }

    private static MimeMessage WithLinkedResources(params (string FileName, string MediaType, byte[] Content, string ContentId)[] resources)
    {
        MimeMessage message = ReadingEml.NewMessage();
        var builder = new BodyBuilder { TextBody = "text part", HtmlBody = "<p>html part</p>" };
        foreach ((string fileName, string mediaType, byte[] content, string contentId) in resources)
        {
            MimeEntity entity = builder.LinkedResources.Add(fileName, content, ContentType.Parse(mediaType));
            entity.ContentId = contentId;
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    // Case 2
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Multipart_alternative_fills_html_plain_text_and_headers()
    {
        MimeMessage message = ReadingEml.NewMessage("subject_alternative");
        message.Body = new BodyBuilder { TextBody = "plain_marker_text", HtmlBody = "<p>html_marker_text</p>" }.ToMessageBody();

        MailContent content = await ReadAsync(message);

        Assert.Equal("subject_alternative", content.Subject);
        Assert.Equal("Sender A <sender_a@example.test>", content.From);
        Assert.Equal(new[] { "Recipient B <recipient_b@example.test>", "recipient_c@example.test" }, content.To.ToArray());
        Assert.Equal(new[] { "Copy D <copy_d@example.test>" }, content.Cc.ToArray());
        Assert.Equal(ReadingEml.MessageDate.UtcDateTime, content.Date?.UtcDateTime);
        Assert.NotNull(content.Html);
        Assert.Contains("html_marker_text", content.Html, StringComparison.Ordinal);
        Assert.Contains("plain_marker_text", content.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("html_marker_text", content.PlainText, StringComparison.Ordinal);
        Assert.Empty(content.Attachments);
        Assert.Empty(content.InlineImages);
    }

    // Case 2: the reader does not own the stream.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Stream_is_not_disposed_and_content_is_usable_after_the_stream_is_gone()
    {
        MimeMessage message = ReadingEml.NewMessage();
        message.Body = new BodyBuilder { TextBody = "stream_marker_text" }.ToMessageBody();
        MemoryStream stream = ReadingEml.ToStream(message);

        MailContent content = await MailContentReader.ReadAsync(stream);

        Assert.True(stream.CanRead);
        await stream.DisposeAsync();
        Assert.Contains("stream_marker_text", content.PlainText, StringComparison.Ordinal);
    }

    // Case 2: headers are cleaned with D1 (bidi control in a display name).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task From_is_cleaned_with_the_display_text_rule()
    {
        MimeMessage message = ReadingEml.NewMessage();
        message.From.Clear();
        message.From.Add(new MailboxAddress("Sender\u202E A", "sender_a@example.test"));
        message.Body = new TextPart("plain") { Text = "body" };

        MailContent content = await ReadAsync(message);

        Assert.Equal(SafeDisplayText.Clean(content.From), content.From);
        Assert.DoesNotContain("\u202E", content.From, StringComparison.Ordinal);
        Assert.Contains("sender_a@example.test", content.From, StringComparison.Ordinal);
    }

    // Case 3
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Text_only_message_has_no_html()
    {
        MimeMessage message = ReadingEml.NewMessage();
        message.Body = new TextPart("plain") { Text = "only_plain_marker" };

        MailContent content = await ReadAsync(message);

        Assert.Null(content.Html);
        Assert.Contains("only_plain_marker", content.PlainText, StringComparison.Ordinal);
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_only_message_derives_the_plain_text_from_the_html()
    {
        MimeMessage message = ReadingEml.NewMessage();
        message.Body = new TextPart("html") { Text = "<html><body><p>html_only_marker</p><script>script_marker()</script></body></html>" };

        MailContent content = await ReadAsync(message);

        Assert.NotNull(content.Html);
        Assert.Contains("html_only_marker", content.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", content.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("script_marker", content.PlainText, StringComparison.Ordinal);
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Inline_png_with_content_id_becomes_a_data_uri_and_is_not_an_attachment()
    {
        MimeMessage message = WithLinkedResources(("logo.png", "image/png", ReadingEml.TinyPng, "logo_1@example.test"));

        MailContent content = await ReadAsync(message);

        string dataUri = Assert.Contains("logo_1@example.test", content.InlineImages);
        Assert.StartsWith("data:image/png;base64,", dataUri, StringComparison.Ordinal);
        Assert.Equal(ReadingEml.TinyPng, Convert.FromBase64String(dataUri["data:image/png;base64,".Length..]));
        Assert.Empty(content.Attachments);
    }

    // Case 5
    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public async Task Other_allowed_image_types_are_inlined(string mediaType)
    {
        MimeMessage message = WithLinkedResources(("picture.bin", mediaType, ReadingEml.TinyPng, "pic_1@example.test"));

        MailContent content = await ReadAsync(message);

        string dataUri = Assert.Contains("pic_1@example.test", content.InlineImages);
        Assert.StartsWith($"data:{mediaType};base64,", dataUri, StringComparison.Ordinal);
        Assert.Empty(content.Attachments);
    }

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Inline_svg_is_never_inlined_but_listed_as_attachment()
    {
        byte[] svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>x()</script></svg>"u8.ToArray();
        MimeMessage message = WithLinkedResources(
            ("drawing.svg", "image/svg+xml", svg, "svg_1@example.test"),
            ("logo.png", "image/png", ReadingEml.TinyPng, "logo_1@example.test"));

        MailContent content = await ReadAsync(message);

        Assert.DoesNotContain("svg_1@example.test", content.InlineImages.Keys);
        Assert.Contains("logo_1@example.test", content.InlineImages.Keys);
        Assert.DoesNotContain(content.InlineImages.Values, value => value.Contains("svg", StringComparison.OrdinalIgnoreCase));
        MailAttachmentInfo attachment = Assert.Single(content.Attachments);
        Assert.Equal("drawing.svg", attachment.FileName);
        Assert.Equal("image/svg+xml", attachment.ContentType);
    }

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Image_over_five_megabytes_is_skipped_and_listed_as_attachment()
    {
        MimeMessage message = WithLinkedResources(
            ("huge.png", "image/png", PngOfSize((5 * OneMebibyte) + 1), "huge_1@example.test"),
            ("small.png", "image/png", ReadingEml.TinyPng, "small_1@example.test"));

        MailContent content = await ReadAsync(message);

        Assert.DoesNotContain("huge_1@example.test", content.InlineImages.Keys);
        Assert.Contains("small_1@example.test", content.InlineImages.Keys);
        MailAttachmentInfo attachment = Assert.Single(content.Attachments);
        Assert.Equal("huge.png", attachment.FileName);
    }

    // Case 6: six images of 4.5 MB (27 MB together) are each under 5 MB; the 25 MB total rule must stop the sixth.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Images_together_over_twenty_five_megabytes_are_cut_off_and_the_rest_is_listed_as_attachment()
    {
        byte[] image = PngOfSize(4_500_000);
        var resources = Enumerable.Range(1, 6)
            .Select(number => ($"photo_{number}.png", "image/png", image, $"photo_{number}@example.test"))
            .ToArray();
        MimeMessage message = WithLinkedResources(resources);

        MailContent content = await ReadAsync(message);

        Assert.Equal(5, content.InlineImages.Count);
        MailAttachmentInfo skipped = Assert.Single(content.Attachments);
        Assert.StartsWith("photo_", skipped.FileName, StringComparison.Ordinal);
    }

    // Case 7
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Attachment_file_name_with_path_and_bidi_characters_is_cleaned_and_never_used_as_a_path()
    {
        const string rawName = "..\\..\\evil_dir\u202Etxt.exe";
        var part = new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream("attachment_bytes"u8.ToArray())),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = rawName },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        MimeMessage message = ReadingEml.NewMessage();
        message.Body = new Multipart("mixed") { new TextPart("plain") { Text = "body" }, part };
        string escapeTarget = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "evil_dir"));

        MailContent content = await ReadAsync(message);

        MailAttachmentInfo attachment = Assert.Single(content.Attachments);
        Assert.DoesNotContain("\u202E", attachment.FileName, StringComparison.Ordinal);
        Assert.Equal(SafeDisplayText.Clean(attachment.FileName), attachment.FileName);
        Assert.Contains("evil_dir", attachment.FileName, StringComparison.Ordinal);
        Assert.False(Directory.Exists(escapeTarget));
        Assert.False(File.Exists(escapeTarget));
    }

    // Case 7
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Attachment_without_a_name_gets_an_empty_file_name_and_a_named_attachment_keeps_its_data()
    {
        var unnamed = new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream(new byte[10])),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        var named = new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream(new byte[20])),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "report_a.pdf" },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        MimeMessage message = ReadingEml.NewMessage();
        message.Body = new Multipart("mixed") { new TextPart("plain") { Text = "body" }, unnamed, named };

        MailContent content = await ReadAsync(message);

        Assert.Equal(2, content.Attachments.Count);
        MailAttachmentInfo noName = Assert.Single(content.Attachments, attachment => attachment.FileName.Length == 0);
        Assert.Equal("application/octet-stream", noName.ContentType);
        MailAttachmentInfo report = Assert.Single(content.Attachments, attachment => attachment.FileName == "report_a.pdf");
        Assert.Equal("application/pdf", report.ContentType);
        Assert.True(report.Size is null or 20);
    }

    // Case 8
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_stream_throws_MailContentException()
    {
        using var empty = new MemoryStream();

        await Assert.ThrowsAsync<MailContentException>(() => MailContentReader.ReadAsync(empty));
    }

    // Case 8
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cancelled_token_throws_OperationCanceledException()
    {
        MimeMessage message = ReadingEml.NewMessage();
        message.Body = new TextPart("plain") { Text = "body" };
        using MemoryStream stream = ReadingEml.ToStream(message);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MailContentReader.ReadAsync(stream, cancelled.Token));
    }
}
