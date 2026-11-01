namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-04a case 20 (D5): <see cref="IMessageStore.CountUnreadAsync"/>.</summary>
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:ui")]
public sealed class SqliteMessageStoreUnreadCountTests
{
    private static async Task<MailMessage> AddAsync(
        StorageFixture storage, MailFolder folder, uint uid, MessageFlags flags)
    {
        MailMessage message = TestData.NewMessage(folder.AccountId, folder.Id, uid, flags: flags);
        return await storage.Messages.UpsertAsync(message, null);
    }

    // Case 20
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_store_counts_zero()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();

        Assert.Equal(0, await storage.Messages.CountUnreadAsync(new MessageQuery()));
    }

    // Case 20
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Seen_messages_are_not_counted_and_other_flags_do_not_make_a_message_seen()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder inbox = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        await AddAsync(storage, inbox, 1, MessageFlags.None);
        await AddAsync(storage, inbox, 2, MessageFlags.Seen);
        await AddAsync(storage, inbox, 3, MessageFlags.Flagged);
        await AddAsync(storage, inbox, 4, MessageFlags.Seen | MessageFlags.Flagged);
        await AddAsync(storage, inbox, 5, MessageFlags.Answered);

        int unread = await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox));

        Assert.Equal(3, unread);
    }

    // Case 20
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Role_counts_over_all_accounts_and_ignores_other_roles()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account accountA = await storage.AddAccountAsync("Account_A");
        Account accountB = await storage.AddAccountAsync("Account_B");
        MailFolder inboxA = await storage.AddFolderAsync(accountA.Id, FolderRole.Inbox, "INBOX");
        MailFolder inboxB = await storage.AddFolderAsync(accountB.Id, FolderRole.Inbox, "INBOX");
        MailFolder junkA = await storage.AddFolderAsync(accountA.Id, FolderRole.Junk, "Junk");
        await AddAsync(storage, inboxA, 1, MessageFlags.None);
        await AddAsync(storage, inboxA, 2, MessageFlags.None);
        await AddAsync(storage, inboxB, 1, MessageFlags.None);
        await AddAsync(storage, inboxB, 2, MessageFlags.Seen);
        await AddAsync(storage, junkA, 1, MessageFlags.None);

        Assert.Equal(3, await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox)));
        Assert.Equal(1, await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Junk)));
        Assert.Equal(0, await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Quarantine)));
    }

    // Case 20
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Account_filter_narrows_the_role_and_without_role_counts_the_whole_account()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account accountA = await storage.AddAccountAsync("Account_A");
        Account accountB = await storage.AddAccountAsync("Account_B");
        MailFolder inboxA = await storage.AddFolderAsync(accountA.Id, FolderRole.Inbox, "INBOX");
        MailFolder junkA = await storage.AddFolderAsync(accountA.Id, FolderRole.Junk, "Junk");
        MailFolder inboxB = await storage.AddFolderAsync(accountB.Id, FolderRole.Inbox, "INBOX");
        await AddAsync(storage, inboxA, 1, MessageFlags.None);
        await AddAsync(storage, junkA, 1, MessageFlags.None);
        await AddAsync(storage, junkA, 2, MessageFlags.None);
        await AddAsync(storage, inboxB, 1, MessageFlags.None);

        Assert.Equal(1, await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox, AccountId: accountA.Id)));
        Assert.Equal(1, await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox, AccountId: accountB.Id)));
        Assert.Equal(2, await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Junk, AccountId: accountA.Id)));
        Assert.Equal(3, await storage.Messages.CountUnreadAsync(new MessageQuery(AccountId: accountA.Id)));
    }

    // Case 20
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folder_filter_counts_only_that_folder_and_wins_over_role()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder inbox = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        MailFolder custom = await storage.AddFolderAsync(account.Id, FolderRole.None, "Projects");
        await AddAsync(storage, inbox, 1, MessageFlags.None);
        await AddAsync(storage, custom, 1, MessageFlags.None);
        await AddAsync(storage, custom, 2, MessageFlags.None);
        await AddAsync(storage, custom, 3, MessageFlags.Seen);

        Assert.Equal(2, await storage.Messages.CountUnreadAsync(new MessageQuery(AccountId: account.Id, FolderId: custom.Id)));
        Assert.Equal(2, await storage.Messages.CountUnreadAsync(
            new MessageQuery(Role: FolderRole.Inbox, AccountId: account.Id, FolderId: custom.Id)));
    }

    // Case 20
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Limit_and_offset_are_ignored()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder inbox = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        for (uint uid = 1; uid <= 7; uid++)
        {
            await AddAsync(storage, inbox, uid, MessageFlags.None);
        }

        int unread = await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox, Limit: 2, Offset: 5));

        Assert.Equal(7, unread);
    }

    // Case 20: the count follows flag updates (a message read elsewhere is no longer unread).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Count_follows_flag_updates()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder inbox = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        await AddAsync(storage, inbox, 1, MessageFlags.None);
        await AddAsync(storage, inbox, 2, MessageFlags.None);

        await storage.Messages.UpdateFlagsAsync(inbox.Id, new Dictionary<uint, MessageFlags> { [1] = MessageFlags.Seen });

        Assert.Equal(1, await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox)));
    }
}
