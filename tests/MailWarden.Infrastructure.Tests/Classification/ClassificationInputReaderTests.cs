using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-07c D4 / cases 8 and 9: building a <see cref="ClassificationInput"/> from an EML.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationInputReaderTests
{
    private static async Task<ClassificationInput?> ReadAsync(byte[] eml)
    {
        using var stream = new MemoryStream(eml);
        return await ClassificationInputReader.ReadAsync(stream);
    }

    private static byte[] Plain(string? from, string body = "Plain body.", string? replyTo = null, string[]? auth = null) =>
        ClassificationTestEml.Build(from, auth ?? [], body, replyTo);

    private static byte[] Html(string html) =>
        ClassificationTestEml.BuildWithContentType("text/html; charset=utf-8", html);

    // ---- case 8: headers -------------------------------------------------------------------------------------------

    // Case 8
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task From_with_display_name_gives_address_and_name()
    {
        ClassificationInput? input = await ReadAsync(Plain("Sender A <sender_a@example.test>"));

        Assert.NotNull(input);
        Assert.Equal("sender_a@example.test", input.FromAddress);
        Assert.Equal("Sender A", input.FromName);
        Assert.Null(input.ReplyToAddress);
        Assert.Empty(input.AuthenticationResultsHeaders);
        Assert.Empty(input.LinkHosts);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task From_without_display_name_gives_an_empty_name()
    {
        ClassificationInput? input = await ReadAsync(Plain("sender_a@example.test"));

        Assert.NotNull(input);
        Assert.Equal("sender_a@example.test", input.FromAddress);
        Assert.Equal(string.Empty, input.FromName);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Quoted_display_name_is_unquoted()
    {
        ClassificationInput? input = await ReadAsync(Plain("\"Sender, A\" <sender_a@example.test>"));

        Assert.NotNull(input);
        Assert.Equal("Sender, A", input.FromName);
        Assert.Equal("sender_a@example.test", input.FromAddress);
    }

    // Case 8
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Missing_from_gives_null()
    {
        Assert.Null(await ReadAsync(Plain(null)));
    }

    // Case 8
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Two_from_addresses_give_null()
    {
        Assert.Null(await ReadAsync(Plain("Sender A <sender_a@example.test>, Sender B <sender_b@example.test>")));
    }

    // Case 8
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("sender_a")]
    [InlineData("Sender A <sender_a>")]
    [InlineData("sender_a@")]
    public async Task From_without_a_domain_gives_null(string from)
    {
        Assert.Null(await ReadAsync(Plain(from)));
    }

    // Case 8
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reply_to_address_is_read()
    {
        ClassificationInput? input = await ReadAsync(Plain("Sender A <sender_a@example.test>", replyTo: "Reply Desk <reply_a@other.test>"));

        Assert.NotNull(input);
        Assert.Equal("reply_a@other.test", input.ReplyToAddress);
    }

    // Case 8: three headers, order kept, top-most first
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Authentication_results_are_kept_in_header_order()
    {
        ClassificationInput? input = await ReadAsync(Plain(
            "Sender A <sender_a@example.test>",
            auth:
            [
                "mx1.example.test; spf=pass smtp.mailfrom=example.test",
                "mx2.example.test; dkim=pass header.d=example.test",
                "mx3.example.test; dmarc=pass header.from=example.test",
            ]));

        Assert.NotNull(input);
        Assert.Equal(3, input.AuthenticationResultsHeaders.Count);
        Assert.Contains("mx1.example.test", input.AuthenticationResultsHeaders[0], StringComparison.Ordinal);
        Assert.Contains("spf=pass", input.AuthenticationResultsHeaders[0], StringComparison.Ordinal);
        Assert.Contains("mx2.example.test", input.AuthenticationResultsHeaders[1], StringComparison.Ordinal);
        Assert.Contains("mx3.example.test", input.AuthenticationResultsHeaders[2], StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Garbage_bytes_give_null()
    {
        Assert.Null(await ReadAsync(SyncTestEml.Garbage()));
    }

    // ---- body ----------------------------------------------------------------------------------------------------------

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Plain_body_is_the_body_text()
    {
        ClassificationInput? input = await ReadAsync(Plain("sender_a@example.test", "Your paypal statement is ready."));

        Assert.NotNull(input);
        Assert.Contains("Your paypal statement is ready.", input.BodyText, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_body_is_reduced_to_text()
    {
        ClassificationInput? input = await ReadAsync(Html("<html><body><p>Visible words here.</p></body></html>"));

        Assert.NotNull(input);
        Assert.Contains("Visible words here.", input.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", input.BodyText, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Body_text_is_limited_to_200000_characters()
    {
        ClassificationInput? input = await ReadAsync(Plain("sender_a@example.test", new string('x', 300_000)));

        Assert.NotNull(input);
        Assert.Equal(200_000, input.BodyText.Length);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Stream_is_not_disposed_by_the_reader()
    {
        using var stream = new MemoryStream(Plain("sender_a@example.test"));

        await ClassificationInputReader.ReadAsync(stream);

        Assert.True(stream.CanRead);
        _ = stream.Length;
    }

    // ---- case 9: link hosts --------------------------------------------------------------------------------------------

    // Case 9
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_anchor_hosts_are_lower_cased_and_keep_duplicates()
    {
        ClassificationInput? input = await ReadAsync(Html(
            "<html><body><a href=\"https://Example.Test/path\">one</a> " +
            "<a href=\"http://example.test/other\">two</a> " +
            "<a href=\"https://Second.Example.Test:8443/x\">three</a></body></html>"));

        Assert.NotNull(input);
        Assert.Equal(
            ["example.test", "example.test", "second.example.test"],
            input.LinkHosts.OrderBy(host => host, StringComparer.Ordinal).ToArray());
    }

    // Case 9: the query is not part of the host
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Tracking_link_with_query_gives_only_its_own_host()
    {
        ClassificationInput? input = await ReadAsync(Html(
            "<a href=\"https://Click.Tracker.Test/c?u=https%3A%2F%2Ftarget.example.test%2Fa&x=1&r=http://redirect.example.test/\">go</a>"));

        Assert.NotNull(input);
        Assert.Equal(["click.tracker.test"], input.LinkHosts.ToArray());
    }

    // Case 9
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mailto_and_other_schemes_are_ignored()
    {
        ClassificationInput? input = await ReadAsync(Html(
            "<a href=\"mailto:contact_a@mail-host.test\">mail</a> " +
            "<a href=\"javascript:alert(1)\">script</a> " +
            "<a href=\"ftp://files.example.test/a\">ftp</a> " +
            "<a href=\"https://kept.example.test/\">kept</a>"));

        Assert.NotNull(input);
        Assert.Equal(["kept.example.test"], input.LinkHosts.ToArray());
    }

    // Case 9: malformed URLs are skipped without exception
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Malformed_urls_are_skipped_without_exception()
    {
        ClassificationInput? input = await ReadAsync(Html(
            "<a href=\"https://\">empty</a> " +
            "<a href=\"http://[broken\">broken</a> " +
            "<a href=\"not a url\">words</a> " +
            "<a href=\"\">nothing</a> " +
            "<a>no href</a> " +
            "<a href=\"https://good.example.test/\">good</a>"));

        Assert.NotNull(input);
        Assert.Equal(["good.example.test"], input.LinkHosts.ToArray());
    }

    // Case 9: only anchors count in HTML; plain URL text and images do not
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Only_anchor_hrefs_count_in_html()
    {
        ClassificationInput? input = await ReadAsync(Html(
            "<p>See https://texturl.example.test/page for details.</p>" +
            "<img src=\"https://image.example.test/pixel.gif\">" +
            "<a href=\"https://anchor.example.test/\">anchor</a>"));

        Assert.NotNull(input);
        Assert.Equal(["anchor.example.test"], input.LinkHosts.ToArray());
    }

    // Case 9: text-only mail
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Text_only_urls_are_read_from_the_text()
    {
        ClassificationInput? input = await ReadAsync(Plain(
            "sender_a@example.test",
            "Visit https://Text-One.Example.Test/a?b=1 and http://text-two.example.test:8080/b today\r\n" +
            "or write to mailto:person_a@mail-host.test for help."));

        Assert.NotNull(input);
        Assert.Equal(
            ["text-one.example.test", "text-two.example.test"],
            input.LinkHosts.OrderBy(host => host, StringComparer.Ordinal).ToArray());
    }

    // Case 9: without HTML the text URLs are used, with HTML only the anchors
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Alternative_mail_with_html_uses_the_anchors_not_the_text_part()
    {
        ClassificationInput? input = await ReadAsync(ClassificationTestEml.Alternative(
            "Text part https://text-part.example.test/a",
            "<html><body><a href=\"https://html-part.example.test/b\">link</a></body></html>"));

        Assert.NotNull(input);
        Assert.Equal(["html-part.example.test"], input.LinkHosts.ToArray());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mail_without_links_has_no_link_hosts()
    {
        ClassificationInput? input = await ReadAsync(Plain("sender_a@example.test", "No links at all."));

        Assert.NotNull(input);
        Assert.Empty(input.LinkHosts);
    }

    // The input of a real fixture feeds the classifier to the expected tier (end of the chain, no mocks)
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(MailKind.Legit, SpamTier.LikelyLegit)]
    [InlineData(MailKind.LikelySpam, SpamTier.LikelySpam)]
    [InlineData(MailKind.CertainSpam, SpamTier.CertainSpam)]
    public async Task Fixture_input_reaches_the_expected_tier_with_the_real_signals(MailKind kind, SpamTier expected)
    {
        ClassificationInput? input = await ReadAsync(ClassificationTestEml.For(kind));
        Assert.NotNull(input);
        var classifier = new SpamClassifier(
            ClassifierSettings.Default, new ContactSenderDirectory(["unrelated_a@example.test"], []), SpamClassifier.CreateDefaultSignals());

        ClassificationResult result = classifier.Classify(input);

        Assert.Equal(expected, result.Tier);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Trusted_fixture_is_trusted_for_its_contact_only()
    {
        ClassificationInput? input = await ReadAsync(ClassificationTestEml.For(MailKind.Trusted));
        Assert.NotNull(input);
        SpamClassifier withContact = new(
            ClassifierSettings.Default, new ContactSenderDirectory([ClassificationTestEml.ContactAddress], []), SpamClassifier.CreateDefaultSignals());
        SpamClassifier withoutContact = new(
            ClassifierSettings.Default, new ContactSenderDirectory(["unrelated_a@example.test"], []), SpamClassifier.CreateDefaultSignals());

        Assert.Equal(SpamTier.TrustedContact, withContact.Classify(input).Tier);
        Assert.Equal(SpamTier.LikelyLegit, withoutContact.Classify(input).Tier);
    }
}
