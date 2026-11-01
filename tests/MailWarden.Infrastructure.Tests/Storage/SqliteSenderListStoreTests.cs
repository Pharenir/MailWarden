using MailWarden.Core.Classification;

namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-06 case 2 / D2: <see cref="SqliteSenderListStore"/> against real in-memory SQLite.</summary>
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:contacts")]
public sealed class SqliteSenderListStoreTests
{
    private static SenderListEntry Address(string entry, ListMembership kind) => new(entry, false, kind);

    private static SenderListEntry Domain(string entry, ListMembership kind) => new(entry, true, kind);

    // Case 2
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Empty_store_lists_nothing_and_set_entries_are_listed_with_their_kind()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);
        Assert.Empty(await store.ListAsync());

        await store.SetAsync(Address("sender_a@example.test", ListMembership.Allowed));
        await store.SetAsync(Domain("blocked.test", ListMembership.Blocked));

        IReadOnlyList<SenderListEntry> listed = await store.ListAsync();
        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, entry => !entry.IsDomain && entry.Kind == ListMembership.Allowed
            && string.Equals(entry.Entry, "sender_a@example.test", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(listed, entry => entry.IsDomain && entry.Kind == ListMembership.Blocked
            && string.Equals(entry.Entry, "blocked.test", StringComparison.OrdinalIgnoreCase));
    }

    // Case 2: "an entry has one kind; setting it again replaces it"
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Setting_an_entry_again_replaces_its_kind_and_keeps_one_row()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);
        await store.SetAsync(Address("sender_a@example.test", ListMembership.Allowed));

        await store.SetAsync(Address("sender_a@example.test", ListMembership.Blocked));
        await store.SetAsync(Domain("example.test", ListMembership.Allowed));
        await store.SetAsync(Domain("example.test", ListMembership.Blocked));

        Assert.Equal(2L, await storage.CountRowsAsync("sender_list"));
        IReadOnlyList<SenderListEntry> listed = await store.ListAsync();
        Assert.All(listed, entry => Assert.Equal(ListMembership.Blocked, entry.Kind));
    }

    // Case 2: the same text as address and as domain flag are separate entries (key is entry plus flag)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Address_entry_and_domain_entry_of_a_domain_do_not_replace_each_other()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);

        await store.SetAsync(Address("sender_a@example.test", ListMembership.Allowed));
        await store.SetAsync(Domain("example.test", ListMembership.Blocked));

        IReadOnlyList<SenderListEntry> listed = await store.ListAsync();
        Assert.Equal(2, listed.Count);
        Assert.Equal(ListMembership.Allowed, listed.Single(entry => !entry.IsDomain).Kind);
        Assert.Equal(ListMembership.Blocked, listed.Single(entry => entry.IsDomain).Kind);
    }

    // Case 2: case-insensitive
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Entries_ignore_letter_case_when_set_and_removed()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);
        await store.SetAsync(Address("Sender_A@Example.Test", ListMembership.Allowed));
        await store.SetAsync(Address("SENDER_A@EXAMPLE.TEST", ListMembership.Blocked));
        await store.SetAsync(Domain("Blocked.Test", ListMembership.Blocked));
        Assert.Equal(ListMembership.Blocked, Assert.Single(await store.ListAsync(), entry => !entry.IsDomain).Kind);

        await store.RemoveAsync("sender_a@example.test", isDomain: false);
        await store.RemoveAsync("BLOCKED.TEST", isDomain: true);

        Assert.Empty(await store.ListAsync());
    }

    // Case 2
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Remove_deletes_only_the_named_entry()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);
        await store.SetAsync(Address("sender_a@example.test", ListMembership.Allowed));
        await store.SetAsync(Address("sender_b@example.test", ListMembership.Blocked));
        await store.SetAsync(Domain("example.test", ListMembership.Blocked));

        await store.RemoveAsync("sender_a@example.test", isDomain: false);

        IReadOnlyList<SenderListEntry> listed = await store.ListAsync();
        Assert.Equal(2, listed.Count);
        Assert.DoesNotContain(listed, entry => string.Equals(entry.Entry, "sender_a@example.test", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(listed, entry => entry.IsDomain);
    }

    // Case 2: unknown entry = no-op; a wrong flag does not hit the other entry
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Removing_an_unknown_entry_or_with_the_wrong_flag_is_a_no_op()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);
        await store.SetAsync(Domain("example.test", ListMembership.Blocked));

        await store.RemoveAsync("unknown_a@example.test", isDomain: false);
        await store.RemoveAsync("example.test", isDomain: false);
        await store.RemoveAsync("other.test", isDomain: true);

        Assert.Single(await store.ListAsync());
    }

    // Case 2: trimmed
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Entries_are_trimmed_when_stored_and_when_removed()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);

        await store.SetAsync(Address("  sender_a@example.test \t", ListMembership.Allowed));
        await store.SetAsync(Domain(" blocked.test ", ListMembership.Blocked));

        IReadOnlyList<SenderListEntry> listed = await store.ListAsync();
        Assert.Contains(listed, entry => entry.Entry == "sender_a@example.test");
        Assert.Contains(listed, entry => entry.Entry == "blocked.test");
        // The same trimmed address is the same entry.
        await store.SetAsync(Address("sender_a@example.test", ListMembership.Blocked));
        Assert.Equal(2L, await storage.CountRowsAsync("sender_list"));
    }

    // Case 2: invalid entries -> ArgumentException, nothing stored
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("no_at_sign", false)]
    [InlineData("sender_a@", false)]
    [InlineData("sender_a@  ", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("localhost", true)]
    [InlineData("sender_a@example.test", true)]
    [InlineData("@example.test", true)]
    public async Task Invalid_entries_are_rejected_with_ArgumentException_and_store_nothing(string entry, bool isDomain)
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SetAsync(new SenderListEntry(entry, isDomain, ListMembership.Blocked)));

        Assert.Equal(0L, await storage.CountRowsAsync("sender_list"));
    }

    // Case 2: kind None is invalid for both flags
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("sender_a@example.test", false)]
    [InlineData("example.test", true)]
    public async Task Kind_none_is_rejected_with_ArgumentException(string entry, bool isDomain)
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SetAsync(new SenderListEntry(entry, isDomain, ListMembership.None)));

        Assert.Equal(0L, await storage.CountRowsAsync("sender_list"));
    }

    // Case 2 (decision): a domain entry that is a public suffix is rejected
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("co.uk")]
    [InlineData("com")]
    [InlineData("CO.UK")]
    public async Task Public_suffix_as_domain_entry_is_rejected(string suffix)
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SetAsync(Domain(suffix, ListMembership.Blocked)));

        Assert.Equal(0L, await storage.CountRowsAsync("sender_list"));
    }

    // Case 2: positive controls for the boundaries of the validation
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("example.co.uk", true)]
    [InlineData("a@b.test", false)]
    [InlineData("sender_a@localhost", false)]
    [InlineData("example.test", true)]
    [InlineData("mail.example.co.uk", true)]
    public async Task Valid_boundary_entries_are_accepted(string entry, bool isDomain)
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);

        await store.SetAsync(new SenderListEntry(entry, isDomain, ListMembership.Allowed));

        Assert.Single(await store.ListAsync());
    }

    // Case 2: cancellation
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cancelled_token_stops_a_set()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SetAsync(Address("sender_a@example.test", ListMembership.Allowed), source.Token));

        Assert.Equal(0L, await storage.CountRowsAsync("sender_list"));
    }
}
