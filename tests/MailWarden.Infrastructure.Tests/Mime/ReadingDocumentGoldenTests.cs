using System.Security.Cryptography;
using System.Text;
using MailWarden.Infrastructure.Mime;
using MimeKit;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>
/// AP-04e case 12 (E8): the Light and Dark reading documents are byte-identical to the output of <c>main</c> before AP-04e.
/// The SHA-256 values come from <c>work\AP-04e\golden-main.md</c> (upper-case hex over the UTF-8 bytes of <c>Html</c>).
/// Also covers the fail-fast case for an unknown <see cref="ReadingTheme"/>.
/// </summary>
[Trait("Category", "feature:ui")]
public sealed class ReadingDocumentGoldenTests
{
    private const string PlainText = "Hello <world>\nLine 2 & more";
    private const string HtmlWithoutColors =
        "<p>Hallo <a href=\"https://links.example.test/a\">Link</a> <mark><a href=\"https://links.example.test/b\">Mark</a></mark></p>";
    private const string HtmlWithColors = "<p style=\"color:#000\">Text</p><img src=\"https://images.example.test/a.png\">";

    // Mail with the golden headers (no Cc, no Date) built through the real reader; html and plain text are set directly.
    private static MailContent ContentOf(string mail)
    {
        var message = new MimeMessage { Subject = "Subject" };
        message.From.Add(new MailboxAddress(string.Empty, "sender_a@example.test"));
        message.To.Add(new MailboxAddress(string.Empty, "recipient_a@example.test"));
        message.Body = new TextPart("plain") { Text = "reader text" };
        using MemoryStream stream = ReadingEml.ToStream(message);
        MailContent read = MailContentReader.ReadAsync(stream).GetAwaiter().GetResult();
        return mail switch
        {
            "plain" => read with { Html = null, PlainText = PlainText },
            "html-without-colors" => read with { Html = HtmlWithoutColors, PlainText = "Hallo" },
            "html-with-colors" => read with { Html = HtmlWithColors, PlainText = "Text" },
            _ => throw new ArgumentOutOfRangeException(nameof(mail)),
        };
    }

    private static string Sha256(ReadingDocument document) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.Html)));

    // allowRemoteImages: true/false = Build; null = BuildFromPlainText.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("plain", ReadingTheme.Light, false, "0E84BED217D164B44E9D567BE7D8C5D9D154445B342845401EE6667FE0E82C1D")]
    [InlineData("plain", ReadingTheme.Light, true, "0E84BED217D164B44E9D567BE7D8C5D9D154445B342845401EE6667FE0E82C1D")]
    [InlineData("plain", ReadingTheme.Dark, false, "38FC44AA9CCBC5595BAE5C79F4E6362BCE8237E330C5E2D9913C83B7ED8125B4")]
    [InlineData("plain", ReadingTheme.Dark, true, "38FC44AA9CCBC5595BAE5C79F4E6362BCE8237E330C5E2D9913C83B7ED8125B4")]
    [InlineData("plain", ReadingTheme.Light, null, "0E84BED217D164B44E9D567BE7D8C5D9D154445B342845401EE6667FE0E82C1D")]
    [InlineData("plain", ReadingTheme.Dark, null, "38FC44AA9CCBC5595BAE5C79F4E6362BCE8237E330C5E2D9913C83B7ED8125B4")]
    [InlineData("html-without-colors", ReadingTheme.Light, false, "D3FB9A1CF7B2400D4E2226AF44B9D10E84D3F06B04D4E55217C68CBC5F36DC86")]
    [InlineData("html-without-colors", ReadingTheme.Light, true, "4C627454B531FD78A7722DF937A686FBD81FA997D9FE624B836837CC0642B66C")]
    [InlineData("html-without-colors", ReadingTheme.Dark, false, "609BFA710CA52513C8D7098BF70551AE34C77F895938C73E7DFD4801148C0448")]
    [InlineData("html-without-colors", ReadingTheme.Dark, true, "A90C43E14694D6A867C6192500E657B29B12CBDE9DC0ED3B28583923853FD14D")]
    [InlineData("html-without-colors", ReadingTheme.Light, null, "A2E55612D599CA318C4CAF5C36D76F611087ED94E18D6B5984C9D6A8C5F9EA02")]
    [InlineData("html-without-colors", ReadingTheme.Dark, null, "34F35317FF4C3F0198A9C408E8E04FB839D9FEB5626B9114364110EA13D4A67B")]
    [InlineData("html-with-colors", ReadingTheme.Light, false, "7B9621B1AC48F207C74472B7B57034C17F3A683BF8EFBA3DBDBDD9059C8A8F08")]
    [InlineData("html-with-colors", ReadingTheme.Light, true, "7DB74459E60CE29A069F80C97841F0737199060F9DFF5DE894A8D50E6E1A8ED2")]
    [InlineData("html-with-colors", ReadingTheme.Dark, false, "88575697597701E5171C9F589B53EF5987C5E25EA996931E5E942B849B58BC88")]
    [InlineData("html-with-colors", ReadingTheme.Dark, true, "7853E3A86A257332812D022D4B05DD9C3DB71573F9811303AD028A595589AF1E")]
    [InlineData("html-with-colors", ReadingTheme.Light, null, "319C242E1BBB089BF39EAB274B83AFF15FCEAB7BF00713793B6879E35A27F0B7")]
    [InlineData("html-with-colors", ReadingTheme.Dark, null, "5BDD5BC469507D06330CEBBAC52A292294A04C01F8743548E4DC428235538A95")]
    public async Task Light_and_dark_documents_are_byte_identical_to_main(string mail, ReadingTheme theme, bool? allowRemoteImages, string expectedSha256)
    {
        await Task.Yield();
        MailContent content = ContentOf(mail);

        ReadingDocument document = allowRemoteImages is bool allow
            ? ReadingDocumentBuilder.Build(content, allow, allowEmbeddedImages: true, theme)
            : ReadingDocumentBuilder.BuildFromPlainText(content, theme);

        Assert.Equal(expectedSha256, Sha256(document));
    }

    // Fail fast: an undefined theme value is rejected by every entry point.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unknown_reading_theme_throws_ArgumentOutOfRangeException()
    {
        await Task.Yield();
        const ReadingTheme unknown = (ReadingTheme)99;
        MailContent content = ContentOf("html-without-colors");

        Assert.Throws<ArgumentOutOfRangeException>(() => ReadingDocumentBuilder.Build(content, false, true, unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadingDocumentBuilder.BuildFromPlainText(content, unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadingDocumentBuilder.PageBackground(unknown));
    }
}
