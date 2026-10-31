using MailWarden.Core.Accounts;
using MailWarden.Infrastructure.Security;

namespace MailWarden.Infrastructure.Tests.Accounts;

[Trait("Category", "feature:accounts")]
[Trait("Category", "smoke")]
public sealed class AccountsSmokeTests
{
    // Case 27
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Add_get_token_remove_works_end_to_end_and_temp_root_is_removable()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "mailwarden-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            var credentials = new DpapiCredentialStore(Path.Combine(rootPath, "credentials"));
            await credentials.WriteAsync("msal.cache", "artificial-cache"u8.ToArray());
            await using (StorageFixture storage = await StorageFixture.CreateAsync())
            {
                var fake = new FakeTokenProvider();
                fake.EnqueueSignIn("user_a@example.test");
                var service = new AccountService(storage.Accounts, [fake]);

                Account account = await service.AddAccountAsync(MailProvider.Microsoft, null);
                AccessToken token = await service.GetAccessTokenAsync(account.Id);
                Assert.Equal("user_a@example.test", token.Username);

                await service.RemoveAccountAsync(account.Id);

                Assert.Empty(await storage.Accounts.ListAsync());
                Assert.Equal(["user_a@example.test"], fake.SignedOutUsernames);
            }

            await credentials.DeleteAsync("msal.cache");
            Assert.Empty(await credentials.ListKeysAsync(string.Empty));
            Directory.Delete(rootPath, recursive: true);
            Assert.False(Directory.Exists(rootPath));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
