namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-04b Y-b: role view of one account (filter on the folder's account) keeps counts and order, with and without exclusions.</summary>
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:ui")]
public sealed class SqliteMessageStoreRoleViewTests
{
    private sealed record Row(MailMessage Message, MailFolder Folder);

    private sealed record Seed(StorageFixture Storage, Account AccountA, Account AccountB, IReadOnlyList<Row> Rows);

    private static readonly DateTimeOffset Start = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Two accounts; each has Inbox, Archive, Junk and a custom folder. Distinct dates, even UIDs are seen.</summary>
    private static async Task<Seed> SeedAsync()
    {
        StorageFixture storage = await StorageFixture.CreateAsync();
        Account accountA = await storage.AddAccountAsync("Account_A");
        Account accountB = await storage.AddAccountAsync("Account_B");
        List<Row> rows = [];
        int minute = 0;
        foreach (Account account in new[] { accountA, accountB })
        {
            foreach ((FolderRole role, string name) in new[] { (FolderRole.Inbox, "INBOX"), (FolderRole.Archive, "Archive"), (FolderRole.Junk, "Junk"), (FolderRole.None, "Custom") })
            {
                MailFolder folder = await storage.AddFolderAsync(account.Id, role, name);
                for (uint uid = 1; uid <= 4; uid++)
                {
                    MessageFlags flags = uid % 2 == 0 ? MessageFlags.Seen : MessageFlags.None;
                    MailMessage message = await storage.Messages.UpsertAsync(
                        TestData.NewMessage(account.Id, folder.Id, uid, $"subject_{account.DisplayName}_{name}_{uid}", date: Start.AddMinutes(minute++), flags: flags),
                        null);
                    rows.Add(new Row(message, folder));
                }
            }
        }

        return new Seed(storage, accountA, accountB, rows);
    }

    private static IEnumerable<Row> RoleViewRows(Seed seed, FolderRole role, Account account) =>
        seed.Rows.Where(row => row.Folder.Role == role && row.Folder.AccountId == account.Id);

    private static bool IsUnread(Row row) => (row.Message.Flags & MessageFlags.Seen) == 0;

    // Y-b: unread count of the role view of one account equals the count over the same rows.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderRole.Inbox)]
    [InlineData(FolderRole.Junk)]
    [InlineData(FolderRole.Archive)]
    public async Task Y_b_unread_count_of_a_role_view_of_one_account_equals_the_count_over_the_same_rows(FolderRole role)
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;

        foreach (Account account in new[] { seed.AccountA, seed.AccountB })
        {
            int expected = RoleViewRows(seed, role, account).Count(IsUnread);
            int actual = await storage.Messages.CountUnreadAsync(new MessageQuery(Role: role, AccountId: account.Id));
            Assert.Equal(2, expected);
            Assert.Equal(expected, actual);
        }
    }

    // Y-b: with excluded ids; ids of other accounts or roles do not change the count.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Y_b_excluded_ids_are_subtracted_only_inside_the_viewed_rows()
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;
        Row[] viewed = [.. RoleViewRows(seed, FolderRole.Inbox, seed.AccountA).Where(IsUnread)];
        Row[] otherAccount = [.. RoleViewRows(seed, FolderRole.Inbox, seed.AccountB)];
        Row[] otherRole = [.. RoleViewRows(seed, FolderRole.Junk, seed.AccountA)];
        Row seenInView = RoleViewRows(seed, FolderRole.Inbox, seed.AccountA).First(row => !IsUnread(row));

        int one = await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox, AccountId: seed.AccountA.Id, ExcludedMessageIds: [viewed[0].Message.Id]));
        int foreign = await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox, AccountId: seed.AccountA.Id, ExcludedMessageIds: [.. otherAccount.Concat(otherRole).Select(row => row.Message.Id)]));
        int seen = await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox, AccountId: seed.AccountA.Id, ExcludedMessageIds: [seenInView.Message.Id]));
        int all = await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Inbox, AccountId: seed.AccountA.Id, ExcludedMessageIds: [.. viewed.Select(row => row.Message.Id)]));

        Assert.Equal(1, one);
        Assert.Equal(2, foreign);
        Assert.Equal(2, seen);
        Assert.Equal(0, all);
    }

    // Y-b: the other views keep their counts (folder, role over all accounts, account).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Y_b_folder_and_all_account_role_views_keep_their_counts()
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;
        Row first = seed.Rows.First(row => row.Folder.Role == FolderRole.Junk && row.Folder.AccountId == seed.AccountB.Id);

        Assert.Equal(2, await storage.Messages.CountUnreadAsync(new MessageQuery(FolderId: first.Folder.Id)));
        Assert.Equal(4, await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Junk)));
        Assert.Equal(3, await storage.Messages.CountUnreadAsync(new MessageQuery(Role: FolderRole.Junk, ExcludedMessageIds: [first.Message.Id])));
        Assert.Equal(8, await storage.Messages.CountUnreadAsync(new MessageQuery(AccountId: seed.AccountA.Id)));
    }

    // Y-b: ListAsync of the role view of one account returns the same rows newest first and pages correctly.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Y_b_list_of_a_role_view_of_one_account_returns_the_rows_newest_first_and_pages()
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;
        long[] expected = [.. RoleViewRows(seed, FolderRole.Inbox, seed.AccountB).OrderByDescending(row => row.Message.Date).Select(row => row.Message.Id)];

        IReadOnlyList<MailMessage> all = await storage.Messages.ListAsync(new MessageQuery(Role: FolderRole.Inbox, AccountId: seed.AccountB.Id, Limit: 100));
        IReadOnlyList<MailMessage> firstPage = await storage.Messages.ListAsync(new MessageQuery(Role: FolderRole.Inbox, AccountId: seed.AccountB.Id, Limit: 3));
        IReadOnlyList<MailMessage> secondPage = await storage.Messages.ListAsync(new MessageQuery(Role: FolderRole.Inbox, AccountId: seed.AccountB.Id, Limit: 3, Offset: 3));

        Assert.Equal(expected, all.Select(message => message.Id).ToArray());
        Assert.Equal(expected.Take(3), firstPage.Select(message => message.Id));
        Assert.Equal(expected.Skip(3), secondPage.Select(message => message.Id));
        Assert.All(all, message => Assert.Equal(seed.AccountB.Id, message.AccountId));
    }

    // Y-b: ListAsync of the all-account role view and of a folder view are unchanged.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Y_b_list_of_the_all_account_role_view_and_of_a_folder_view_is_unchanged()
    {
        Seed seed = await SeedAsync();
        await using StorageFixture storage = seed.Storage;
        long[] allAccounts = [.. seed.Rows.Where(row => row.Folder.Role == FolderRole.Inbox).OrderByDescending(row => row.Message.Date).Select(row => row.Message.Id)];
        Row any = seed.Rows.First(row => row.Folder.FullName == "Custom" && row.Folder.AccountId == seed.AccountA.Id);
        long[] folder = [.. seed.Rows.Where(row => row.Folder.Id == any.Folder.Id).OrderByDescending(row => row.Message.Date).Select(row => row.Message.Id)];

        IReadOnlyList<MailMessage> roleView = await storage.Messages.ListAsync(new MessageQuery(Role: FolderRole.Inbox, Limit: 100));
        IReadOnlyList<MailMessage> folderView = await storage.Messages.ListAsync(new MessageQuery(FolderId: any.Folder.Id, Limit: 100));

        Assert.Equal(allAccounts, roleView.Select(message => message.Id).ToArray());
        Assert.Equal(folder, folderView.Select(message => message.Id).ToArray());
    }
}
