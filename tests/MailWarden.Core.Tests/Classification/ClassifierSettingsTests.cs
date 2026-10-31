namespace MailWarden.Core.Tests.Classification;

[Trait("Category", "feature:classifier")]
public sealed class ClassifierSettingsTests
{
    private static ClassifierSettings Valid => ClassifierSettings.Default;

    public static TheoryData<string, ClassifierSettings> InvalidSettings() => new()
    {
        { "likely equals certain", Valid with { LikelySpamThreshold = 70, CertainSpamThreshold = 70 } },
        { "likely above certain", Valid with { LikelySpamThreshold = 80, CertainSpamThreshold = 70 } },
        { "likely zero", Valid with { LikelySpamThreshold = 0 } },
        { "likely negative", Valid with { LikelySpamThreshold = -5 } },
        { "certain zero", Valid with { LikelySpamThreshold = 0, CertainSpamThreshold = 0 } },
        { "certain negative", Valid with { LikelySpamThreshold = -10, CertainSpamThreshold = -1 } },
        { "empty tld entry", Valid with { SuspiciousTlds = new HashSet<string> { "top", "" } } },
        { "null tld entry", Valid with { SuspiciousTlds = new HashSet<string> { null! } } },
        { "empty freemail entry", Valid with { FreemailDomains = new HashSet<string> { "gmail.com", "" } } },
        { "null freemail entry", Valid with { FreemailDomains = new HashSet<string> { null! } } },
    };

    [Fact]
    public void Default_has_documented_thresholds()
    {
        Assert.Equal(30, ClassifierSettings.Default.LikelySpamThreshold);
        Assert.Equal(70, ClassifierSettings.Default.CertainSpamThreshold);
    }

    [Fact]
    public void Default_contains_documented_suspicious_tlds()
    {
        string[] expected = ["top", "xyz", "click", "loan", "work", "gq", "tk", "ml", "cf", "ga", "icu", "buzz", "rest", "cfd", "sbs"];

        Assert.Equal(expected.Length, ClassifierSettings.Default.SuspiciousTlds.Count);
        foreach (string tld in expected)
        {
            Assert.Contains(tld, ClassifierSettings.Default.SuspiciousTlds);
        }
    }

    [Fact]
    public void Default_contains_documented_freemail_domains()
    {
        string[] expected =
        [
            "gmail.com", "googlemail.com", "hotmail.com", "hotmail.de", "outlook.com", "outlook.de", "live.com",
            "live.de", "msn.com", "yahoo.com", "yahoo.de", "gmx.de", "gmx.net", "web.de", "t-online.de", "icloud.com",
            "me.com", "aol.com", "proton.me", "protonmail.com", "freenet.de",
        ];

        Assert.Equal(expected.Length, ClassifierSettings.Default.FreemailDomains.Count);
        foreach (string domain in expected)
        {
            Assert.Contains(domain, ClassifierSettings.Default.FreemailDomains);
        }
    }

    [Fact]
    public void Default_is_valid()
    {
        ClassifierSettings.Default.Validate();
    }

    [Fact]
    public void Validate_accepts_custom_valid_settings()
    {
        ClassifierSettings custom = Valid with { LikelySpamThreshold = 1, CertainSpamThreshold = 2 };

        custom.Validate();
    }

    // Case 27
    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Validate_rejects_invalid_settings(string description, ClassifierSettings settings)
    {
        Assert.False(string.IsNullOrEmpty(description));
        Assert.ThrowsAny<ArgumentException>(settings.Validate);
    }

    // Case 27
    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Classifier_constructor_rejects_invalid_settings(string description, ClassifierSettings settings)
    {
        Assert.False(string.IsNullOrEmpty(description));
        Assert.ThrowsAny<ArgumentException>(
            () => new SpamClassifier(settings, new FakeSenderDirectory(), SpamClassifier.CreateDefaultSignals()));
    }
}
