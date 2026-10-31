namespace MailWarden.Infrastructure.Tests.Storage;

[Trait("Category", "feature:storage")]
public sealed class SqliteFolderStoreTests
{
    // Case 7
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_returns_copy_with_positive_id()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();

        MailFolder stored = await storage.Folders.UpsertAsync(
            TestData.NewFolder(account.Id, "INBOX", FolderRole.Inbox, 4242));

        Assert.True(stored.Id > 0);
        Assert.Equal(account.Id, stored.AccountId);
        Assert.Equal("INBOX", stored.FullName);
        Assert.Equal(FolderRole.Inbox, stored.Role);
        Assert.Equal(4242u, stored.UidValidity);
        Assert.Equal(stored, await storage.Folders.GetAsync(stored.Id));
    }

    // Case 7
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_with_same_account_and_name_keeps_id_and_updates_values()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder first = await storage.Folders.UpsertAsync(
            TestData.NewFolder(account.Id, "Archive_X", FolderRole.None, 1));

        MailFolder second = await storage.Folders.UpsertAsync(
            TestData.NewFolder(account.Id, "Archive_X", FolderRole.Archive, 2));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(FolderRole.Archive, second.Role);
        Assert.Equal(2u, second.UidValidity);
        Assert.Equal(second, await storage.Folders.GetAsync(first.Id));
        Assert.Single(await storage.Folders.ListAsync(account.Id));
    }

    // Case 7
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Same_name_under_second_account_gets_a_different_id()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account accountA = await storage.AddAccountAsync("Account_A");
        Account accountB = await storage.AddAccountAsync("Account_B");

        MailFolder folderA = await storage.Folders.UpsertAsync(TestData.NewFolder(accountA.Id, "INBOX"));
        MailFolder folderB = await storage.Folders.UpsertAsync(TestData.NewFolder(accountB.Id, "INBOX"));

        Assert.NotEqual(folderA.Id, folderB.Id);
    }

    // Case 8
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_for_unknown_account_throws_StorageException()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();

        await Assert.ThrowsAnyAsync<StorageException>(
            () => storage.Folders.UpsertAsync(TestData.NewFolder(Guid.NewGuid())));
    }

    // Contract: ordered by FullName
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task List_is_ordered_by_full_name_and_limited_to_the_account()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync("Account_A");
        Account other = await storage.AddAccountAsync("Account_B");
        await storage.AddFolderAsync(account.Id, FolderRole.None, "folder_c");
        await storage.AddFolderAsync(account.Id, FolderRole.None, "folder_a");
        await storage.AddFolderAsync(account.Id, FolderRole.None, "folder_b");
        await storage.AddFolderAsync(other.Id, FolderRole.None, "folder_other");

        IReadOnlyList<MailFolder> listed = await storage.Folders.ListAsync(account.Id);

        Assert.Equal(new[] { "folder_a", "folder_b", "folder_c" }, listed.Select(f => f.FullName).ToArray());
    }

    // AP-00 finding: role is stored explicitly, never derived from the name
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Role_is_stored_as_given_and_not_derived_from_name()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();

        MailFolder junkNamedUserFolder = await storage.Folders.UpsertAsync(
            TestData.NewFolder(account.Id, "Junk-E-Mail", FolderRole.None));
        MailFolder spamFolder = await storage.Folders.UpsertAsync(
            TestData.NewFolder(account.Id, "folder_named_arbitrarily", FolderRole.Junk));

        Assert.Equal(FolderRole.None, (await storage.Folders.GetAsync(junkNamedUserFolder.Id))!.Role);
        Assert.Equal(FolderRole.Junk, (await storage.Folders.GetAsync(spamFolder.Id))!.Role);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Get_of_unknown_id_returns_null_and_delete_of_unknown_id_is_a_no_op()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder folder = await storage.AddFolderAsync(account.Id, FolderRole.Inbox);

        Assert.Null(await storage.Folders.GetAsync(folder.Id + 1000));
        await storage.Folders.DeleteAsync(folder.Id + 1000);

        Assert.NotNull(await storage.Folders.GetAsync(folder.Id));
    }

    // Case 23
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_removes_its_messages_and_keeps_the_account()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder doomed = await storage.AddFolderAsync(account.Id, FolderRole.Inbox);
        MailFolder survivor = await storage.AddFolderAsync(account.Id, FolderRole.Junk);
        MailMessage doomedMessage = await storage.AddMessageAsync(doomed, 1);
        MailMessage survivorMessage = await storage.AddMessageAsync(survivor, 1);

        await storage.Folders.DeleteAsync(doomed.Id);

        Assert.Null(await storage.Folders.GetAsync(doomed.Id));
        Assert.Null(await storage.Messages.GetAsync(doomedMessage.Id));
        Assert.NotNull(await storage.Accounts.GetAsync(account.Id));
        Assert.NotNull(await storage.Folders.GetAsync(survivor.Id));
        Assert.NotNull(await storage.Messages.GetAsync(survivorMessage.Id));
    }
}
