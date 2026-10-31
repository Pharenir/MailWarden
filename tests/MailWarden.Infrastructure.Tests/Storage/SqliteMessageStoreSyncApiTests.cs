namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-03a case 24: <c>GetUidFlagsAsync</c>, <c>GetUidEmlPathsAsync</c>, <c>UpdateFlagsAsync</c>.</summary>
[Trait("Category", "feature:storage")]
public sealed class SqliteMessageStoreSyncApiTests
{
    private static async Task<(Account Account, MailFolder Inbox)> SeedAsync(StorageFixture storage)
    {
        Account account = await storage.AddAccountAsync();
        MailFolder inbox = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        return (account, inbox);
    }

    // Case 24
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Get_uid_flags_returns_every_uid_of_the_folder_only()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedAsync(storage);
        MailFolder junk = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk");
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 1, flags: MessageFlags.Seen), null);
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 5, flags: MessageFlags.Answered | MessageFlags.Draft), null);
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 9), null);
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, junk.Id, 2, flags: MessageFlags.Flagged), null);

        IReadOnlyDictionary<uint, MessageFlags> flags = await storage.Messages.GetUidFlagsAsync(inbox.Id);

        Assert.Equal(3, flags.Count);
        Assert.Equal(MessageFlags.Seen, flags[1]);
        Assert.Equal(MessageFlags.Answered | MessageFlags.Draft, flags[5]);
        Assert.Equal(MessageFlags.None, flags[9]);
        Assert.False(flags.ContainsKey(2));
    }

    // Case 24
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Get_uid_flags_is_empty_for_unknown_and_for_empty_folder()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (_, MailFolder inbox) = await SeedAsync(storage);

        Assert.Empty(await storage.Messages.GetUidFlagsAsync(inbox.Id));
        Assert.Empty(await storage.Messages.GetUidFlagsAsync(inbox.Id + 1000));
    }

    // Case 24
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Get_uid_eml_paths_returns_paths_and_null_for_rows_without_one()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedAsync(storage);
        MailFolder junk = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk");
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 1, emlPath: "accounts/x/folders/1/1.eml"), null);
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 2, emlPath: null), null);
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, junk.Id, 3, emlPath: "accounts/x/folders/2/3.eml"), null);

        IReadOnlyDictionary<uint, string?> paths = await storage.Messages.GetUidEmlPathsAsync(inbox.Id);

        Assert.Equal(2, paths.Count);
        Assert.Equal("accounts/x/folders/1/1.eml", paths[1]);
        Assert.True(paths.ContainsKey(2));
        Assert.Null(paths[2]);
    }

    // Case 24
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Get_uid_eml_paths_is_empty_for_unknown_folder()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();

        Assert.Empty(await storage.Messages.GetUidEmlPathsAsync(4242));
    }

    // Case 24
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Update_flags_changes_only_the_given_uids_and_only_their_flags()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedAsync(storage);
        MailFolder junk = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk");
        MailMessage one = await storage.AddMessageAsync(inbox, 1, subject: "subject_one", bodyText: "quokkaword");
        MailMessage two = await storage.AddMessageAsync(inbox, 2, subject: "subject_two");
        MailMessage sameUidElsewhere = await storage.AddMessageAsync(junk, 1, subject: "subject_junk");

        await storage.Messages.UpdateFlagsAsync(
            inbox.Id,
            new Dictionary<uint, MessageFlags> { [1] = MessageFlags.Seen | MessageFlags.Flagged });

        Assert.Equal(one with { Flags = MessageFlags.Seen | MessageFlags.Flagged }, await storage.Messages.GetAsync(one.Id));
        Assert.Equal(two, await storage.Messages.GetAsync(two.Id));
        Assert.Equal(sameUidElsewhere, await storage.Messages.GetAsync(sameUidElsewhere.Id));
    }

    // Case 24: several uids in one call, including clearing all flags
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Update_flags_handles_several_uids_in_one_call_and_can_clear_flags()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedAsync(storage);
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 1, flags: MessageFlags.Seen), null);
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 2), null);
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 3, flags: MessageFlags.Deleted), null);

        await storage.Messages.UpdateFlagsAsync(
            inbox.Id,
            new Dictionary<uint, MessageFlags>
            {
                [1] = MessageFlags.None,
                [2] = MessageFlags.Answered,
                [3] = MessageFlags.Seen | MessageFlags.Draft,
            });

        IReadOnlyDictionary<uint, MessageFlags> flags = await storage.Messages.GetUidFlagsAsync(inbox.Id);
        Assert.Equal(MessageFlags.None, flags[1]);
        Assert.Equal(MessageFlags.Answered, flags[2]);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Draft, flags[3]);
    }

    // Case 24: unknown UIDs are ignored
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Update_flags_ignores_unknown_uids_without_creating_rows()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedAsync(storage);
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 1), null);

        await storage.Messages.UpdateFlagsAsync(
            inbox.Id,
            new Dictionary<uint, MessageFlags> { [1] = MessageFlags.Seen, [99] = MessageFlags.Seen, [100] = MessageFlags.Flagged });

        Assert.Equal(1, await storage.CountRowsAsync("messages"));
        IReadOnlyDictionary<uint, MessageFlags> flags = await storage.Messages.GetUidFlagsAsync(inbox.Id);
        Assert.Equal(MessageFlags.Seen, Assert.Single(flags).Value);
    }

    // Case 24: empty dictionary
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Update_flags_with_empty_dictionary_is_a_no_op()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedAsync(storage);
        MailMessage message = await storage.Messages.UpsertAsync(
            TestData.NewMessage(account.Id, inbox.Id, 1, flags: MessageFlags.Seen), null);

        await storage.Messages.UpdateFlagsAsync(inbox.Id, new Dictionary<uint, MessageFlags>());

        Assert.Equal(message, await storage.Messages.GetAsync(message.Id));
    }

    // Case 24: the full-text index is not touched by a flag update
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Update_flags_keeps_the_full_text_index_intact()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (_, MailFolder inbox) = await SeedAsync(storage);
        MailMessage message = await storage.AddMessageAsync(inbox, 1, subject: "indexedsubject", bodyText: "indexedbodyword");

        await storage.Messages.UpdateFlagsAsync(inbox.Id, new Dictionary<uint, MessageFlags> { [1] = MessageFlags.Seen });

        Assert.Equal(message.Id, Assert.Single(await storage.Messages.SearchAsync("indexedbodyword")).Id);
        Assert.Equal(message.Id, Assert.Single(await storage.Messages.SearchAsync("indexedsubject")).Id);
    }
}
