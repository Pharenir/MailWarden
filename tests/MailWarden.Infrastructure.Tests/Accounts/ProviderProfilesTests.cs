using MailWarden.Infrastructure.Providers;

namespace MailWarden.Infrastructure.Tests.Accounts;

[Trait("Category", "feature:accounts")]
public sealed class ProviderProfilesTests
{
    // Case 14
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("a@hotmail.de", MailProvider.Microsoft)]
    [InlineData("a@Outlook.com", MailProvider.Microsoft)]
    [InlineData("a@live.de", MailProvider.Microsoft)]
    [InlineData("a@msn.com", MailProvider.Microsoft)]
    [InlineData("a@windowslive.com", MailProvider.Microsoft)]
    [InlineData("a@googlemail.com", MailProvider.Google)]
    [InlineData("a@GMAIL.com", MailProvider.Google)]
    public void DetectFromAddress_recognises_known_domains(string address, MailProvider expected)
    {
        Assert.Equal(expected, ProviderProfiles.DetectFromAddress(address));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public void DetectFromAddress_returns_null_for_unknown_domain()
    {
        Assert.Null(ProviderProfiles.DetectFromAddress("a@example.test"));
    }

    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("no-at")]
    [InlineData("")]
    public void DetectFromAddress_rejects_invalid_address(string address)
    {
        Assert.ThrowsAny<ArgumentException>(() => ProviderProfiles.DetectFromAddress(address));
    }

    // Case 15
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public void For_GenericImap_throws_NotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() => ProviderProfiles.For(MailProvider.GenericImap));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public void Microsoft_profile_has_contract_endpoints()
    {
        ProviderProfile profile = ProviderProfiles.For(MailProvider.Microsoft);

        Assert.Equal(MailProvider.Microsoft, profile.Provider);
        Assert.Equal(new ServerEndpoint("outlook.office365.com", 993, TransportSecurity.SslOnConnect), profile.Imap);
        Assert.Equal(new ServerEndpoint("smtp-mail.outlook.com", 587, TransportSecurity.StartTls), profile.Smtp);
        Assert.Equal(profile, ProviderProfiles.Microsoft);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public void Google_profile_has_contract_endpoints()
    {
        ProviderProfile profile = ProviderProfiles.For(MailProvider.Google);

        Assert.Equal(MailProvider.Google, profile.Provider);
        Assert.Equal(new ServerEndpoint("imap.gmail.com", 993, TransportSecurity.SslOnConnect), profile.Imap);
        Assert.Equal(new ServerEndpoint("smtp.gmail.com", 587, TransportSecurity.StartTls), profile.Smtp);
        Assert.Equal(profile, ProviderProfiles.Google);
    }
}
