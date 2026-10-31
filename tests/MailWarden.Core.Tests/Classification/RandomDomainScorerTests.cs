using System.Globalization;

namespace MailWarden.Core.Tests.Classification;

[Trait("Category", "feature:classifier")]
public sealed class RandomDomainScorerTests
{
    // Case 10
    [Theory]
    [InlineData("qzxkvb73")]
    [InlineData("xkqzvbnm")]
    [InlineData("bvtrkzpq")]
    [InlineData("a8f3k2l9m")]
    [InlineData("zqwxjkvh")]
    [InlineData("hjkdfgtrw")]
    [InlineData("x7k2p9q4")]
    [InlineData("mnbvcxzl")]
    [InlineData("wqzrtpx8")]
    [InlineData("kjhgfdsqw")]
    public void Random_labels_score_at_least_0_8(string label)
    {
        double score = RandomDomainScorer.Score(label);

        Assert.True(score >= 0.8, $"{label} scored {score}");
    }

    // Case 11
    [Theory]
    [InlineData("hotmail")]
    [InlineData("googlemail")]
    [InlineData("amazon")]
    [InlineData("paypal")]
    [InlineData("sparkasse")]
    [InlineData("telekom")]
    [InlineData("check24")]
    [InlineData("zalando")]
    [InlineData("switchup")]
    [InlineData("bundeswehr")]
    [InlineData("lieferando")]
    [InlineData("netflix")]
    [InlineData("spotify")]
    [InlineData("mediamarkt")]
    [InlineData("kfzteile24")]
    [InlineData("schwarzkopf")]
    [InlineData("ebay-kleinanzeigen")]
    [InlineData("versicherung")]
    [InlineData("rechtsanwalt")]
    [InlineData("1und1")]
    public void Plausible_labels_score_below_0_5(string label)
    {
        double score = RandomDomainScorer.Score(label);

        Assert.True(score < 0.5, $"{label} scored {score}");
    }

    // Case 12
    [Theory]
    [InlineData("dhl")]
    [InlineData("gmx")]
    [InlineData("dkb")]
    [InlineData("xkcd")]
    [InlineData("")]
    [InlineData("a-b-c-d")]
    [InlineData("x-q-z-")]
    public void Short_labels_score_zero(string label)
    {
        Assert.Equal(0.0, RandomDomainScorer.Score(label));
    }

    [Theory]
    [InlineData("qzxkvb73")]
    [InlineData("paypal")]
    [InlineData("ebay-kleinanzeigen")]
    [InlineData("a8f3k2l9m")]
    [InlineData("aaaaa")]
    [InlineData("1234567890")]
    public void Score_is_within_unit_range_and_deterministic(string label)
    {
        double first = RandomDomainScorer.Score(label);
        double second = RandomDomainScorer.Score(label);

        Assert.InRange(first, 0.0, 1.0);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Score_is_culture_invariant()
    {
        string[] labels = ["lieferando", "mediamarkt", "qzxkvb73", "kjhgfdsqw", "irish"];
        double[] expected = labels.Select(RandomDomainScorer.Score).ToArray();
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            double[] actual = labels.Select(RandomDomainScorer.Score).ToArray();

            Assert.Equal(expected, actual);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
