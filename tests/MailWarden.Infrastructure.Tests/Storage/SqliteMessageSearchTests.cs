namespace MailWarden.Infrastructure.Tests.Storage;

[Trait("Category", "feature:storage")]
public sealed class SqliteMessageSearchTests
{
    private static async Task<(StorageFixture Storage, MailFolder Inbox)> CreateAsync()
    {
        StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder inbox = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        return (storage, inbox);
    }

    // Case 17: word in the subject (also mixed case)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Search_finds_message_by_subject_word_case_insensitively()
    {
        (StorageFixture storage, MailFolder inbox) = await CreateAsync();
        await using (storage)
        {
            MailMessage message = await storage.AddMessageAsync(inbox, 1, subject: "sample_subject_invoice");
            await storage.AddMessageAsync(inbox, 2, subject: "unrelated_topic");

            foreach (string term in new[] { "invoice", "INVOICE", "InVoIcE" })
            {
                MailMessage found = Assert.Single(await storage.Messages.SearchAsync(term));
                Assert.Equal(message.Id, found.Id);
            }
        }
    }

    // Case 17 + positive control: a word that exists only in bodyText must be found (fails if body is not indexed)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Search_finds_message_by_word_that_only_occurs_in_body_text()
    {
        (StorageFixture storage, MailFolder inbox) = await CreateAsync();
        await using (storage)
        {
            MailMessage message = await storage.AddMessageAsync(
                inbox, 1, subject: "plain_subject", bodyText: "the quokkawordonly appears in the body");
            await storage.AddMessageAsync(inbox, 2, subject: "other_subject", bodyText: "nothing special");

            MailMessage found = Assert.Single(await storage.Messages.SearchAsync("quokkawordonly"));

            Assert.Equal(message.Id, found.Id);
            Assert.DoesNotContain("quokkawordonly", found.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("quokkawordonly", found.TextPreview, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Case 17: from address (mixed case) and from name
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Search_finds_message_by_from_address_and_from_name()
    {
        (StorageFixture storage, MailFolder inbox) = await CreateAsync();
        await using (storage)
        {
            MailMessage message = await storage.AddMessageAsync(
                inbox, 1, subject: "s1", fromAddress: "sender_a@example.test", fromName: "Zorbathe Sender");
            await storage.AddMessageAsync(
                inbox, 2, subject: "s2", fromAddress: "sender_b@other.test", fromName: "Someone Else");

            Assert.Equal(message.Id, Assert.Single(await storage.Messages.SearchAsync("sender_a@example.test")).Id);
            Assert.Equal(message.Id, Assert.Single(await storage.Messages.SearchAsync("SENDER_A@EXAMPLE.TEST")).Id);
            Assert.Equal(message.Id, Assert.Single(await storage.Messages.SearchAsync("zorbathe")).Id);
        }
    }

    // Case 17: unknown word
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Search_for_unknown_word_returns_empty()
    {
        (StorageFixture storage, MailFolder inbox) = await CreateAsync();
        await using (storage)
        {
            await storage.AddMessageAsync(inbox, 1, subject: "sample_subject_invoice", bodyText: "some body");

            Assert.Empty(await storage.Messages.SearchAsync("nonexistentwordxyz"));
        }
    }

    // Contract: newest first
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Search_results_are_newest_first()
    {
        (StorageFixture storage, MailFolder inbox) = await CreateAsync();
        await using (storage)
        {
            var baseDate = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
            MailMessage older = await storage.AddMessageAsync(inbox, 1, subject: "commonword a", date: baseDate);
            MailMessage newer = await storage.AddMessageAsync(inbox, 2, subject: "commonword b", date: baseDate.AddDays(1));

            IReadOnlyList<MailMessage> found = await storage.Messages.SearchAsync("commonword");

            Assert.Equal(new[] { newer.Id, older.Id }, found.Select(m => m.Id).ToArray());
        }
    }

    // Contract: limit
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Search_respects_the_limit()
    {
        (StorageFixture storage, MailFolder inbox) = await CreateAsync();
        await using (storage)
        {
            for (uint uid = 1; uid <= 3; uid++)
            {
                await storage.AddMessageAsync(inbox, uid, subject: $"limitword {uid}");
            }

            Assert.Equal(2, (await storage.Messages.SearchAsync("limitword", limit: 2)).Count);
        }
    }

    // Case 18
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  ")]
    [InlineData("\t \n")]
    [InlineData("\0")] // D2: control characters count as separators
    [InlineData("\t\0 ")]
    public async Task Search_with_blank_text_throws_ArgumentException(string blankText)
    {
        (StorageFixture storage, _) = await CreateAsync();
        await using (storage)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.Messages.SearchAsync(blankText));
        }
    }

    // Case 18 / D7: null text is an ArgumentNullException
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Search_with_null_text_throws_ArgumentNullException()
    {
        (StorageFixture storage, _) = await CreateAsync();
        await using (storage)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => storage.Messages.SearchAsync(null!));
        }
    }

    // D2: a NUL character inside the text separates tokens like whitespace and must not throw
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Search_treats_control_character_between_words_as_separator()
    {
        (StorageFixture storage, MailFolder inbox) = await CreateAsync();
        await using (storage)
        {
            MailMessage message = await storage.AddMessageAsync(inbox, 1, subject: "alpha xray");
            await storage.AddMessageAsync(inbox, 2, subject: "unrelated_topic");

            IReadOnlyList<MailMessage> found = await storage.Messages.SearchAsync("alpha\0x");

            Assert.Equal(message.Id, Assert.Single(found).Id);
        }
    }

    // Case 19
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("\"foo\" OR bar*")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("\"")]
    [InlineData("*")]
    [InlineData("foo AND")]
    [InlineData("NEAR(a b)")]
    [InlineData("col:value")]
    [InlineData("-negated ^start")]
    public async Task Search_with_fts_syntax_characters_does_not_throw(string hostileInput)
    {
        (StorageFixture storage, MailFolder inbox) = await CreateAsync();
        await using (storage)
        {
            await storage.AddMessageAsync(inbox, 1, subject: "foo bar baz", bodyText: "body");

            IReadOnlyList<MailMessage> result = await storage.Messages.SearchAsync(hostileInput);

            Assert.NotNull(result);
        }
    }

    // Case 19: operators are plain words, so OR does not widen the search
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Search_treats_or_operator_as_plain_word()
    {
        (StorageFixture storage, MailFolder inbox) = await CreateAsync();
        await using (storage)
        {
            await storage.AddMessageAsync(inbox, 1, subject: "onlyleftword");
            await storage.AddMessageAsync(inbox, 2, subject: "onlyrightword");

            // If "OR" were interpreted as an FTS operator, this would match both messages.
            Assert.Empty(await storage.Messages.SearchAsync("onlyleftword OR onlyrightword"));
        }
    }
}
