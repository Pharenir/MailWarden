namespace MailWarden.Core.Tests.TestSupport;

/// <summary>Builders for classifier inputs and classifiers; all data is artificial.</summary>
internal static class TestInputs
{
    /// <summary>A plausible, non-freemail domain whose label is a natural word.</summary>
    public const string PlausibleDomain = "paypal.test";

    public static readonly IReadOnlyList<string> NoHeaders = [];

    public static ClassificationInput Create(
        string from = "sender_a@" + PlausibleDomain,
        string fromName = "Sender A",
        string? replyTo = null,
        IReadOnlyList<string>? headers = null,
        string body = "Your paypal statement is ready.",
        IReadOnlyList<string>? links = null) =>
        new(from, fromName, replyTo, headers ?? NoHeaders, body, links ?? []);

    public static SpamClassifier CreateClassifier(ISenderDirectory? directory = null) =>
        new(ClassifierSettings.Default, directory ?? new FakeSenderDirectory(), SpamClassifier.CreateDefaultSignals());

    public static SpamClassifier CreateStubClassifier(
        ISenderDirectory? directory, ClassifierSettings? settings, params ISpamSignal[] signals) =>
        new(settings ?? ClassifierSettings.Default, directory ?? new FakeSenderDirectory(), signals);

    /// <summary>Case 13 input: plausible domain, everything passes, body mentions the domain.</summary>
    public static ClassificationInput LegitAuthenticated() =>
        Create(headers: [AuthHeaders.AllPass(PlausibleDomain)]);

    /// <summary>Case 14 input: random .top domain, everything fails, body without the label, foreign links.</summary>
    public static ClassificationInput ObviousSpam() =>
        Create(
            from: "offer@qzxkvb73.top",
            fromName: "Offer",
            headers: [AuthHeaders.AllFail("qzxkvb73.top")],
            body: "Claim your prize now.",
            links: ["tracker.other.test", "cdn.another.test"]);

    public static SignalResult? Find(ClassificationResult result, string signalId) =>
        result.Signals.FirstOrDefault(signal => signal.SignalId == signalId);
}
