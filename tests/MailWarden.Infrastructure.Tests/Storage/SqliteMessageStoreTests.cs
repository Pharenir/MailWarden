namespace MailWarden.Infrastructure.Tests.Storage;

[Trait("Category", "feature:storage")]
public sealed class SqliteMessageStoreTests
{
    private static readonly DateTimeOffset BaseDate = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private static async Task<(Account Account, MailFolder Inbox)> SeedInboxAsync(StorageFixture storage)
    {
        Account account = await storage.AddAccountAsync();
        MailFolder inbox = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        return (account, inbox);
    }

    // Case 9
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_returns_positive_id_and_get_returns_equal_record()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedInboxAsync(storage);
        MailMessage input = TestData.NewMessage(account.Id, inbox.Id, 7, messageId: null);

        MailMessage stored = await storage.Messages.UpsertAsync(input, "body words");

        Assert.True(stored.Id > 0);
        Assert.Equal(input with { Id = stored.Id }, stored);
        Assert.Equal(stored, await storage.Messages.GetAsync(stored.Id));
        Assert.Equal(stored, await storage.Messages.GetByUidAsync(inbox.Id, 7));
    }

    // Case 9 + positive control: Date offset must survive the round trip (record equality ignores the offset)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Date_keeps_its_original_offset_and_message_id_stays_null()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedInboxAsync(storage);
        var dateWithOffset = new DateTimeOffset(2026, 5, 17, 9, 30, 15, TimeSpan.FromHours(2));
        MailMessage stored = await storage.Messages.UpsertAsync(
            TestData.NewMessage(account.Id, inbox.Id, 1, date: dateWithOffset, messageId: null), null);

        MailMessage? byId = await storage.Messages.GetAsync(stored.Id);
        MailMessage? byUid = await storage.Messages.GetByUidAsync(inbox.Id, 1);

        foreach (MailMessage? loaded in new[] { stored, byId, byUid })
        {
            Assert.NotNull(loaded);
            Assert.Equal(dateWithOffset, loaded.Date);
            Assert.Equal(TimeSpan.FromHours(2), loaded.Date.Offset);
            Assert.Null(loaded.MessageId);
        }
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task All_fields_round_trip_including_message_id_flags_and_eml_path()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedInboxAsync(storage);
        MailMessage input = TestData.NewMessage(
            account.Id,
            inbox.Id,
            4_000_000_000u,
            subject: "sample_subject_invoice",
            fromName: "Sender A",
            flags: MessageFlags.Seen | MessageFlags.Flagged,
            messageId: "<msg_1@example.test>",
            emlPath: "accounts/x/folders/1/4000000000.eml",
            textPreview: "preview_text") with { HasAttachments = true, Size = 9_876_543_210L };

        MailMessage stored = await storage.Messages.UpsertAsync(input, "body");
        MailMessage? loaded = await storage.Messages.GetByUidAsync(inbox.Id, 4_000_000_000u);

        Assert.Equal(input with { Id = stored.Id }, loaded);
        Assert.Equal(TimeSpan.FromHours(2), loaded!.Date.Offset);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Get_and_get_by_uid_return_null_when_missing()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (_, MailFolder inbox) = await SeedInboxAsync(storage);

        Assert.Null(await storage.Messages.GetAsync(12345));
        Assert.Null(await storage.Messages.GetByUidAsync(inbox.Id, 99));
    }

    // Case 10
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_with_same_key_updates_values_and_search_index()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedInboxAsync(storage);
        MailMessage first = await storage.Messages.UpsertAsync(
            TestData.NewMessage(account.Id, inbox.Id, 5, subject: "oldsubjectword"), null);

        MailMessage second = await storage.Messages.UpsertAsync(
            TestData.NewMessage(account.Id, inbox.Id, 5, subject: "newsubjectword", flags: MessageFlags.Seen | MessageFlags.Answered),
            null);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("newsubjectword", second.Subject);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Answered, second.Flags);
        Assert.Equal(second, await storage.Messages.GetAsync(first.Id));
        Assert.Equal(1, await storage.CountRowsAsync("messages"));
        Assert.Empty(await storage.Messages.SearchAsync("oldsubjectword"));
        MailMessage found = Assert.Single(await storage.Messages.SearchAsync("newsubjectword"));
        Assert.Equal(first.Id, found.Id);
    }

    // Contract: bodyText null = keep existing indexed text on update
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_with_null_body_text_keeps_the_indexed_body()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedInboxAsync(storage);
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 1), "quokkaword");

        await storage.Messages.UpsertAsync(
            TestData.NewMessage(account.Id, inbox.Id, 1, flags: MessageFlags.Seen), null);

        Assert.Single(await storage.Messages.SearchAsync("quokkaword"));
    }

    // Contract: non-null bodyText replaces the indexed text
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_with_new_body_text_replaces_the_indexed_body()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedInboxAsync(storage);
        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 1), "quokkaword");

        await storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 1), "wombatword");

        Assert.Empty(await storage.Messages.SearchAsync("quokkaword"));
        Assert.Single(await storage.Messages.SearchAsync("wombatword"));
    }

    // Case 11
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_for_unknown_folder_throws_StorageException()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedInboxAsync(storage);

        await Assert.ThrowsAnyAsync<StorageException>(
            () => storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id + 1000, 1), null));
    }

    // Case 11a (D3): the message account must equal the folder's account
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_with_account_different_from_folder_account_throws_StorageException()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account accountA = await storage.AddAccountAsync("Account_A");
        Account accountB = await storage.AddAccountAsync("Account_B");
        MailFolder inboxA = await storage.AddFolderAsync(accountA.Id, FolderRole.Inbox, "INBOX");

        await Assert.ThrowsAnyAsync<StorageException>(
            () => storage.Messages.UpsertAsync(TestData.NewMessage(accountB.Id, inboxA.Id, 1), "body"));

        Assert.Empty(await storage.Messages.ListAsync(new MessageQuery(AccountId: accountB.Id)));
        Assert.Equal(0, await storage.CountRowsAsync("messages"));
    }

    // Case 11a (D15): update path - an existing message must not be moved to another account
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_of_existing_message_with_other_account_throws_and_keeps_stored_row()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account accountA = await storage.AddAccountAsync("Account_A");
        Account accountB = await storage.AddAccountAsync("Account_B");
        MailFolder inboxA = await storage.AddFolderAsync(accountA.Id, FolderRole.Inbox, "INBOX");
        MailMessage original = await storage.AddMessageAsync(inboxA, 1, subject: "original_subject");

        await Assert.ThrowsAnyAsync<StorageException>(
            () => storage.Messages.UpsertAsync(
                TestData.NewMessage(accountB.Id, inboxA.Id, 1, subject: "hijacked_subject"), "body"));

        MailMessage? afterwards = await storage.Messages.GetByUidAsync(inboxA.Id, 1);
        Assert.NotNull(afterwards);
        Assert.Equal(accountA.Id, afterwards.AccountId);
        Assert.Equal(original, afterwards);
        Assert.Equal(1, await storage.CountRowsAsync("messages"));
    }

    // Case 15a (D7): invalid paging arguments fail fast
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(10, -1)]
    public async Task List_with_invalid_limit_or_offset_throws_ArgumentException(int limit, int offset)
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => storage.Messages.ListAsync(new MessageQuery(Limit: limit, Offset: offset)));
    }

    // Case 15a (D7): IMAP UIDs start at 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_with_uid_zero_throws_ArgumentOutOfRangeException()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (Account account, MailFolder inbox) = await SeedInboxAsync(storage);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => storage.Messages.UpsertAsync(TestData.NewMessage(account.Id, inbox.Id, 0), null));
    }

    // Case 12
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task List_by_role_returns_inbox_messages_of_all_accounts_newest_first_with_id_tie_break()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account accountA = await storage.AddAccountAsync("Account_A");
        Account accountB = await storage.AddAccountAsync("Account_B");
        MailFolder inboxA = await storage.AddFolderAsync(accountA.Id, FolderRole.Inbox, "INBOX");
        MailFolder junkA = await storage.AddFolderAsync(accountA.Id, FolderRole.Junk, "Junk");
        MailFolder inboxB = await storage.AddFolderAsync(accountB.Id, FolderRole.Inbox, "INBOX");
        MailFolder junkB = await storage.AddFolderAsync(accountB.Id, FolderRole.Junk, "Junk");

        MailMessage oldest = await storage.AddMessageAsync(inboxA, 1, date: BaseDate.AddHours(1));
        MailMessage tieLowerId = await storage.AddMessageAsync(inboxA, 2, date: BaseDate.AddHours(3));
        MailMessage middle = await storage.AddMessageAsync(inboxB, 1, date: BaseDate.AddHours(2));
        MailMessage tieHigherId = await storage.AddMessageAsync(inboxB, 2, date: BaseDate.AddHours(3));
        await storage.AddMessageAsync(junkA, 1, date: BaseDate.AddHours(9));
        await storage.AddMessageAsync(junkB, 1, date: BaseDate.AddHours(9));

        IReadOnlyList<MailMessage> listed = await storage.Messages.ListAsync(new MessageQuery(Role: FolderRole.Inbox));

        Assert.Equal(
            new[] { tieHigherId.Id, tieLowerId.Id, middle.Id, oldest.Id },
            listed.Select(m => m.Id).ToArray());
    }

    // Case 13
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task List_by_role_and_account_returns_only_that_accounts_messages()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account accountA = await storage.AddAccountAsync("Account_A");
        Account accountB = await storage.AddAccountAsync("Account_B");
        MailFolder inboxA = await storage.AddFolderAsync(accountA.Id, FolderRole.Inbox, "INBOX");
        MailFolder inboxB = await storage.AddFolderAsync(accountB.Id, FolderRole.Inbox, "INBOX");
        MailMessage messageA1 = await storage.AddMessageAsync(inboxA, 1, date: BaseDate.AddHours(1));
        MailMessage messageA2 = await storage.AddMessageAsync(inboxA, 2, date: BaseDate.AddHours(2));
        await storage.AddMessageAsync(inboxB, 1, date: BaseDate.AddHours(3));

        IReadOnlyList<MailMessage> listed = await storage.Messages.ListAsync(
            new MessageQuery(Role: FolderRole.Inbox, AccountId: accountA.Id));

        Assert.Equal(new[] { messageA2.Id, messageA1.Id }, listed.Select(m => m.Id).ToArray());
        Assert.All(listed, m => Assert.Equal(accountA.Id, m.AccountId));
    }

    // Case 14
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task List_folder_id_wins_over_role()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder inbox = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        MailFolder junk = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk");
        MailMessage inboxMessage = await storage.AddMessageAsync(inbox, 1);
        await storage.AddMessageAsync(junk, 1);

        IReadOnlyList<MailMessage> listed = await storage.Messages.ListAsync(
            new MessageQuery(Role: FolderRole.Junk, FolderId: inbox.Id));

        Assert.Equal(inboxMessage.Id, Assert.Single(listed).Id);
    }

    // Case 15
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task List_applies_limit_and_offset_on_the_newest_first_order()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (_, MailFolder inbox) = await SeedInboxAsync(storage);
        for (uint uid = 1; uid <= 5; uid++)
        {
            await storage.AddMessageAsync(inbox, uid, date: BaseDate.AddHours(uid));
        }

        IReadOnlyList<MailMessage> all = await storage.Messages.ListAsync(new MessageQuery());
        IReadOnlyList<MailMessage> page = await storage.Messages.ListAsync(new MessageQuery(Limit: 2, Offset: 2));

        Assert.Equal(5, all.Count);
        Assert.Equal(new[] { all[2].Id, all[3].Id }, page.Select(m => m.Id).ToArray());
    }

    // Case 16
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task List_with_default_query_returns_all_messages_newest_first()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder inbox = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        MailFolder junk = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk");
        MailMessage second = await storage.AddMessageAsync(inbox, 1, date: BaseDate.AddHours(2));
        MailMessage third = await storage.AddMessageAsync(junk, 1, date: BaseDate.AddHours(3));
        MailMessage first = await storage.AddMessageAsync(inbox, 2, date: BaseDate.AddHours(1));

        IReadOnlyList<MailMessage> listed = await storage.Messages.ListAsync(new MessageQuery());

        Assert.Equal(new[] { third.Id, second.Id, first.Id }, listed.Select(m => m.Id).ToArray());
    }

    // Case 20
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_removes_message_and_its_search_entries()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (_, MailFolder inbox) = await SeedInboxAsync(storage);
        MailMessage doomed = await storage.AddMessageAsync(inbox, 1, subject: "doomedsubject", bodyText: "doomedbody");
        MailMessage survivor = await storage.AddMessageAsync(inbox, 2, subject: "survivorsubject");

        await storage.Messages.DeleteAsync(doomed.Id);

        Assert.Null(await storage.Messages.GetAsync(doomed.Id));
        Assert.Empty(await storage.Messages.SearchAsync("doomedsubject"));
        Assert.Empty(await storage.Messages.SearchAsync("doomedbody"));
        Assert.NotNull(await storage.Messages.GetAsync(survivor.Id));
    }

    // Case 20
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_of_unknown_id_is_a_no_op()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (_, MailFolder inbox) = await SeedInboxAsync(storage);
        MailMessage message = await storage.AddMessageAsync(inbox, 1);

        await storage.Messages.DeleteAsync(message.Id + 1000);

        Assert.NotNull(await storage.Messages.GetAsync(message.Id));
    }

    // Case 21
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_by_uids_removes_exactly_the_given_uids_of_that_folder()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder inbox = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        MailFolder otherFolder = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk");
        for (uint uid = 1; uid <= 4; uid++)
        {
            await storage.AddMessageAsync(inbox, uid, subject: $"subject_{uid}");
        }

        MailMessage sameUidElsewhere = await storage.AddMessageAsync(otherFolder, 2);

        await storage.Messages.DeleteByUidsAsync(inbox.Id, new uint[] { 2, 3 });

        IReadOnlyList<MailMessage> remaining = await storage.Messages.ListAsync(new MessageQuery(FolderId: inbox.Id));
        Assert.Equal(new uint[] { 1, 4 }, remaining.Select(m => m.Uid).OrderBy(uid => uid).ToArray());
        Assert.NotNull(await storage.Messages.GetAsync(sameUidElsewhere.Id));
        Assert.Empty(await storage.Messages.SearchAsync("subject_2"));
    }

    // Case 21
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_by_uids_with_empty_collection_is_a_no_op()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        (_, MailFolder inbox) = await SeedInboxAsync(storage);
        await storage.AddMessageAsync(inbox, 1);
        await storage.AddMessageAsync(inbox, 2);

        await storage.Messages.DeleteByUidsAsync(inbox.Id, Array.Empty<uint>());

        Assert.Equal(2, (await storage.Messages.ListAsync(new MessageQuery(FolderId: inbox.Id))).Count);
    }
}
