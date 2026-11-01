using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-06 D7: <see cref="ContactBook"/> is a thin facade over the contact store and the sender list store.</summary>
[Trait("Category", "feature:contacts")]
public sealed class ContactBookTests
{
    private static ContactBook Create(StorageFixture storage) =>
        new(new SqliteContactStore(storage.Database), new SqliteSenderListStore(storage.Database));

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Add_and_remove_contact_reach_the_store()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        ContactBook book = Create(storage);
        var contacts = new SqliteContactStore(storage.Database);

        await book.AddContactAsync("contact_a@example.test", "Contact A");

        Contact added = Assert.Single(await contacts.ListAsync());
        Assert.Equal("Contact A", added.DisplayName);

        await book.RemoveContactAsync("CONTACT_A@example.test");
        Assert.Empty(await contacts.ListAsync());
    }

    // Decision: the name is cleaned like in the harvest
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Added_contact_name_is_cleaned_of_control_and_bidi_characters()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();

        await Create(storage).AddContactAsync("contact_a@example.test", "Re‮cip​ie\u0007nt A");

        Assert.Equal("Recipient A", Assert.Single(await new SqliteContactStore(storage.Database).ListAsync()).DisplayName);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Allow_block_and_unlist_reach_the_list_store()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        ContactBook book = Create(storage);
        var lists = new SqliteSenderListStore(storage.Database);

        await book.AllowAsync("sender_a@example.test", isDomain: false);
        await book.BlockAsync("blocked.test", isDomain: true);

        IReadOnlyList<SenderListEntry> listed = await lists.ListAsync();
        Assert.Equal(ListMembership.Allowed, listed.Single(entry => !entry.IsDomain).Kind);
        Assert.Equal(ListMembership.Blocked, listed.Single(entry => entry.IsDomain).Kind);

        await book.BlockAsync("sender_a@example.test", isDomain: false);
        Assert.Equal(ListMembership.Blocked, (await lists.ListAsync()).Single(entry => !entry.IsDomain).Kind);

        await book.UnlistAsync("sender_a@example.test", isDomain: false);
        await book.UnlistAsync("blocked.test", isDomain: true);
        Assert.Empty(await lists.ListAsync());
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Invalid_list_entry_is_rejected_with_argument_exception()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        ContactBook book = Create(storage);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => book.AllowAsync("no_at_sign", isDomain: false));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => book.BlockAsync("co.uk", isDomain: true));

        Assert.Empty(await new SqliteSenderListStore(storage.Database).ListAsync());
    }
}
