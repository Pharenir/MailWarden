namespace MailWarden.Core.Tests.Classification;

/// <summary>Decision logic of <see cref="SpamClassifier"/>: AuthStatus, tiers, overrides, argument checks.</summary>
[Trait("Category", "feature:classifier")]
public sealed class SpamClassifierTests
{
    private const string Sender = "sender_a@" + TestInputs.PlausibleDomain;
    private const string AuthenticatedHeaderMarker = "authenticated";

    private static ClassificationResult Classify(ClassificationInput input, ISenderDirectory? directory = null) =>
        TestInputs.CreateClassifier(directory).Classify(input);

    private static ClassificationResult ClassifyWithStubs(
        ClassificationInput input, ISenderDirectory? directory, params ISpamSignal[] signals) =>
        TestInputs.CreateStubClassifier(directory, null, signals).Classify(input);

    // ---- default signal list -------------------------------------------------------------------------------

    [Fact]
    public void CreateDefaultSignals_returns_the_built_in_signals_in_contract_order()
    {
        string[] expected =
        [
            "authentication", "random-domain", "suspicious-tld", "domain-not-in-body",
            "display-name-mismatch", "reply-to-mismatch", "link-domain-mismatch",
        ];

        Assert.Equal(expected, SpamClassifier.CreateDefaultSignals().Select(signal => signal.Id).ToArray());
    }

    // ---- cases 13 and 18 -----------------------------------------------------------------------------------

    // Case 13
    [Fact]
    public void Authenticated_plausible_sender_is_likely_legit_with_minus_20()
    {
        ClassificationResult result = Classify(TestInputs.LegitAuthenticated());

        Assert.Equal(SpamTier.LikelyLegit, result.Tier);
        Assert.Equal(-20, result.Score);
        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Null(result.Override);
        SignalResult authentication = Assert.IsType<SignalResult>(TestInputs.Find(result, "authentication"));
        Assert.Equal(-20, authentication.Points);
    }

    // Case 18
    [Fact]
    public void Missing_header_for_plausible_non_contact_is_likely_legit_and_unknown()
    {
        ClassificationResult result = Classify(TestInputs.Create());

        Assert.Equal(SpamTier.LikelyLegit, result.Tier);
        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Null(TestInputs.Find(result, "authentication"));
        Assert.Null(result.Override);
    }

    // ---- case 14 -------------------------------------------------------------------------------------------

    // Case 14
    [Fact]
    public void Obvious_spam_is_certain_spam_with_all_expected_findings()
    {
        ClassificationResult result = Classify(TestInputs.ObviousSpam());

        Assert.Equal(SpamTier.CertainSpam, result.Tier);
        Assert.Equal(120, result.Score);
        Assert.Equal(AuthStatus.Failed, result.AuthStatus);
        Assert.Null(result.Override);
        Assert.Equal(50, TestInputs.Find(result, "authentication")?.Points);
        Assert.Equal(35, TestInputs.Find(result, "random-domain")?.Points);
        Assert.Equal(10, TestInputs.Find(result, "suspicious-tld")?.Points);
        Assert.Equal(15, TestInputs.Find(result, "domain-not-in-body")?.Points);
        Assert.Equal(10, TestInputs.Find(result, "link-domain-mismatch")?.Points);
        Assert.Equal(120, result.Signals.Sum(signal => signal.Points));
    }

    [Fact]
    public void Signals_are_returned_in_evaluation_order()
    {
        ClassificationResult result = Classify(TestInputs.ObviousSpam());

        string[] expected = ["authentication", "random-domain", "suspicious-tld", "domain-not-in-body", "link-domain-mismatch"];
        Assert.Equal(expected, result.Signals.Select(signal => signal.SignalId).ToArray());
    }

    // ---- AuthStatus ----------------------------------------------------------------------------------------

    public static TheoryData<string, AuthStatus> AuthStatusScenarios() => new()
    {
        { "mx.test; dmarc=pass header.from=paypal.test", AuthStatus.Authenticated },
        { "mx.test; dmarc=pass header.from=other.test", AuthStatus.Unknown }, // C12
        { "mx.test; dmarc=pass header.from=mail.paypal.test", AuthStatus.Authenticated },
        { "mx.test; dmarc=pass", AuthStatus.Authenticated },
        { "mx.test; dkim=pass header.d=paypal.test", AuthStatus.Authenticated },
        { "mx.test; dkim=pass header.d=mail.paypal.test", AuthStatus.Authenticated },
        { "mx.test; spf=pass smtp.mailfrom=bounce@paypal.test", AuthStatus.Authenticated },
        { "mx.test; spf=pass smtp.mailfrom=bounce@mail.paypal.test", AuthStatus.Authenticated },
        { "mx.test; spf=fail smtp.mailfrom=x@paypal.test; dkim=pass header.d=paypal.test", AuthStatus.Authenticated },
        { "mx.test; dmarc=fail header.from=paypal.test; dkim=pass header.d=paypal.test", AuthStatus.Failed }, // C11
        { "mx.test; dmarc=fail header.from=paypal.test; spf=pass smtp.mailfrom=paypal.test", AuthStatus.Failed },
        { "mx.test; dmarc=pass header.from=other.test; dkim=pass header.d=paypal.test", AuthStatus.Authenticated },
        { "mx.test; dmarc=pass header.from=other.test; spf=fail smtp.mailfrom=bounce@paypal.test", AuthStatus.Failed },
        { "mx.test; dkim=pass header.d=mailer.test", AuthStatus.Unknown },
        { "mx.test; spf=pass smtp.mailfrom=bounce@mailer.test", AuthStatus.Unknown },
        { "mx.test; spf=softfail smtp.mailfrom=bounce@paypal.test", AuthStatus.Unknown },
        { "mx.test; spf=neutral smtp.mailfrom=bounce@paypal.test", AuthStatus.Unknown },
        { "mx.test; spf=temperror smtp.mailfrom=bounce@paypal.test", AuthStatus.Unknown },
        { "mx.test; dkim=none", AuthStatus.Unknown },
        { "mx.test; dmarc=none header.from=paypal.test", AuthStatus.Unknown },
        { "mx.test; dkim=fail header.d=paypal.test; dkim=pass header.d=mailer.test", AuthStatus.Unknown },
        { "", AuthStatus.Unknown },
        { "%%%;;==", AuthStatus.Unknown },
        { "mx.test; dmarc=fail header.from=paypal.test", AuthStatus.Failed },
        { "mx.test; spf=fail smtp.mailfrom=bounce@paypal.test", AuthStatus.Failed },
        { "mx.test; dkim=fail header.d=paypal.test", AuthStatus.Failed },
        { "mx.test; dkim=fail header.d=paypal.test; dkim=fail header.d=other.test", AuthStatus.Failed },
        { "mx.test; spf=softfail smtp.mailfrom=bounce@paypal.test; dkim=fail header.d=paypal.test", AuthStatus.Failed },
    };

    [Theory]
    [MemberData(nameof(AuthStatusScenarios))]
    public void AuthStatus_is_derived_from_the_header(string header, AuthStatus expected)
    {
        ClassificationResult result = Classify(TestInputs.Create(headers: [header]));

        Assert.Equal(expected, result.AuthStatus);
    }

    [Fact]
    public void AuthStatus_without_any_header_is_unknown()
    {
        Assert.Equal(AuthStatus.Unknown, Classify(TestInputs.Create(headers: [])).AuthStatus);
    }

    // Case 21 (positive control: fails if a header other than index 0 is used)
    [Fact]
    public void Only_the_top_most_header_counts_failed_over_passed()
    {
        ClassificationInput input = TestInputs.Create(
            headers: [AuthHeaders.AllFail(TestInputs.PlausibleDomain), AuthHeaders.AllPass(TestInputs.PlausibleDomain)]);

        Assert.Equal(AuthStatus.Failed, Classify(input).AuthStatus);
    }

    // Case 21 (reverse direction: a forged failing lower header cannot hurt)
    [Fact]
    public void Only_the_top_most_header_counts_passed_over_failed()
    {
        ClassificationInput input = TestInputs.Create(
            headers: [AuthHeaders.AllPass(TestInputs.PlausibleDomain), AuthHeaders.AllFail(TestInputs.PlausibleDomain)]);

        ClassificationResult result = Classify(input);

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Equal(-20, TestInputs.Find(result, "authentication")?.Points);
    }

    // Case 21
    [Fact]
    public void Lower_headers_are_ignored_when_the_top_most_is_inconclusive()
    {
        ClassificationInput input = TestInputs.Create(
            headers: ["mx.test; spf=neutral", AuthHeaders.AllPass(TestInputs.PlausibleDomain)]);

        Assert.Equal(AuthStatus.Unknown, Classify(input).AuthStatus);
    }

    // Case 22 (positive control: fails if alignment is ignored)
    [Fact]
    public void Dkim_pass_for_a_non_aligned_domain_is_not_authenticated()
    {
        ClassificationInput input = TestInputs.Create(
            from: "sender_a@example.test",
            body: "Mail from example",
            headers: ["mx.test; dkim=pass header.d=mailer.test"]);

        ClassificationResult result = Classify(input);

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Null(TestInputs.Find(result, "authentication"));
    }

    public static TheoryData<string, int?> AuthenticationPointsScenarios() => new()
    {
        { AuthHeaders.AllPass(TestInputs.PlausibleDomain), -20 },
        { "mx.test; dmarc=pass header.from=paypal.test; spf=fail smtp.mailfrom=x@paypal.test", -20 },
        { "mx.test; dmarc=fail header.from=paypal.test", 40 },
        { "mx.test; spf=fail smtp.mailfrom=x@paypal.test", 20 },
        { "mx.test; spf=softfail smtp.mailfrom=x@paypal.test", 10 },
        { "mx.test; dkim=fail header.d=paypal.test", 20 },
        { "mx.test; dkim=fail header.d=paypal.test; dkim=fail header.d=other.test", 20 },
        { "mx.test; spf=softfail smtp.mailfrom=x@paypal.test; dkim=fail header.d=paypal.test", 30 },
        { "mx.test; dmarc=fail header.from=paypal.test; spf=fail smtp.mailfrom=x@paypal.test", 50 },
        { "mx.test; dmarc=fail; spf=fail; dkim=fail", 50 },
        { "mx.test; spf=neutral smtp.mailfrom=x@paypal.test", null },
        { "mx.test; dkim=pass header.d=mailer.test", null },
        { "mx.test; dkim=fail header.d=paypal.test; dkim=pass header.d=mailer.test", null },
    };

    [Theory]
    [MemberData(nameof(AuthenticationPointsScenarios))]
    public void Authentication_signal_points(string header, int? expectedPoints)
    {
        ClassificationResult result = Classify(TestInputs.Create(headers: [header]));

        SignalResult? authentication = TestInputs.Find(result, "authentication");
        Assert.Equal(expectedPoints, authentication?.Points);
        Assert.Equal(expectedPoints ?? 0, result.Score);
    }

    // ---- contacts, allow list, block list ------------------------------------------------------------------

    // Case 15
    [Fact]
    public void Authenticated_contact_is_trusted_contact_with_override()
    {
        var directory = new FakeSenderDirectory().AddContact(Sender);

        ClassificationResult result = Classify(TestInputs.LegitAuthenticated(), directory);

        Assert.Equal(SpamTier.TrustedContact, result.Tier);
        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.False(string.IsNullOrWhiteSpace(result.Override));
        Assert.Contains(AuthenticatedHeaderMarker, result.Override, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Contact_lookup_is_case_insensitive_via_the_directory()
    {
        var directory = new FakeSenderDirectory().AddContact("SENDER_A@PAYPAL.TEST");

        ClassificationResult result = Classify(TestInputs.LegitAuthenticated(), directory);

        Assert.Equal(SpamTier.TrustedContact, result.Tier);
    }

    // Case 16 (positive control: fails if contacts bypass authentication)
    [Fact]
    public void Contact_with_failed_authentication_is_not_trusted_and_tier_follows_score()
    {
        var directory = new FakeSenderDirectory().AddContact(Sender);
        ClassificationInput input = TestInputs.Create(
            headers: ["mx.test; spf=fail smtp.mailfrom=bounce@paypal.test; dkim=fail header.d=paypal.test"]);

        ClassificationResult result = Classify(input, directory);

        Assert.Equal(AuthStatus.Failed, result.AuthStatus);
        Assert.NotEqual(SpamTier.TrustedContact, result.Tier);
        Assert.Equal(40, result.Score);
        Assert.Equal(SpamTier.LikelySpam, result.Tier);
        Assert.Null(result.Override);
        SignalResult authentication = Assert.IsType<SignalResult>(TestInputs.Find(result, "authentication"));
        Assert.Equal(40, authentication.Points);
        Assert.False(string.IsNullOrWhiteSpace(authentication.Reason));
    }

    [Fact]
    public void Contact_with_failed_authentication_and_high_score_is_certain_spam()
    {
        var directory = new FakeSenderDirectory().AddContact("offer@qzxkvb73.top");

        ClassificationResult result = Classify(TestInputs.ObviousSpam(), directory);

        Assert.Equal(SpamTier.CertainSpam, result.Tier);
        Assert.Null(result.Override);
    }

    // Case 17
    [Fact]
    public void Contact_without_header_is_capped_at_likely_legit_even_with_high_score()
    {
        var directory = new FakeSenderDirectory().AddContact("offer@qzxkvb73.top");
        ClassificationInput input = TestInputs.Create(
            from: "offer@qzxkvb73.top", fromName: "Offer", body: "Claim your prize now.", headers: []);

        ClassificationResult result = Classify(input, directory);

        Assert.True(result.Score >= ClassifierSettings.Default.LikelySpamThreshold, $"Score {result.Score} is too low for this case");
        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Equal(SpamTier.LikelyLegit, result.Tier);
        Assert.NotNull(result.Override);
        Assert.Contains("could not be verified", result.Override, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Contact_with_unknown_authentication_is_never_trusted_even_with_negative_score()
    {
        var directory = new FakeSenderDirectory().AddContact(Sender);

        ClassificationResult result = ClassifyWithStubs(
            TestInputs.Create(), directory, new FixedPointsSignal("stub", -50));

        Assert.Equal(SpamTier.LikelyLegit, result.Tier);
        Assert.Contains("could not be verified", result.Override, StringComparison.OrdinalIgnoreCase);
    }

    // Case 19
    [Fact]
    public void Block_list_beats_contact_and_authentication()
    {
        var directory = new FakeSenderDirectory().AddContact(Sender).AddBlocked(Sender);

        ClassificationResult result = Classify(TestInputs.LegitAuthenticated(), directory);

        Assert.Equal(SpamTier.CertainSpam, result.Tier);
        Assert.NotNull(result.Override);
        Assert.Contains("block list", result.Override, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
    }

    [Fact]
    public void Signals_and_score_are_still_returned_when_an_override_decides()
    {
        var directory = new FakeSenderDirectory().AddBlocked(Sender);

        ClassificationResult result = ClassifyWithStubs(
            TestInputs.Create(), directory, new FixedPointsSignal("stub-a", 5), new FixedPointsSignal("stub-b", 7));

        Assert.Equal(SpamTier.CertainSpam, result.Tier);
        Assert.Equal(12, result.Score);
        Assert.Equal(new[] { "stub-a", "stub-b" }, result.Signals.Select(signal => signal.SignalId).ToArray());
    }

    // Case 20
    [Fact]
    public void Allow_list_with_unknown_authentication_is_trusted_contact()
    {
        var directory = new FakeSenderDirectory().AddAllowed(Sender);

        ClassificationResult result = Classify(TestInputs.Create(), directory);

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Equal(SpamTier.TrustedContact, result.Tier);
        Assert.NotNull(result.Override);
        Assert.Contains("allow list", result.Override, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allow_list_with_authenticated_sender_is_trusted_contact()
    {
        var directory = new FakeSenderDirectory().AddAllowed(Sender);

        ClassificationResult result = Classify(TestInputs.LegitAuthenticated(), directory);

        Assert.Equal(SpamTier.TrustedContact, result.Tier);
        Assert.Contains("allow list", result.Override, StringComparison.OrdinalIgnoreCase);
    }

    // Case 20
    [Fact]
    public void Allow_list_with_failed_authentication_falls_through_to_the_score()
    {
        var directory = new FakeSenderDirectory().AddAllowed(Sender);
        ClassificationInput input = TestInputs.Create(
            headers: ["mx.test; dmarc=fail header.from=paypal.test; spf=fail smtp.mailfrom=x@paypal.test"]);

        ClassificationResult result = Classify(input, directory);

        Assert.Equal(AuthStatus.Failed, result.AuthStatus);
        Assert.Equal(50, result.Score);
        Assert.Equal(SpamTier.LikelySpam, result.Tier);
        Assert.Null(result.Override);
    }

    [Fact]
    public void Non_contact_without_lists_uses_the_score_without_override()
    {
        ClassificationResult result = Classify(TestInputs.ObviousSpam());

        Assert.Null(result.Override);
    }

    // ---- case 26: thresholds -------------------------------------------------------------------------------

    // Case 26
    [Theory]
    [InlineData(-10, SpamTier.LikelyLegit)]
    [InlineData(0, SpamTier.LikelyLegit)]
    [InlineData(29, SpamTier.LikelyLegit)]
    [InlineData(30, SpamTier.LikelySpam)]
    [InlineData(69, SpamTier.LikelySpam)]
    [InlineData(70, SpamTier.CertainSpam)]
    [InlineData(500, SpamTier.CertainSpam)]
    public void Default_thresholds_decide_the_tier(int points, SpamTier expected)
    {
        ClassificationResult result = ClassifyWithStubs(TestInputs.Create(), null, new FixedPointsSignal("stub", points));

        Assert.Equal(points, result.Score);
        Assert.Equal(expected, result.Tier);
        Assert.Null(result.Override);
    }

    [Theory]
    [InlineData(9, SpamTier.LikelyLegit)]
    [InlineData(10, SpamTier.LikelySpam)]
    [InlineData(19, SpamTier.LikelySpam)]
    [InlineData(20, SpamTier.CertainSpam)]
    public void Custom_thresholds_are_used(int points, SpamTier expected)
    {
        ClassifierSettings settings = ClassifierSettings.Default with { LikelySpamThreshold = 10, CertainSpamThreshold = 20 };
        var classifier = new SpamClassifier(settings, new FakeSenderDirectory(), [new FixedPointsSignal("stub", points)]);

        Assert.Equal(expected, classifier.Classify(TestInputs.Create()).Tier);
    }

    [Fact]
    public void Score_is_the_sum_of_all_signals_and_null_results_are_omitted()
    {
        ClassificationResult result = ClassifyWithStubs(
            TestInputs.Create(),
            null,
            new FixedPointsSignal("first", 20),
            new FixedPointsSignal("silent", null),
            new FixedPointsSignal("second", 15),
            new FixedPointsSignal("third", -5));

        Assert.Equal(30, result.Score);
        Assert.Equal(SpamTier.LikelySpam, result.Tier);
        Assert.Equal(new[] { "first", "second", "third" }, result.Signals.Select(signal => signal.SignalId).ToArray());
    }

    [Fact]
    public void Classifier_without_signals_yields_score_zero()
    {
        ClassificationResult result = ClassifyWithStubs(TestInputs.Create(), null);

        Assert.Equal(0, result.Score);
        Assert.Empty(result.Signals);
        Assert.Equal(SpamTier.LikelyLegit, result.Tier);
    }

    // ---- cases 28, 29 --------------------------------------------------------------------------------------

    // Case 28
    [Fact]
    public void Duplicate_signal_ids_are_rejected_by_the_constructor()
    {
        ISpamSignal[] signals = [new FixedPointsSignal("same", 1), new FixedPointsSignal("same", 2)];

        Assert.ThrowsAny<ArgumentException>(
            () => new SpamClassifier(ClassifierSettings.Default, new FakeSenderDirectory(), signals));
    }

    // Case 28 (C5): ids are compared case-insensitively
    [Fact]
    public void Duplicate_signal_ids_differing_only_in_case_are_rejected()
    {
        ISpamSignal[] signals = [new FixedPointsSignal("a", 1), new FixedPointsSignal("A", 2)];

        Assert.ThrowsAny<ArgumentException>(
            () => new SpamClassifier(ClassifierSettings.Default, new FakeSenderDirectory(), signals));
    }

    // Case 10a (C9): middle tier of random-domain, 0.5 <= score < 0.8 gives +15
    [Fact]
    public void Middle_tier_random_label_gives_fifteen_points()
    {
        const string label = "wrtplkjo";
        double score = RandomDomainScorer.Score(label);
        Assert.InRange(score, 0.5, 0.7999999);

        ClassificationResult result = TestInputs.CreateClassifier().Classify(
            TestInputs.Create(from: $"sender_a@{label}.test", body: $"Message from {label}."));

        SignalResult? finding = TestInputs.Find(result, "random-domain");
        Assert.NotNull(finding);
        Assert.Equal(15, finding.Points);
        Assert.Contains(label, finding.Reason);
    }

    // Case 21a (C11): a DMARC fail is not rescued by an aligned SPF pass
    [Fact]
    public void Dmarc_fail_beats_aligned_spf_pass()
    {
        ClassificationInput input = TestInputs.Create(
            from: "sender_a@example.test",
            body: "Mail from example",
            headers: ["mx.test; spf=pass smtp.mailfrom=example.test; dmarc=fail header.from=example.test"]);

        ClassificationResult result = Classify(input);

        Assert.Equal(AuthStatus.Failed, result.AuthStatus);
        Assert.True(TestInputs.Find(result, "authentication")?.Points > 0);
    }

    // Case 21a (C11): a contact with that header is not trusted
    [Fact]
    public void Contact_with_dmarc_fail_and_aligned_spf_pass_is_not_trusted()
    {
        var directory = new FakeSenderDirectory().AddContact("sender_a@example.test");
        ClassificationInput input = TestInputs.Create(
            from: "sender_a@example.test",
            body: "Mail from example",
            headers: ["mx.test; spf=pass smtp.mailfrom=example.test; dmarc=fail header.from=example.test"]);

        ClassificationResult result = Classify(input, directory);

        Assert.Equal(AuthStatus.Failed, result.AuthStatus);
        Assert.NotEqual(SpamTier.TrustedContact, result.Tier);
        Assert.Null(result.Override);
    }

    // Case 22a (C12): DMARC pass for a foreign header.from does not authenticate
    [Fact]
    public void Dmarc_pass_for_a_foreign_domain_is_not_authenticated()
    {
        ClassificationInput input = TestInputs.Create(
            from: "sender_a@example.test",
            body: "Mail from example",
            headers: ["mx.test; dmarc=pass header.from=other.test"]);

        ClassificationResult result = Classify(input);

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Null(TestInputs.Find(result, "authentication"));
    }

    // Case 28a (C13): custom signals must report their own id and a non-empty reason
    private sealed class MisbehavingSignal(string id, string resultId, string reason) : ISpamSignal
    {
        public string Id { get; } = id;

        public SignalResult? Evaluate(ClassificationInput input, ClassificationContext context) =>
            new(resultId, 5, reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Signal_result_with_empty_reason_is_rejected(string reason)
    {
        SpamClassifier classifier = TestInputs.CreateStubClassifier(null, null, new MisbehavingSignal("stub", "stub", reason));

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => classifier.Classify(TestInputs.Create()));
        Assert.Contains("stub", exception.Message);
    }

    [Fact]
    public void Signal_result_with_a_different_signal_id_is_rejected()
    {
        SpamClassifier classifier = TestInputs.CreateStubClassifier(
            null, null, new MisbehavingSignal("stub", "other-id", "Some reason"));

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => classifier.Classify(TestInputs.Create()));
        Assert.Contains("stub", exception.Message);
    }

    // Case 29
    [Fact]
    public void Classify_null_throws_argument_null_exception()
    {
        SpamClassifier classifier = TestInputs.CreateClassifier();

        Assert.Throws<ArgumentNullException>(() => classifier.Classify(null!));
    }

    // Case 29
    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("")]
    public void Classify_from_address_without_at_sign_throws_argument_exception(string fromAddress)
    {
        SpamClassifier classifier = TestInputs.CreateClassifier();

        Assert.ThrowsAny<ArgumentException>(() => classifier.Classify(TestInputs.Create(from: fromAddress)));
    }
}
