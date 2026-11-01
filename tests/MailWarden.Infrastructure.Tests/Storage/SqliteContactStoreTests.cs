namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-07c D2 / case 7: <see cref="SqliteContactStore"/> against real in-memory SQLite.</summary>
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:classifier")]
public sealed class SqliteContactStoreTests
{
    // Case 7
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Upsert_twice_with_different_case_keeps_one_row_with_the_updated_name()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);

        Contact first = await store.UpsertAsync(new Contact(0, "Contact_A@Example.Test", "First Name", ContactSource.Manual));
        Contact second = await store.UpsertAsync(new Contact(0, "contact_a@example.test", "Second Name", ContactSource.Imported));

        IReadOnlyList<Contact> listed = await store.ListAsync();
        Contact only = Assert.Single(listed);
        Assert.Equal(1L, await storage.CountRowsAsync("contacts"));
        Assert.Equal("Second Name", only.DisplayName);
        Assert.Equal(ContactSource.Imported, only.Source);
        Assert.Equal("contact_a@example.test", only.EmailAddress, ignoreCase: true);
        Assert.Equal("Second Name", second.DisplayName);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Id, only.Id);
        Assert.True(first.Id > 0);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Empty_store_lists_nothing_and_upsert_returns_the_stored_copy()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);
        Assert.Empty(await store.ListAsync());

        Contact stored = await store.UpsertAsync(new Contact(0, "contact_a@example.test", "Contact A", ContactSource.AutoCollected));

        Assert.True(stored.Id > 0);
        Assert.Equal("contact_a@example.test", stored.EmailAddress);
        Assert.Equal("Contact A", stored.DisplayName);
        Assert.Equal(ContactSource.AutoCollected, stored.Source);
        Contact listed = Assert.Single(await store.ListAsync());
        Assert.Equal(stored, listed);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Different_addresses_are_different_rows()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);

        await store.UpsertAsync(new Contact(0, "contact_a@example.test", "Contact A", ContactSource.Manual));
        await store.UpsertAsync(new Contact(0, "contact_b@example.test", "Contact B", ContactSource.Imported));
        await store.UpsertAsync(new Contact(0, "contact_a@other.test", "Contact A Other", ContactSource.AutoCollected));

        IReadOnlyList<Contact> listed = await store.ListAsync();
        Assert.Equal(3, listed.Count);
        Assert.Equal(
            new[] { "contact_a@example.test", "contact_a@other.test", "contact_b@example.test" },
            listed.Select(contact => contact.EmailAddress.ToLowerInvariant()).OrderBy(address => address, StringComparer.Ordinal).ToArray());
        Assert.Equal(3, listed.Select(contact => contact.Id).Distinct().Count());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Every_contact_source_round_trips()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);
        foreach (ContactSource source in Enum.GetValues<ContactSource>())
        {
            await store.UpsertAsync(new Contact(0, $"contact_{source}@example.test", "Name", source));
        }

        IReadOnlyList<Contact> listed = await store.ListAsync();

        foreach (ContactSource source in Enum.GetValues<ContactSource>())
        {
            Assert.Equal(source, Assert.Single(listed, contact => contact.EmailAddress == $"contact_{source}@example.test").Source);
        }
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Contacts_survive_reopening_a_file_database()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.RootPath, "contacts.db");
        await using (SqliteDatabase first = SqliteDatabase.ForFile(databasePath))
        {
            await first.InitializeAsync();
            await new SqliteContactStore(first).UpsertAsync(new Contact(0, "contact_a@example.test", "Contact A", ContactSource.Manual));
        }

        await using SqliteDatabase second = SqliteDatabase.ForFile(databasePath);
        await second.InitializeAsync();

        Contact loaded = Assert.Single(await new SqliteContactStore(second).ListAsync());
        Assert.Equal("Contact A", loaded.DisplayName);
    }
}
