namespace MailWarden.Core.Tests.Classification;

[Trait("Category", "feature:classifier")]
public sealed class ClassifierSmokeTests
{
    private static SpamClassifier CreateProductionLikeClassifier() =>
        new(ClassifierSettings.Default, new FakeSenderDirectory(), SpamClassifier.CreateDefaultSignals());

    // Case 31 (case 13 through the public composition)
    [Fact]
    [Trait("Category", "smoke")]
    public void Default_classifier_rates_authenticated_plausible_sender_as_likely_legit()
    {
        ClassificationResult result = CreateProductionLikeClassifier().Classify(TestInputs.LegitAuthenticated());

        Assert.Equal(SpamTier.LikelyLegit, result.Tier);
        Assert.Equal(-20, result.Score);
    }

    // Case 31 (case 14 through the public composition)
    [Fact]
    [Trait("Category", "smoke")]
    public void Default_classifier_rates_obvious_spam_as_certain_spam()
    {
        ClassificationResult result = CreateProductionLikeClassifier().Classify(TestInputs.ObviousSpam());

        Assert.Equal(SpamTier.CertainSpam, result.Tier);
        Assert.Equal(120, result.Score);
    }
}
