namespace MailWarden.Core.Tests.Classification;

/// <summary>Case 30: every reason of the built-in signals is non-empty and names the concrete value judged.</summary>
[Trait("Category", "feature:classifier")]
public sealed class SignalReasonTests
{
    private static ClassificationResult Classify(ClassificationInput input) => TestInputs.CreateClassifier().Classify(input);

    private static string ReasonOf(ClassificationResult result, string signalId)
    {
        SignalResult signal = Assert.IsType<SignalResult>(TestInputs.Find(result, signalId));
        return signal.Reason;
    }

    private static void AssertContainsAny(string reason, params string[] values)
    {
        Assert.True(
            values.Any(value => reason.Contains(value, StringComparison.OrdinalIgnoreCase)),
            $"Reason '{reason}' contains none of: {string.Join(", ", values)}");
    }

    public static TheoryData<string> ScenarioNames() => new() { "13", "14", "16", "21", "24", "25a", "25b", "contact-unknown" };

    private static ClassificationInput ScenarioInput(string name) => name switch
    {
        "13" => TestInputs.LegitAuthenticated(),
        "14" => TestInputs.ObviousSpam(),
        "16" => TestInputs.Create(headers: ["mx.test; spf=fail smtp.mailfrom=bounce@paypal.test; dkim=fail header.d=paypal.test"]),
        "21" => TestInputs.Create(headers: [AuthHeaders.AllFail("paypal.test"), AuthHeaders.AllPass("paypal.test")]),
        "24" => TestInputs.Create(from: "x@qzxkvb73.top", fromName: "PayPal Service <service@paypal.test>"),
        "25a" => TestInputs.Create(from: "billing@example.test", body: "example", replyTo: "collect@gmail.com"),
        "25b" => TestInputs.Create(from: "billing@example.test", body: "example", replyTo: "a@other.test"),
        "contact-unknown" => TestInputs.Create(from: "offer@qzxkvb73.top", body: "hi", links: ["a.other.test"]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // Case 30
    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Every_reason_is_a_non_empty_text(string scenario)
    {
        ClassificationResult result = Classify(ScenarioInput(scenario));

        Assert.NotEmpty(result.Signals);
        foreach (SignalResult signal in result.Signals)
        {
            Assert.False(string.IsNullOrWhiteSpace(signal.Reason), $"Signal '{signal.SignalId}' has an empty reason");
            Assert.False(string.IsNullOrWhiteSpace(signal.SignalId));
        }
    }

    // Case 30a (C14): sender-controlled values are truncated in reasons
    [Fact]
    public void Reasons_stay_short_for_very_long_sender_values()
    {
        string longToken = new('a', 1000);
        ClassificationInput input = TestInputs.Create(
            from: "x@qzxkvb73.top",
            fromName: $"Support {longToken}.other.test",
            replyTo: $"help@{longToken}.reply.test",
            body: "qzxkvb73");

        ClassificationResult result = Classify(input);

        Assert.NotNull(TestInputs.Find(result, "display-name-mismatch"));
        Assert.NotNull(TestInputs.Find(result, "reply-to-mismatch"));
        foreach (SignalResult signal in result.Signals)
        {
            Assert.True(
                signal.Reason.Length <= 200,
                $"Reason of '{signal.SignalId}' has {signal.Reason.Length} characters");
        }
    }

    // Case 30
    [Fact]
    public void Authentication_reasons_name_the_domain_or_the_method()
    {
        string passReason = ReasonOf(Classify(TestInputs.LegitAuthenticated()), "authentication");
        string failReason = ReasonOf(Classify(TestInputs.ObviousSpam()), "authentication");

        AssertContainsAny(passReason, "paypal.test", "DMARC", "DKIM", "SPF");
        AssertContainsAny(failReason, "qzxkvb73.top", "DMARC", "DKIM", "SPF");
    }

    // Case 30
    [Fact]
    public void Random_domain_reason_names_the_label()
    {
        string reason = ReasonOf(Classify(TestInputs.ObviousSpam()), "random-domain");

        Assert.Contains("qzxkvb73", reason, StringComparison.OrdinalIgnoreCase);
    }

    // Case 30
    [Fact]
    public void Suspicious_tld_reason_names_the_tld()
    {
        string reason = ReasonOf(Classify(TestInputs.ObviousSpam()), "suspicious-tld");

        Assert.Contains("top", reason, StringComparison.OrdinalIgnoreCase);
    }

    // Case 30
    [Fact]
    public void Domain_not_in_body_reason_names_the_label_or_domain()
    {
        string reason = ReasonOf(Classify(TestInputs.ObviousSpam()), "domain-not-in-body");

        AssertContainsAny(reason, "qzxkvb73");
    }

    // Case 30
    [Fact]
    public void Link_reason_names_a_host_or_the_sender_domain()
    {
        string reason = ReasonOf(Classify(TestInputs.ObviousSpam()), "link-domain-mismatch");

        AssertContainsAny(reason, "qzxkvb73", "tracker.other.test", "another.test", "other.test");
    }

    // Case 30
    [Fact]
    public void Display_name_reason_names_the_foreign_domain()
    {
        string reason = ReasonOf(Classify(ScenarioInput("24")), "display-name-mismatch");

        Assert.Contains("paypal.test", reason, StringComparison.OrdinalIgnoreCase);
    }

    // Case 30
    [Fact]
    public void Reply_to_reason_names_the_reply_to_domain()
    {
        string freemailReason = ReasonOf(Classify(ScenarioInput("25a")), "reply-to-mismatch");
        string foreignReason = ReasonOf(Classify(ScenarioInput("25b")), "reply-to-mismatch");

        AssertContainsAny(freemailReason, "gmail.com");
        AssertContainsAny(foreignReason, "other.test");
    }

    // Case 30 / failed contact (case 16): the authentication finding carries its reason
    [Fact]
    public void Failed_authentication_reason_is_present_for_contacts()
    {
        var directory = new FakeSenderDirectory().AddContact("sender_a@paypal.test");

        ClassificationResult result = TestInputs.CreateClassifier(directory).Classify(ScenarioInput("16"));

        string reason = ReasonOf(result, "authentication");
        AssertContainsAny(reason, "paypal.test", "SPF", "DKIM");
    }
}
