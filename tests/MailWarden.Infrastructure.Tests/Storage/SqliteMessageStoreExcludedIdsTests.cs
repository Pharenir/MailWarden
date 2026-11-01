namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-04b X3: <c>MessageQuery.ExcludedMessageIds</c> in <c>CountUnreadAsync</c> against real SQLite.</summary>
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:ui")]
public sealed class SqliteMessageStoreExcludedIdsTests
{
    private sealed record Seed(StorageFixture Storage, Account AccountA, Account AccountB, MailFolder InboxA, MailFolder InboxB, IReadOnlyList<MailMessage> UnreadA, IReadOnlyList<MailMessage> UnreadB, MailMessage SeenA);

    private static async Task<Seed> SeedAsync()
    {
        StorageFixture storage = await StorageFixture.CreateAsync();
        Account accountA = await storage.AddAccountAsync("Account_A");
        Account accountB = await storage.AddAccountAsync("Account_B");
        MailFolder inboxA = await storage.AddFolderAsync(accountA.Id, FolderRole.Inbox, "INBOX");
        MailFolder inboxB = await storage.AddFolderAsync(accountB.Id, FolderRole.Inbox, "INBOX");
        List<MailMessage> unreadA = [];
        List<MailMessage> unreadB = [];
        for (uint uid = 1; uid <= 3; uid++)
        {
            unreadA.Add(await storage.AddMessageAsync(inboxA, uid));
        }

        for (uint uid = 1; uid <= 2; uid++)
        {
            unreadB.Add(await storage.Messages.UpsertAsync(TestData.NewMessage(accountB.Id, inboxB.Id, uid), null));
        }

        MailMessage seenA = await storage.Messages.UpsertAsync(TestData.NewMessage(accountA.Id, inboxA.Id, 10, flags: MessageFlags.Seen), null);
        return new Seed(storage, accountA, accountB, inboxA, inboxB, unreadA, unreadB, seenA);
    }

    // Folder view: excluded unread mails are not counted.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Folder_view_does_not_count_excluded_unread_mails()
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;

        int count = await storage.Messages.CountUnreadAsync(new MessageQuery(FolderId: seed.InboxA.Id, ExcludedMessageIds: [seed.UnreadA[0].Id, seed.UnreadA[2].Id]));

        Assert.Equal(1, count);
    }

    // Role view (all accounts): exclusions of both accounts apply.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Role_view_does_not_count_excluded_unread_mails_of_any_account()
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;

        Assert.Equal(5, await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox)));
        int count = await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox, ExcludedMessageIds: [seed.UnreadA[0].Id, seed.UnreadB[1].Id]));

        Assert.Equal(3, count);
    }

    // Account + role view.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Account_role_view_respects_the_exclusion()
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;

        int count = await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox, AccountId: seed.AccountB.Id, ExcludedMessageIds: [seed.UnreadB[0].Id]));

        Assert.Equal(1, count);
    }

    // null, empty and unknown ids change nothing; a seen mail in the set changes nothing either.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Null_empty_unknown_and_seen_ids_change_nothing()
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;

        Assert.Equal(3, await storage.Messages.CountUnreadAsync(new MessageQuery(FolderId: seed.InboxA.Id, ExcludedMessageIds: null)));
        Assert.Equal(3, await storage.Messages.CountUnreadAsync(new MessageQuery(FolderId: seed.InboxA.Id, ExcludedMessageIds: [])));
        Assert.Equal(3, await storage.Messages.CountUnreadAsync(new MessageQuery(FolderId: seed.InboxA.Id, ExcludedMessageIds: [987_654_321L, -5L])));
        Assert.Equal(3, await storage.Messages.CountUnreadAsync(new MessageQuery(FolderId: seed.InboxA.Id, ExcludedMessageIds: [seed.SeenA.Id])));
    }

    // Excluding everything gives 0.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Excluding_every_unread_mail_gives_zero()
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;

        int count = await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox, ExcludedMessageIds: [.. seed.UnreadA.Concat(seed.UnreadB).Select(message => message.Id)]));

        Assert.Equal(0, count);
    }

    // A large set (far above SQLite's default variable limit) still works and still excludes the real ids inside it.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_large_set_of_ids_works()
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;
        List<long> ids = [.. Enumerable.Range(100_000, 40_000).Select(value => (long)value)];
        ids.Add(seed.UnreadA[1].Id);

        int count = await storage.Messages.CountUnreadAsync(new MessageQuery(FolderId: seed.InboxA.Id, ExcludedMessageIds: ids));

        Assert.Equal(2, count);
    }

    // Duplicates in the set are harmless.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Duplicate_ids_are_harmless()
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;
        long id = seed.UnreadA[0].Id;

        int count = await storage.Messages.CountUnreadAsync(new MessageQuery(FolderId: seed.InboxA.Id, ExcludedMessageIds: [id, id, id]));

        Assert.Equal(2, count);
    }
}
