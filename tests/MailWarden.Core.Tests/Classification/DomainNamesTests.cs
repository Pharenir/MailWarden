namespace MailWarden.Core.Tests.Classification;

[Trait("Category", "feature:classifier")]
public sealed class DomainNamesTests
{
    // Case 7
    [Fact]
    public void GetDomain_normalizes_case_and_trailing_dot()
    {
        Assert.Equal("mail.example.test", DomainNames.GetDomain("A.User@Mail.Example.TEST."));
    }

    [Fact]
    public void GetDomain_uses_the_last_at_sign_and_trims()
    {
        Assert.Equal("example.test", DomainNames.GetDomain("  \"odd@local\"@Example.test  "));
    }

    // Case 7
    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("")]
    [InlineData("user@")]
    public void GetDomain_without_domain_throws_argument_exception(string address)
    {
        Assert.ThrowsAny<ArgumentException>(() => DomainNames.GetDomain(address));
    }

    // Case 7
    [Fact]
    public void GetDomain_null_throws_argument_null_exception()
    {
        Assert.Throws<ArgumentNullException>(() => DomainNames.GetDomain(null!));
    }

    // Case 8
    [Theory]
    [InlineData("mail.qzxkvb73.top", "qzxkvb73.top")]
    [InlineData("shop.example.co.uk", "example.co.uk")]
    [InlineData("example.test", "example.test")]
    [InlineData("localhost", "localhost")]
    [InlineData("a.b.c.example.de", "example.de")]
    [InlineData("Mail.Example.TEST", "example.test")]
    [InlineData("example.co.uk", "example.co.uk")]
    public void GetOrganizationalDomain_examples(string domain, string expected)
    {
        Assert.Equal(expected, DomainNames.GetOrganizationalDomain(domain));
    }

    // Case 8 (built-in multi-label suffix list, minimum set from the contract)
    [Theory]
    [InlineData("co.uk")]
    [InlineData("org.uk")]
    [InlineData("ac.uk")]
    [InlineData("gov.uk")]
    [InlineData("com.au")]
    [InlineData("net.au")]
    [InlineData("org.au")]
    [InlineData("co.jp")]
    [InlineData("co.nz")]
    [InlineData("com.br")]
    [InlineData("com.tr")]
    [InlineData("co.za")]
    [InlineData("com.cn")]
    public void Multi_label_public_suffixes_are_respected(string suffix)
    {
        string domain = $"www.shop.example.{suffix}";

        Assert.Equal($"example.{suffix}", DomainNames.GetOrganizationalDomain(domain));
        Assert.Equal("example", DomainNames.GetRegistrableLabel(domain));
        Assert.Equal(suffix, DomainNames.GetPublicSuffix(domain));
    }

    [Theory]
    [InlineData("mail.qzxkvb73.top", "qzxkvb73")]
    [InlineData("shop.example.co.uk", "example")]
    [InlineData("example.test", "example")]
    public void GetRegistrableLabel_examples(string domain, string expected)
    {
        Assert.Equal(expected, DomainNames.GetRegistrableLabel(domain));
    }

    [Theory]
    [InlineData("qzxkvb73.top", "top")]
    [InlineData("mail.example.de", "de")]
    [InlineData("example.co.uk", "co.uk")]
    [InlineData("shop.example.co.uk", "co.uk")]
    public void GetPublicSuffix_examples(string domain, string expected)
    {
        Assert.Equal(expected, DomainNames.GetPublicSuffix(domain));
    }

    // Case 9
    [Fact]
    public void AreAligned_subdomain_and_parent()
    {
        Assert.True(DomainNames.AreAligned("news.example.test", "example.test"));
        Assert.True(DomainNames.AreAligned("example.test", "news.example.test"));
    }

    // Case 9
    [Fact]
    public void AreAligned_different_organizational_domains_are_not_aligned()
    {
        Assert.False(DomainNames.AreAligned("example.test", "example-mail.test"));
        Assert.False(DomainNames.AreAligned("example.test", "example.other"));
    }

    [Fact]
    public void AreAligned_is_case_insensitive()
    {
        Assert.True(DomainNames.AreAligned("News.Example.TEST", "example.test"));
    }

    [Fact]
    public void AreAligned_respects_multi_label_suffixes()
    {
        Assert.True(DomainNames.AreAligned("a.example.co.uk", "b.example.co.uk"));
        Assert.False(DomainNames.AreAligned("example.co.uk", "other.co.uk"));
    }
}
