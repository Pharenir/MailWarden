namespace MailWarden.Infrastructure.Tests.Storage;

[Trait("Category", "feature:storage")]
public sealed class SqliteAccountStoreTests
{
    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_then_get_returns_equal_record()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = TestData.NewAccount("Account_A", "sender_a@example.test", MailProvider.Google);

        await storage.Accounts.UpsertAsync(account);

        Assert.Equal(account, await storage.Accounts.GetAsync(account.Id));
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_with_same_id_updates_display_name_without_adding_a_row()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account original = TestData.NewAccount("Account_A");
        await storage.Accounts.UpsertAsync(original);

        Account renamed = original with { DisplayName = "Account_Renamed" };
        await storage.Accounts.UpsertAsync(renamed);

        Assert.Equal(renamed, await storage.Accounts.GetAsync(original.Id));
        Assert.Single(await storage.Accounts.ListAsync());
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task List_is_ordered_by_display_name()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account accountC = await storage.AddAccountAsync("Account_C");
        Account accountA = await storage.AddAccountAsync("Account_A");
        Account accountB = await storage.AddAccountAsync("Account_B");

        IReadOnlyList<Account> listed = await storage.Accounts.ListAsync();

        Assert.Equal(new[] { accountA.Id, accountB.Id, accountC.Id }, listed.Select(a => a.Id).ToArray());
    }

    // Case 5 (supporting): unknown id
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Get_of_unknown_id_returns_null()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();

        Assert.Null(await storage.Accounts.GetAsync(Guid.NewGuid()));
    }

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_with_same_email_and_different_id_throws_StorageException()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        await storage.AddAccountAsync("Account_A", "sender_a@example.test");
        Account duplicate = TestData.NewAccount("Account_B", "sender_a@example.test");

        await Assert.ThrowsAnyAsync<StorageException>(() => storage.Accounts.UpsertAsync(duplicate));
    }

    // Case 6 (schema says email is NOCASE unique)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_with_same_email_in_different_case_throws_StorageException()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        await storage.AddAccountAsync("Account_A", "sender_a@example.test");
        Account duplicate = TestData.NewAccount("Account_B", "SENDER_A@EXAMPLE.TEST");

        await Assert.ThrowsAnyAsync<StorageException>(() => storage.Accounts.UpsertAsync(duplicate));
    }

    // Contract: unknown id = no-op
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_of_unknown_id_is_a_no_op()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();

        await storage.Accounts.DeleteAsync(Guid.NewGuid());

        Assert.Single(await storage.Accounts.ListAsync());
        Assert.NotNull(await storage.Accounts.GetAsync(account.Id));
    }

    // Case 22
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_cascades_to_folders_and_messages_and_leaves_other_accounts_untouched()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account doomed = await storage.AddAccountAsync("Account_A");
        Account survivor = await storage.AddAccountAsync("Account_B");
        MailFolder doomedFolder = await storage.AddFolderAsync(doomed.Id, FolderRole.Inbox);
        MailFolder survivorFolder = await storage.AddFolderAsync(survivor.Id, FolderRole.Inbox);
        MailMessage doomedMessage = await storage.AddMessageAsync(doomedFolder, 1);
        MailMessage survivorMessage = await storage.AddMessageAsync(survivorFolder, 1);

        await storage.Accounts.DeleteAsync(doomed.Id);

        Assert.Null(await storage.Accounts.GetAsync(doomed.Id));
        Assert.Empty(await storage.Folders.ListAsync(doomed.Id));
        Assert.Null(await storage.Folders.GetAsync(doomedFolder.Id));
        Assert.Null(await storage.Messages.GetAsync(doomedMessage.Id));

        Assert.NotNull(await storage.Accounts.GetAsync(survivor.Id));
        Assert.Single(await storage.Folders.ListAsync(survivor.Id));
        Assert.Equal(survivorMessage, await storage.Messages.GetAsync(survivorMessage.Id));
    }

    // Case 22 (D16): the cascade leaves no full-text rows of the deleted messages
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_removes_full_text_entries_of_the_deleted_messages_only()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account doomed = await storage.AddAccountAsync("Account_A");
        Account survivor = await storage.AddAccountAsync("Account_B");
        MailFolder doomedFolder = await storage.AddFolderAsync(doomed.Id, FolderRole.Inbox);
        MailFolder survivorFolder = await storage.AddFolderAsync(survivor.Id, FolderRole.Inbox);
        await storage.AddMessageAsync(doomedFolder, 1, bodyText: "zebrawordonly");
        MailMessage survivorMessage = await storage.AddMessageAsync(survivorFolder, 1, bodyText: "giraffewordkept");
        Assert.Single(await storage.Messages.SearchAsync("zebrawordonly"));

        await storage.Accounts.DeleteAsync(doomed.Id);

        Assert.Empty(await storage.Messages.SearchAsync("zebrawordonly"));
        MailMessage found = Assert.Single(await storage.Messages.SearchAsync("giraffewordkept"));
        Assert.Equal(survivorMessage.Id, found.Id);
    }
}
