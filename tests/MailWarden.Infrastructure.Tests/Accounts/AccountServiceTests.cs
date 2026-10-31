using MailWarden.Core.Accounts;

namespace MailWarden.Infrastructure.Tests.Accounts;

[Trait("Category", "feature:accounts")]
public sealed class AccountServiceTests
{
    private static async Task<Account> StoreAccountAsync(StorageFixture storage, string address = "user_a@example.test")
    {
        Account account = TestData.NewAccount(address, address, MailProvider.Microsoft);
        await storage.Accounts.UpsertAsync(account);
        return account;
    }

    // Case 20
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task AddAccount_stores_account_with_username_as_name_and_address()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var fake = new FakeTokenProvider();
        fake.EnqueueSignIn("user_a@example.test");
        var service = new AccountService(storage.Accounts, [fake]);

        Account account = await service.AddAccountAsync(MailProvider.Microsoft, "hint@example.test");

        Assert.NotEqual(Guid.Empty, account.Id);
        Assert.Equal("user_a@example.test", account.EmailAddress);
        Assert.Equal("user_a@example.test", account.DisplayName);
        Assert.Equal(MailProvider.Microsoft, account.Provider);
        Assert.Equal(account, await storage.Accounts.GetAsync(account.Id));
    }

    // Case 21
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task AddAccount_again_with_different_case_returns_existing_account()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var fake = new FakeTokenProvider();
        fake.EnqueueSignIn("user_a@example.test");
        fake.EnqueueSignIn("USER_A@example.test");
        var service = new AccountService(storage.Accounts, [fake]);

        Account first = await service.AddAccountAsync(MailProvider.Microsoft, null);
        Account second = await service.AddAccountAsync(MailProvider.Microsoft, null);

        Assert.Equal(first, second);
        Assert.Single(await storage.Accounts.ListAsync());
    }

    // Case 22
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task AddAccount_for_GenericImap_throws_NotSupported()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var service = new AccountService(storage.Accounts, [new FakeTokenProvider()]);

        await Assert.ThrowsAsync<NotSupportedException>(() => service.AddAccountAsync(MailProvider.GenericImap, null));
        Assert.Empty(await storage.Accounts.ListAsync());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task AddAccount_for_provider_without_token_provider_throws_NotSupported()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var service = new AccountService(storage.Accounts, [new FakeTokenProvider(MailProvider.Microsoft)]);

        await Assert.ThrowsAsync<NotSupportedException>(() => service.AddAccountAsync(MailProvider.Google, null));
        Assert.Empty(await storage.Accounts.ListAsync());
    }

    // Case 23
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Constructor_rejects_two_providers_for_same_MailProvider()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();

        Assert.ThrowsAny<ArgumentException>(() => new AccountService(
            storage.Accounts,
            [new FakeTokenProvider(MailProvider.Google), new FakeTokenProvider(MailProvider.Google)]));
    }

    // Case 24
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task GetAccessToken_for_unknown_id_throws_ArgumentException()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var service = new AccountService(storage.Accounts, [new FakeTokenProvider()]);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.GetAccessTokenAsync(Guid.NewGuid()));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task GetAccessToken_returns_token_for_stored_account()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await StoreAccountAsync(storage);
        var service = new AccountService(storage.Accounts, [new FakeTokenProvider()]);

        AccessToken token = await service.GetAccessTokenAsync(account.Id);

        Assert.Equal("user_a@example.test", token.Username);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task GetAccessToken_propagates_InteractionRequired_unchanged()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await StoreAccountAsync(storage);
        var thrown = new AuthenticationFailedException(MailProvider.Microsoft, AuthFailureReason.InteractionRequired, "Sign in again.");
        var fake = new FakeTokenProvider { SilentException = thrown };
        var service = new AccountService(storage.Accounts, [fake]);

        AuthenticationFailedException caught =
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => service.GetAccessTokenAsync(account.Id));

        Assert.Same(thrown, caught);
        Assert.Equal(AuthFailureReason.InteractionRequired, caught.Reason);
    }

    // Case 25
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reauthenticate_with_other_account_throws_AccessDenied_and_signs_out_the_new_grant()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await StoreAccountAsync(storage);
        var fake = new FakeTokenProvider();
        fake.EnqueueSignIn("other@example.test");
        var service = new AccountService(storage.Accounts, [fake]);

        AuthenticationFailedException exception =
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => service.ReauthenticateAsync(account.Id));

        Assert.Equal(AuthFailureReason.AccessDenied, exception.Reason);
        Assert.Contains("other@example.test", fake.SignedOutUsernames);
        Assert.DoesNotContain("user_a@example.test", fake.SignedOutUsernames);
        Assert.Equal(account, await storage.Accounts.GetAsync(account.Id));
    }

    // Case 25a (A3)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reauthenticate_with_grant_of_other_stored_account_throws_AccessDenied_without_sign_out()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await StoreAccountAsync(storage);
        Account secondAccount = await StoreAccountAsync(storage, "user_b@example.test");
        var fake = new FakeTokenProvider();
        fake.EnqueueSignIn("user_b@example.test");
        var service = new AccountService(storage.Accounts, [fake]);

        AuthenticationFailedException exception =
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => service.ReauthenticateAsync(account.Id));

        Assert.Equal(AuthFailureReason.AccessDenied, exception.Reason);
        Assert.Empty(fake.SignedOutUsernames);
        Assert.Equal(account, await storage.Accounts.GetAsync(account.Id));
        Assert.Equal(secondAccount, await storage.Accounts.GetAsync(secondAccount.Id));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reauthenticate_with_same_account_case_insensitive_returns_token_without_sign_out()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await StoreAccountAsync(storage);
        var fake = new FakeTokenProvider();
        fake.EnqueueSignIn("USER_A@example.test");
        var service = new AccountService(storage.Accounts, [fake]);

        AccessToken token = await service.ReauthenticateAsync(account.Id);

        Assert.Equal("USER_A@example.test", token.Username);
        Assert.Empty(fake.SignedOutUsernames);
    }

    // Case 26
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task RemoveAccount_signs_out_and_deletes_account()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await StoreAccountAsync(storage);
        var fake = new FakeTokenProvider();
        var service = new AccountService(storage.Accounts, [fake]);

        await service.RemoveAccountAsync(account.Id);

        Assert.Equal(["user_a@example.test"], fake.SignedOutUsernames);
        Assert.Null(await storage.Accounts.GetAsync(account.Id));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task RemoveAccount_with_unknown_id_is_noop_without_sign_out()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var fake = new FakeTokenProvider();
        var service = new AccountService(storage.Accounts, [fake]);

        await service.RemoveAccountAsync(Guid.NewGuid());

        Assert.Empty(fake.SignedOutUsernames);
    }
}
