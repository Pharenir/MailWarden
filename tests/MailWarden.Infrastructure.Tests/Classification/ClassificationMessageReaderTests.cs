using System.Text;
using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-07c fix.2 G3, G4, G11 / cases 40, 41, 46: <see cref="ClassificationInputReader.ReadMessageAsync"/> and raw headers.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationMessageReaderTests
{
    private const string Contact = "sender_a@paypal.test";

    private static async Task<ClassificationMessage> ReadMessageAsync(byte[] eml)
    {
        using var stream = new MemoryStream(eml);
        return await ClassificationInputReader.ReadMessageAsync(stream);
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>Top header whose encoded word decodes to a forged ";dmarc=pass" clause, followed by a real dmarc=fail.</summary>
    private static string EncodedWordForgery() =>
        "mx.test; spf=pass smtp.mailfrom=bounce =?utf-8?B?" + Base64(";dmarc=pass header.from=paypal.test") + "?= ; dmarc=fail header.from=paypal.test";

    private static SpamClassifier ClassifierWithContact() =>
        new(ClassifierSettings.Default, new ContactSenderDirectory([Contact], []), SpamClassifier.CreateDefaultSignals());

    // ---- case 41 --------------------------------------------------------------------------------------------------

    // Case 41
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Normal_message_gives_parsed_input_and_normalised_top_received()
    {
        byte[] eml = ClassificationTestEml.Build(
            "Sender A <sender_a@example.test>",
            [],
            "Plain body.",
            received: "from top.example.test (top.example.test [192.0.2.1])\r\n\tby mx.example.test   with ESMTP id 1;   Mon, 15 Jun 2026 10:00:00 +0200");
        string text = Encoding.UTF8.GetString(eml).Replace("MIME-Version: 1.0\r\n", "Received: from lower.example.test by other.example.test\r\nMIME-Version: 1.0\r\n", StringComparison.Ordinal);

        ClassificationMessage message = await ReadMessageAsync(Encoding.UTF8.GetBytes(text));

        Assert.True(message.Parsed);
        Assert.Equal(
            "from top.example.test (top.example.test [192.0.2.1]) by mx.example.test with ESMTP id 1; Mon, 15 Jun 2026 10:00:00 +0200",
            message.TopReceived);
        Assert.NotNull(message.Input);
        Assert.Equal("sender_a@example.test", message.Input.FromAddress);
    }

    // Case 41
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Garbage_gives_not_parsed_without_exception()
    {
        ClassificationMessage message = await ReadMessageAsync(SyncTestEml.Garbage());

        Assert.False(message.Parsed);
        Assert.Null(message.Input);
        Assert.True(string.IsNullOrEmpty(message.TopReceived));
    }

    // Case 41
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Readable_message_without_from_is_parsed_with_null_input()
    {
        ClassificationMessage message = await ReadMessageAsync(
            ClassificationTestEml.Build(null, [], "Body.", received: "from top.example.test by mx.example.test id 7"));

        Assert.True(message.Parsed);
        Assert.Null(message.Input);
        Assert.Equal("from top.example.test by mx.example.test id 7", message.TopReceived);
    }

    // Case 41
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Message_without_received_has_no_top_received()
    {
        ClassificationMessage message = await ReadMessageAsync(ClassificationTestEml.Build("sender_a@example.test", [], "Body."));

        Assert.True(message.Parsed);
        Assert.True(string.IsNullOrEmpty(message.TopReceived));
    }

    // Case 41
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Stream_failure_propagates_as_io_exception()
    {
        using var stream = new ThrowingStream();

        await Assert.ThrowsAsync<IOException>(() => ClassificationInputReader.ReadMessageAsync(stream));
    }

    // Case 41 / G5: ReadAsync is the thin wrapper
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task ReadAsync_returns_the_input_of_the_message()
    {
        byte[] eml = ClassificationTestEml.Build("Sender A <sender_a@example.test>", [], "Body.");
        using var first = new MemoryStream(eml);
        using var second = new MemoryStream(eml);

        ClassificationInput? viaWrapper = await ClassificationInputReader.ReadAsync(first);
        ClassificationMessage viaMessage = await ClassificationInputReader.ReadMessageAsync(second);

        Assert.NotNull(viaWrapper);
        Assert.NotNull(viaMessage.Input);
        Assert.Equal(viaMessage.Input.FromAddress, viaWrapper.FromAddress);
        Assert.Equal(viaMessage.Input.BodyText, viaWrapper.BodyText);
    }

    // ---- case 40 / G4: raw header values ----------------------------------------------------------------------------

    // Case 40
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Authentication_results_value_is_raw_and_encoded_words_stay_encoded()
    {
        ClassificationMessage message = await ReadMessageAsync(
            ClassificationTestEml.Build("Sender A <" + Contact + ">", [EncodedWordForgery()], "Your paypal statement is ready."));

        Assert.NotNull(message.Input);
        string header = Assert.Single(message.Input.AuthenticationResultsHeaders);
        Assert.Contains("=?utf-8?B?", header, StringComparison.Ordinal);
        Assert.DoesNotContain("dmarc=pass", header, StringComparison.Ordinal);
        Assert.Contains("dmarc=fail", header, StringComparison.Ordinal);
    }

    // Case 40: a contact with that header is not trusted
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Forged_dmarc_pass_in_an_encoded_word_does_not_make_a_contact_trusted()
    {
        ClassificationMessage message = await ReadMessageAsync(
            ClassificationTestEml.Build("Sender A <" + Contact + ">", [EncodedWordForgery()], "Your paypal statement is ready."));
        Assert.NotNull(message.Input);

        ClassificationResult result = ClassifierWithContact().Classify(message.Input);

        Assert.NotEqual(AuthStatus.Authenticated, result.AuthStatus);
        Assert.NotEqual(SpamTier.TrustedContact, result.Tier);
    }

    // Case 40, positive control: a real dmarc=pass without encoded word is still trusted
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Real_dmarc_pass_without_encoded_word_still_gives_trusted_contact()
    {
        ClassificationMessage message = await ReadMessageAsync(
            ClassificationTestEml.Build(
                "Sender A <" + Contact + ">",
                ["mx.test; spf=pass smtp.mailfrom=bounce@paypal.test; dmarc=pass header.from=paypal.test"],
                "Your paypal statement is ready."));
        Assert.NotNull(message.Input);

        ClassificationResult result = ClassifierWithContact().Classify(message.Input);

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Equal(SpamTier.TrustedContact, result.Tier);
    }

    // G4: the top Received is raw as well
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Top_received_is_raw_and_keeps_encoded_words()
    {
        string encoded = "=?utf-8?B?" + Base64("decoded_marker_text") + "?=";
        ClassificationMessage message = await ReadMessageAsync(
            ClassificationTestEml.Build("sender_a@example.test", [], "Body.", received: "from " + encoded + " by mx.example.test"));

        Assert.NotNull(message.TopReceived);
        Assert.Contains("=?utf-8?B?", message.TopReceived, StringComparison.Ordinal);
        Assert.DoesNotContain("decoded_marker_text", message.TopReceived, StringComparison.Ordinal);
    }

    // ---- case 46 / G11 -------------------------------------------------------------------------------------------------

    // Case 46
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task More_than_10000_links_are_capped_without_exception()
    {
        var html = new StringBuilder("<html><body>");
        for (int index = 0; index < 10_500; index++)
        {
            html.Append("<a href=\"https://host").Append(index).Append(".example.test/\">x</a>");
        }

        html.Append("</body></html>");
        byte[] eml = ClassificationTestEml.BuildWithContentType("text/html; charset=utf-8", html.ToString());

        ClassificationMessage message = await ReadMessageAsync(eml);

        Assert.NotNull(message.Input);
        Assert.InRange(message.Input.LinkHosts.Count, 1, 10_000);
    }

    // Case 46, positive control: exactly below the cap nothing is dropped
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Links_below_the_cap_are_all_kept()
    {
        var html = new StringBuilder("<html><body>");
        for (int index = 0; index < 500; index++)
        {
            html.Append("<a href=\"https://host").Append(index).Append(".example.test/\">x</a>");
        }

        html.Append("</body></html>");

        ClassificationMessage message = await ReadMessageAsync(
            ClassificationTestEml.BuildWithContentType("text/html; charset=utf-8", html.ToString()));

        Assert.NotNull(message.Input);
        Assert.Equal(500, message.Input.LinkHosts.Count);
    }
}
