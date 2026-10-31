namespace MailWarden.Core.Tests.Classification;

/// <summary>
/// AP-07b E3: link-domain-mismatch, domain-not-in-body and the weak random-domain band are silent for authenticated
/// senders and behave as in AP-07a for Unknown and Failed senders.
/// </summary>
[Trait("Category", "feature:classifier")]
public sealed class AuthGatedContentSignalTests
{
    private const string WeakLabel = "wrtplkjo"; // random-domain score in [0.5, 0.8), see SpamClassifierTests case 10a
    private const string StrongLabel = "qzxkvb73"; // random-domain score >= 0.8, +35

    private static readonly string[] ForeignLinks = ["one.test", "two.test", "three.test", "four.test", "five.test"];

    private static ClassificationResult Classify(ClassificationInput input) => TestInputs.CreateClassifier().Classify(input);

    private static int? PointsOf(ClassificationResult result, string signalId) => TestInputs.Find(result, signalId)?.Points;

    /// <summary>Headers that yield the given AuthStatus for <paramref name="domain"/>.</summary>
    private static string[] HeadersFor(AuthStatus status, string domain) => status switch
    {
        AuthStatus.Authenticated => [AuthHeaders.AllPass(domain)],
        AuthStatus.Failed => [AuthHeaders.AllFail(domain)],
        AuthStatus.Unknown => [],
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    // ---- link-domain-mismatch ------------------------------------------------------------------------------

    // Case 8
    [Fact]
    public void Authenticated_sender_gets_no_link_domain_mismatch_finding()
    {
        ClassificationInput input = TestInputs.Create(
            headers: HeadersFor(AuthStatus.Authenticated, TestInputs.PlausibleDomain), links: ForeignLinks);

        ClassificationResult result = Classify(input);

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Null(TestInputs.Find(result, "link-domain-mismatch"));
        Assert.Equal(-20, result.Score);
    }

    // Case 9
    [Theory]
    [InlineData(AuthStatus.Unknown)]
    [InlineData(AuthStatus.Failed)]
    public void Unknown_or_failed_sender_keeps_the_link_domain_mismatch_finding(AuthStatus status)
    {
        ClassificationInput input = TestInputs.Create(
            headers: HeadersFor(status, TestInputs.PlausibleDomain), links: ForeignLinks);

        ClassificationResult result = Classify(input);

        Assert.Equal(status, result.AuthStatus);
        Assert.Equal(10, PointsOf(result, "link-domain-mismatch"));
    }

    // Case 9: DMARC fail rescued by compauth is Unknown, so the content signal applies again
    [Fact]
    public void Compauth_case_sender_keeps_the_link_domain_mismatch_finding()
    {
        ClassificationInput input = TestInputs.Create(
            headers: [$"mx.test; dmarc=fail header.from={TestInputs.PlausibleDomain}; compauth=pass reason=105"],
            links: ForeignLinks);

        ClassificationResult result = Classify(input);

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Equal(10, PointsOf(result, "link-domain-mismatch"));
    }

    // ---- domain-not-in-body --------------------------------------------------------------------------------

    // Case 10
    [Fact]
    public void Authenticated_sender_gets_no_domain_not_in_body_finding()
    {
        ClassificationInput input = TestInputs.Create(
            body: "Hello there, your order has shipped.",
            headers: HeadersFor(AuthStatus.Authenticated, TestInputs.PlausibleDomain));

        ClassificationResult result = Classify(input);

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Null(TestInputs.Find(result, "domain-not-in-body"));
        Assert.Equal(-20, result.Score);
    }

    // Case 10 (counterpart)
    [Theory]
    [InlineData(AuthStatus.Unknown)]
    [InlineData(AuthStatus.Failed)]
    public void Unknown_or_failed_sender_keeps_the_domain_not_in_body_finding(AuthStatus status)
    {
        ClassificationInput input = TestInputs.Create(
            body: "Hello there, your order has shipped.", headers: HeadersFor(status, TestInputs.PlausibleDomain));

        ClassificationResult result = Classify(input);

        Assert.Equal(status, result.AuthStatus);
        Assert.Equal(15, PointsOf(result, "domain-not-in-body"));
    }

    // ---- random-domain -------------------------------------------------------------------------------------

    // Case 11
    [Fact]
    public void Authenticated_sender_with_weak_random_label_gets_no_random_domain_finding()
    {
        Assert.InRange(RandomDomainScorer.Score(WeakLabel), 0.5, 0.7999999);
        ClassificationInput input = TestInputs.Create(
            from: $"sender_a@{WeakLabel}.test",
            body: $"Message from {WeakLabel}.",
            headers: HeadersFor(AuthStatus.Authenticated, $"{WeakLabel}.test"));

        ClassificationResult result = Classify(input);

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Null(TestInputs.Find(result, "random-domain"));
    }

    // Case 11 (counterpart: AP-07a behaviour +15 for Unknown and Failed)
    [Theory]
    [InlineData(AuthStatus.Unknown)]
    [InlineData(AuthStatus.Failed)]
    public void Unknown_or_failed_sender_with_weak_random_label_keeps_fifteen_points(AuthStatus status)
    {
        ClassificationInput input = TestInputs.Create(
            from: $"sender_a@{WeakLabel}.test",
            body: $"Message from {WeakLabel}.",
            headers: HeadersFor(status, $"{WeakLabel}.test"));

        ClassificationResult result = Classify(input);

        Assert.Equal(status, result.AuthStatus);
        Assert.Equal(15, PointsOf(result, "random-domain"));
    }

    // Case 12
    [Fact]
    public void Authenticated_sender_with_strong_random_label_keeps_thirty_five_points()
    {
        Assert.True(RandomDomainScorer.Score(StrongLabel) >= 0.8);
        ClassificationInput input = TestInputs.Create(
            from: $"sender_a@{StrongLabel}.test",
            body: $"Message from {StrongLabel}.",
            headers: HeadersFor(AuthStatus.Authenticated, $"{StrongLabel}.test"));

        ClassificationResult result = Classify(input);

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Equal(35, PointsOf(result, "random-domain"));
        Assert.Contains(StrongLabel, TestInputs.Find(result, "random-domain")!.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- signals outside E3 stay untouched -----------------------------------------------------------------

    [Fact]
    public void Authenticated_sender_keeps_suspicious_tld_and_reply_to_findings()
    {
        ClassificationInput input = TestInputs.Create(
            from: $"sender_a@{StrongLabel}.top",
            body: $"Message from {StrongLabel}.",
            replyTo: "collect@gmail.com",
            headers: HeadersFor(AuthStatus.Authenticated, $"{StrongLabel}.top"));

        ClassificationResult result = Classify(input);

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Equal(10, PointsOf(result, "suspicious-tld"));
        Assert.Equal(15, PointsOf(result, "reply-to-mismatch"));
    }

    [Fact]
    public void Authenticated_sender_keeps_the_display_name_mismatch_finding()
    {
        ClassificationInput input = TestInputs.Create(
            fromName: "Service <service@other.test>",
            headers: HeadersFor(AuthStatus.Authenticated, TestInputs.PlausibleDomain));

        Assert.Equal(20, PointsOf(Classify(input), "display-name-mismatch"));
    }

    // Case 21 (positive control)
    [Fact]
    public void Obvious_spam_with_failing_authentication_is_still_certain_spam()
    {
        ClassificationResult result = Classify(TestInputs.ObviousSpam());

        Assert.Equal(SpamTier.CertainSpam, result.Tier);
        Assert.Equal(10, PointsOf(result, "link-domain-mismatch"));
        Assert.Equal(15, PointsOf(result, "domain-not-in-body"));
        Assert.Equal(35, PointsOf(result, "random-domain"));
    }

    // Case 21 (the same message authenticated loses the gated content points but keeps the strong random-domain one)
    [Fact]
    public void Obvious_spam_if_it_were_authenticated_loses_only_the_gated_content_points()
    {
        ClassificationInput input = TestInputs.Create(
            from: "offer@qzxkvb73.top",
            fromName: "Offer",
            headers: [AuthHeaders.AllPass("qzxkvb73.top")],
            body: "Claim your prize now.",
            links: ["tracker.other.test", "cdn.another.test"]);

        ClassificationResult result = Classify(input);

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Null(TestInputs.Find(result, "link-domain-mismatch"));
        Assert.Null(TestInputs.Find(result, "domain-not-in-body"));
        Assert.Equal(35, PointsOf(result, "random-domain"));
        Assert.Equal(10, PointsOf(result, "suspicious-tld"));
    }
}
