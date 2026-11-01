namespace MailWarden.Core.Tests.Classification;

/// <summary>
/// AP-07c fix.3 H5 / case 51: only ASCII whitespace and ASCII control characters separate tokens of an
/// Authentication-Results value. Bytes such as 0xA0 and 0x85 (from the Latin-1 raw value) stay inside the value.
/// </summary>
[Trait("Category", "feature:classifier")]
public sealed class NonAsciiSeparatorTests
{
    private const string Sender = "sender_a@" + TestInputs.PlausibleDomain;

    private static ClassificationResult Classify(string header, bool contact = true)
    {
        var directory = new FakeSenderDirectory();
        if (contact)
        {
            directory.AddContact(Sender);
        }

        return TestInputs.CreateClassifier(directory).Classify(TestInputs.Create(headers: [header]));
    }

    // Case 51
    [Theory]
    [InlineData("\u00A0")]
    [InlineData("\u0085")]
    [InlineData("\u0080")]
    [InlineData("\u009F")]
    public void Non_ascii_whitespace_does_not_split_a_forged_pass_out_of_the_value(string forgedSeparator)
    {
        string header = "mx.test; spf=fail smtp.mailfrom=bounce@paypal.test" + forgedSeparator + "dmarc=pass" + forgedSeparator
            + "header.from=paypal.test; dmarc=fail header.from=paypal.test";

        ClassificationResult result = Classify(header);

        Assert.Equal(AuthStatus.Failed, result.AuthStatus);
        Assert.NotEqual(SpamTier.TrustedContact, result.Tier);
        Assert.Null(result.Override);
    }

    // Case 51, positive control: ASCII space and tab still separate the properties
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Ascii_space_and_tab_still_separate_properties(string separator)
    {
        string header = "mx.test; dmarc=pass" + separator + "header.from=paypal.test";

        ClassificationResult result = Classify(header);

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Equal(SpamTier.TrustedContact, result.Tier);
    }

    // Case 51, positive control: with the same forged separator but an ASCII space the pass is really read
    [Fact]
    public void The_same_header_with_ascii_separators_is_read_as_a_pass()
    {
        const string header = "mx.test; dmarc=pass header.from=paypal.test";

        Assert.Equal(AuthStatus.Authenticated, Classify(header).AuthStatus);
    }
}
