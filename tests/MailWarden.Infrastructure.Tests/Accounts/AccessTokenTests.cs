using MailWarden.Core.Accounts;

namespace MailWarden.Infrastructure.Tests.Accounts;

[Trait("Category", "feature:accounts")]
public sealed class AccessTokenTests
{
    // Case 19
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public void ToString_does_not_contain_token_value()
    {
        var token = new AccessToken("super-secret-token-value-123", DateTimeOffset.UtcNow, "user_a@example.test");

        Assert.DoesNotContain("super-secret-token-value-123", token.ToString(), StringComparison.Ordinal);
    }
}
