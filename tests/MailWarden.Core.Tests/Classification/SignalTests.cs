namespace MailWarden.Core.Tests.Classification;

/// <summary>Points and applicability of the built-in signals, observed through the default classifier.</summary>
[Trait("Category", "feature:classifier")]
public sealed class SignalTests
{
    private static ClassificationResult Classify(ClassificationInput input, ClassifierSettings? settings = null) =>
        new SpamClassifier(settings ?? ClassifierSettings.Default, new FakeSenderDirectory(), SpamClassifier.CreateDefaultSignals())
            .Classify(input);

    private static int? PointsOf(ClassificationResult result, string signalId) => TestInputs.Find(result, signalId)?.Points;

    // ---- random-domain, suspicious-tld ---------------------------------------------------------------------

    [Fact]
    public void Random_label_and_suspicious_tld_score_when_body_mentions_the_label()
    {
        ClassificationResult result = Classify(TestInputs.Create(from: "offer@qzxkvb73.top", body: "Hello from qzxkvb73"));

        Assert.Equal(35, PointsOf(result, "random-domain"));
        Assert.Equal(10, PointsOf(result, "suspicious-tld"));
        Assert.Null(TestInputs.Find(result, "domain-not-in-body"));
        Assert.Equal(45, result.Score);
    }

    [Fact]
    public void Plausible_label_gets_no_random_domain_finding()
    {
        ClassificationResult result = Classify(TestInputs.Create());

        Assert.Null(TestInputs.Find(result, "random-domain"));
        Assert.Null(TestInputs.Find(result, "suspicious-tld"));
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void Suspicious_tld_matching_is_case_insensitive_for_settings_and_sender()
    {
        ClassifierSettings settings = ClassifierSettings.Default with { SuspiciousTlds = new HashSet<string> { "TOP" } };

        ClassificationResult result = Classify(TestInputs.Create(from: "Offer@PAYPAL.Top", body: "paypal"), settings);

        Assert.Equal(10, PointsOf(result, "suspicious-tld"));
    }

    [Fact]
    public void Suspicious_tld_uses_the_last_label_for_multi_label_suffixes()
    {
        ClassifierSettings settings = ClassifierSettings.Default with { SuspiciousTlds = new HashSet<string> { "uk" } };
        ClassificationInput input = TestInputs.Create(from: "info@paypal.co.uk");

        Assert.Equal(10, PointsOf(Classify(input, settings), "suspicious-tld"));
        Assert.Null(TestInputs.Find(Classify(input), "suspicious-tld"));
    }

    [Fact]
    public void Freemail_settings_are_case_insensitive_and_suppress_the_random_domain_finding()
    {
        ClassifierSettings settings = ClassifierSettings.Default with
        {
            FreemailDomains = new HashSet<string> { "QZXKVB73.TOP" },
        };

        ClassificationResult result = Classify(TestInputs.Create(from: "someone@qzxkvb73.top", body: "hello"), settings);

        Assert.Null(TestInputs.Find(result, "random-domain"));
        Assert.Null(TestInputs.Find(result, "domain-not-in-body"));
    }

    // ---- domain-not-in-body --------------------------------------------------------------------------------

    // Case 23
    [Fact]
    public void Freemail_sender_with_random_local_part_and_empty_body_has_no_domain_findings()
    {
        ClassificationInput input = TestInputs.Create(from: "qzxkvb73@gmail.com", fromName: "Someone", body: "");

        ClassificationResult result = Classify(input);

        Assert.Null(TestInputs.Find(result, "random-domain"));
        Assert.Null(TestInputs.Find(result, "domain-not-in-body"));
        Assert.Null(TestInputs.Find(result, "link-domain-mismatch"));
    }

    // Case 23 (stronger: freemail never triggers these even with body and foreign links)
    [Fact]
    public void Freemail_sender_with_body_and_foreign_links_has_no_domain_findings()
    {
        ClassificationInput input = TestInputs.Create(
            from: "qzxkvb73@gmail.com", fromName: "Someone", body: "Please read this.", links: ["tracker.other.test"]);

        ClassificationResult result = Classify(input);

        Assert.Null(TestInputs.Find(result, "random-domain"));
        Assert.Null(TestInputs.Find(result, "domain-not-in-body"));
        Assert.Null(TestInputs.Find(result, "link-domain-mismatch"));
    }

    [Fact]
    public void Domain_not_in_body_scores_15_when_label_and_links_do_not_match()
    {
        ClassificationResult result = Classify(TestInputs.Create(body: "Hello there, your order has shipped."));

        Assert.Equal(15, PointsOf(result, "domain-not-in-body"));
        Assert.Equal(15, result.Score);
    }

    [Fact]
    public void Domain_label_in_body_is_matched_case_insensitively()
    {
        ClassificationResult result = Classify(TestInputs.Create(body: "Best regards, PAYPAL Customer Care"));

        Assert.Null(TestInputs.Find(result, "domain-not-in-body"));
    }

    [Fact]
    public void Empty_body_gives_no_domain_not_in_body_finding()
    {
        ClassificationResult result = Classify(TestInputs.Create(body: ""));

        Assert.Null(TestInputs.Find(result, "domain-not-in-body"));
    }

    [Fact]
    public void Aligned_link_host_suppresses_domain_not_in_body()
    {
        ClassificationResult result = Classify(TestInputs.Create(body: "Hello there.", links: ["www.paypal.test"]));

        Assert.Null(TestInputs.Find(result, "domain-not-in-body"));
        Assert.Null(TestInputs.Find(result, "link-domain-mismatch"));
    }

    [Fact]
    public void Foreign_link_host_does_not_suppress_domain_not_in_body()
    {
        ClassificationResult result = Classify(TestInputs.Create(body: "Hello there.", links: ["www.other.test"]));

        Assert.Equal(15, PointsOf(result, "domain-not-in-body"));
    }

    // ---- display-name-mismatch -----------------------------------------------------------------------------

    // Case 24
    [Fact]
    public void Display_name_with_foreign_address_scores_20()
    {
        ClassificationInput input = TestInputs.Create(
            from: "x@qzxkvb73.top", fromName: "PayPal Service <service@paypal.test>", body: "qzxkvb73");

        Assert.Equal(20, PointsOf(Classify(input), "display-name-mismatch"));
    }

    // Case 24
    [Fact]
    public void Display_name_without_address_or_domain_has_no_finding()
    {
        ClassificationInput input = TestInputs.Create(from: "x@qzxkvb73.top", fromName: "Example Shop", body: "qzxkvb73");

        Assert.Null(TestInputs.Find(Classify(input), "display-name-mismatch"));
    }

    [Fact]
    public void Display_name_with_foreign_domain_like_token_scores_20()
    {
        ClassificationInput input = TestInputs.Create(fromName: "Service www.other.test");

        Assert.Equal(20, PointsOf(Classify(input), "display-name-mismatch"));
    }

    [Fact]
    public void Display_name_with_own_organizational_domain_has_no_finding()
    {
        ClassificationInput inputWithAddress = TestInputs.Create(fromName: "Support <help@mail.paypal.test>");
        ClassificationInput inputWithDomain = TestInputs.Create(fromName: "paypal.test Support");

        Assert.Null(TestInputs.Find(Classify(inputWithAddress), "display-name-mismatch"));
        Assert.Null(TestInputs.Find(Classify(inputWithDomain), "display-name-mismatch"));
    }

    [Fact]
    public void Display_name_with_numeric_dotted_token_is_not_a_domain()
    {
        ClassificationInput input = TestInputs.Create(fromName: "Version 2.0 Support");

        Assert.Null(TestInputs.Find(Classify(input), "display-name-mismatch"));
    }

    // ---- reply-to-mismatch ---------------------------------------------------------------------------------

    // Case 25
    [Fact]
    public void Freemail_reply_to_with_non_freemail_sender_scores_15()
    {
        ClassificationInput input = TestInputs.Create(from: "billing@example.test", body: "example", replyTo: "collect@gmail.com");

        Assert.Equal(15, PointsOf(Classify(input), "reply-to-mismatch"));
    }

    // Case 25
    [Fact]
    public void Aligned_reply_to_has_no_finding()
    {
        ClassificationInput input = TestInputs.Create(from: "billing@example.test", body: "example", replyTo: "support@example.test");

        Assert.Null(TestInputs.Find(Classify(input), "reply-to-mismatch"));
    }

    [Fact]
    public void Reply_to_on_a_subdomain_of_the_sender_is_aligned()
    {
        ClassificationInput input = TestInputs.Create(from: "billing@example.test", body: "example", replyTo: "support@help.example.test");

        Assert.Null(TestInputs.Find(Classify(input), "reply-to-mismatch"));
    }

    // Case 25
    [Fact]
    public void Foreign_non_freemail_reply_to_scores_10()
    {
        ClassificationInput input = TestInputs.Create(from: "billing@example.test", body: "example", replyTo: "a@other.test");

        Assert.Equal(10, PointsOf(Classify(input), "reply-to-mismatch"));
    }

    [Fact]
    public void Freemail_reply_to_with_freemail_sender_scores_10()
    {
        ClassificationInput input = TestInputs.Create(from: "someone@gmail.com", body: "", replyTo: "other@yahoo.com");

        Assert.Equal(10, PointsOf(Classify(input), "reply-to-mismatch"));
    }

    [Fact]
    public void Missing_reply_to_has_no_finding()
    {
        Assert.Null(TestInputs.Find(Classify(TestInputs.Create(replyTo: null)), "reply-to-mismatch"));
    }

    // ---- link-domain-mismatch ------------------------------------------------------------------------------

    [Fact]
    public void Single_foreign_link_scores_10()
    {
        ClassificationResult result = Classify(TestInputs.Create(links: ["www.other.test"]));

        Assert.Equal(10, PointsOf(result, "link-domain-mismatch"));
        Assert.Equal(10, result.Score);
    }

    [Fact]
    public void Eighty_percent_foreign_distinct_links_trigger_the_finding()
    {
        string[] links = ["one.test", "two.test", "three.test", "four.test", "www.paypal.test"];

        Assert.Equal(10, PointsOf(Classify(TestInputs.Create(links: links)), "link-domain-mismatch"));
    }

    [Fact]
    public void Sixty_percent_foreign_distinct_links_do_not_trigger_the_finding()
    {
        string[] links = ["one.test", "two.test", "three.test", "www.paypal.test", "paypal.test"];

        Assert.Null(TestInputs.Find(Classify(TestInputs.Create(links: links)), "link-domain-mismatch"));
    }

    [Fact]
    public void Duplicate_link_hosts_are_counted_once()
    {
        string[] links = ["one.test", "one.test", "one.test", "one.test", "one.test", "www.paypal.test"];

        Assert.Null(TestInputs.Find(Classify(TestInputs.Create(links: links)), "link-domain-mismatch"));
    }

    [Fact]
    public void Only_aligned_or_no_links_give_no_link_finding()
    {
        Assert.Null(TestInputs.Find(Classify(TestInputs.Create(links: ["www.paypal.test"])), "link-domain-mismatch"));
        Assert.Null(TestInputs.Find(Classify(TestInputs.Create(links: [])), "link-domain-mismatch"));
    }
}
