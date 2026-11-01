namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-06 case 3 / D2: <c>AddIfMissingAsync</c> and <c>RemoveAsync</c> of <see cref="SqliteContactStore"/>.</summary>
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:contacts")]
public sealed class SqliteContactStoreAddIfMissingTests
{
    // Case 3: new address is added
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task New_address_is_added_and_returned_as_stored()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);

        Contact added = await store.AddIfMissingAsync(new Contact(0, "recipient_a@example.test", "Recipient A", ContactSource.AutoCollected));

        Assert.True(added.Id > 0);
        Assert.Equal("recipient_a@example.test", added.EmailAddress);
        Assert.Equal("Recipient A", added.DisplayName);
        Assert.Equal(ContactSource.AutoCollected, added.Source);
        Assert.Equal(added, Assert.Single(await store.ListAsync()));
    }

    // Case 3: existing manual contact, other name, other case -> unchanged
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Existing_manual_contact_stays_unchanged_for_other_name_source_and_case()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);
        Contact manual = await store.UpsertAsync(new Contact(0, "Recipient_A@Example.Test", "Manual Name", ContactSource.Manual));

        Contact result = await store.AddIfMissingAsync(new Contact(0, "recipient_a@example.test", "Collected Name", ContactSource.AutoCollected));

        Assert.Equal(manual.Id, result.Id);
        Assert.Equal("Manual Name", result.DisplayName);
        Assert.Equal(ContactSource.Manual, result.Source);
        Contact only = Assert.Single(await store.ListAsync());
        Assert.Equal("Manual Name", only.DisplayName);
        Assert.Equal(ContactSource.Manual, only.Source);
        Assert.Equal(1L, await storage.CountRowsAsync("contacts"));
    }

    // Case 3: "whatever its source"
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(ContactSource.Imported)]
    [InlineData(ContactSource.AutoCollected)]
    [InlineData(ContactSource.Manual)]
    public async Task Existing_contact_of_any_source_is_not_overwritten(ContactSource existingSource)
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);
        await store.UpsertAsync(new Contact(0, "recipient_a@example.test", "First Name", existingSource));

        await store.AddIfMissingAsync(new Contact(0, "recipient_a@example.test", "Second Name", ContactSource.Imported));

        Contact only = Assert.Single(await store.ListAsync());
        Assert.Equal("First Name", only.DisplayName);
        Assert.Equal(existingSource, only.Source);
    }

    // Case 3: a second add of the same address adds nothing, a different address does
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Repeated_add_keeps_one_row_per_address()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);

        await store.AddIfMissingAsync(new Contact(0, "recipient_a@example.test", "A", ContactSource.AutoCollected));
        await store.AddIfMissingAsync(new Contact(0, "recipient_a@example.test", "A", ContactSource.AutoCollected));
        await store.AddIfMissingAsync(new Contact(0, "recipient_b@example.test", "B", ContactSource.AutoCollected));

        Assert.Equal(2L, await storage.CountRowsAsync("contacts"));
    }

    // Case 3: RemoveAsync
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Remove_deletes_the_contact_in_any_letter_case_and_keeps_the_others()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);
        await store.UpsertAsync(new Contact(0, "Recipient_A@Example.Test", "A", ContactSource.Manual));
        await store.UpsertAsync(new Contact(0, "recipient_b@example.test", "B", ContactSource.Manual));

        await store.RemoveAsync("recipient_a@EXAMPLE.test");

        Contact remaining = Assert.Single(await store.ListAsync());
        Assert.Equal("recipient_b@example.test", remaining.EmailAddress);
    }

    // Case 3: a removed contact can be collected again (positive control for the removal)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Removed_contact_can_be_added_again_with_the_new_name()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);
        await store.UpsertAsync(new Contact(0, "recipient_a@example.test", "Old Name", ContactSource.Manual));
        await store.RemoveAsync("recipient_a@example.test");

        Contact added = await store.AddIfMissingAsync(new Contact(0, "recipient_a@example.test", "New Name", ContactSource.AutoCollected));

        Assert.Equal("New Name", added.DisplayName);
        Assert.Equal(ContactSource.AutoCollected, added.Source);
    }

    // Case 3: unknown address on remove is a no-op (assumption: same as the sender list)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Removing_an_unknown_address_changes_nothing()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);
        await store.UpsertAsync(new Contact(0, "recipient_a@example.test", "A", ContactSource.Manual));

        await store.RemoveAsync("unknown_a@example.test");

        Assert.Single(await store.ListAsync());
    }
}
