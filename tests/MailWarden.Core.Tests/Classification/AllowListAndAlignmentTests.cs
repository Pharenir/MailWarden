namespace MailWarden.Core.Tests.Classification;

/// <summary>
/// AP-06 fix.2 R1 / case 20 (allow list needs an authenticated sender) and R2 / case 21 (DKIM and SPF trust needs the
/// same domain or a real parent or child relation to the From domain, not the organisational domain).
/// </summary>
[Trait("Category", "feature:classifier")]
[Trait("Category", "feature:contacts")]
public sealed class AllowListAndAlignmentTests
{
    private const string Sender = "sender_a@" + TestInputs.PlausibleDomain;

    private static ClassificationResult Classify(ClassificationInput input, ISenderDirectory directory) =>
        TestInputs.CreateClassifier(directory).Classify(input);

    // Case 20
    [Fact]
    [Trait("Category", "smoke")]
    public void Allowed_sender_with_authenticated_mail_is_trusted_contact()
    {
        ClassificationResult result = Classify(TestInputs.LegitAuthenticated(), new FakeSenderDirectory().AddAllowed(Sender));

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Equal(SpamTier.TrustedContact, result.Tier);
        Assert.Contains("allow list", result.Override, StringComparison.OrdinalIgnoreCase);
    }

    // Case 20: every header that does not authenticate gives at most LikelyLegit
    [Theory]
    [InlineData("")]
    [InlineData("mx.test; spf=softfail smtp.mailfrom=x@paypal.test; dmarc=none header.from=paypal.test")]
    [InlineData("mx.test; dmarc=fail header.from=paypal.test; compauth=pass reason=100")]
    [InlineData("mx.test; spf=neutral smtp.mailfrom=bounce@paypal.test")]
    public void Allowed_sender_without_proven_authentication_is_likely_legit_and_the_override_names_the_allow_list(string header)
    {
        IReadOnlyList<string> headers = header.Length == 0 ? [] : [header];
        ClassificationResult result = Classify(TestInputs.Create(headers: headers), new FakeSenderDirectory().AddAllowed(Sender));

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Equal(SpamTier.LikelyLegit, result.Tier);
        Assert.NotNull(result.Override);
        Assert.Contains("allow list", result.Override, StringComparison.OrdinalIgnoreCase);
    }

    // Case 20: a contact in the same situation is also at most LikelyLegit (the allow list is not better than a contact)
    [Fact]
    public void Allowed_sender_with_unknown_authentication_is_treated_like_a_contact()
    {
        ClassificationResult allowed = Classify(TestInputs.Create(), new FakeSenderDirectory().AddAllowed(Sender));
        ClassificationResult contact = Classify(TestInputs.Create(), new FakeSenderDirectory().AddContact(Sender));

        Assert.Equal(contact.Tier, allowed.Tier);
    }

    // Case 20: failed authentication is as before
    [Fact]
    public void Allowed_sender_with_failed_authentication_falls_through_to_the_score()
    {
        ClassificationInput input = TestInputs.Create(
            headers: ["mx.test; dmarc=fail header.from=paypal.test; spf=fail smtp.mailfrom=x@paypal.test"]);

        ClassificationResult result = Classify(input, new FakeSenderDirectory().AddAllowed(Sender));

        Assert.Equal(AuthStatus.Failed, result.AuthStatus);
        Assert.Equal(SpamTier.LikelySpam, result.Tier);
        Assert.Null(result.Override);
    }

    private static ClassificationResult ClassifyFirma(string header, string from = "person_a@firma.com.pl")
    {
        var directory = new FakeSenderDirectory().AddContact(from);
        return Classify(TestInputs.Create(from: from, body: "firma", headers: [header]), directory);
    }

    // Case 21: no alignment through the shared public suffix
    [Theory]
    [InlineData("mx.test; dkim=pass header.d=evil.com.pl")]
    [InlineData("mx.test; spf=pass smtp.mailfrom=bounce@evil.com.pl")]
    public void Dkim_or_spf_pass_for_another_domain_below_the_same_suffix_is_not_authenticated(string header)
    {
        ClassificationResult result = ClassifyFirma(header);

        Assert.NotEqual(AuthStatus.Authenticated, result.AuthStatus);
        Assert.NotEqual(SpamTier.TrustedContact, result.Tier);
    }

    // Case 21: positive controls, same domain, child, parent, DMARC
    [Theory]
    [InlineData("mx.test; dkim=pass header.d=firma.com.pl", "person_a@firma.com.pl")]
    [InlineData("mx.test; spf=pass smtp.mailfrom=bounce@firma.com.pl", "person_a@firma.com.pl")]
    [InlineData("mx.test; dkim=pass header.d=mail.firma.com.pl", "person_a@firma.com.pl")]
    [InlineData("mx.test; spf=pass smtp.mailfrom=bounce@mail.firma.com.pl", "person_a@firma.com.pl")]
    [InlineData("mx.test; dkim=pass header.d=firma.com.pl", "a@mail.firma.com.pl")]
    [InlineData("mx.test; dmarc=pass header.from=firma.com.pl", "person_a@firma.com.pl")]
    public void Same_domain_parent_or_child_and_dmarc_pass_are_authenticated(string header, string from)
    {
        ClassificationResult result = ClassifyFirma(header, from);

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Equal(SpamTier.TrustedContact, result.Tier);
    }

    // Case 21: two sibling sub-domains are not related
    [Fact]
    public void Dkim_pass_for_a_sibling_sub_domain_is_not_authenticated()
    {
        ClassificationResult result = ClassifyFirma("mx.test; dkim=pass header.d=news.firma.com.pl", "a@mail.firma.com.pl");

        Assert.NotEqual(AuthStatus.Authenticated, result.AuthStatus);
    }
}
