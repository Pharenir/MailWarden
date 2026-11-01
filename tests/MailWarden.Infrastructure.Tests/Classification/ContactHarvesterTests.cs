using MailWarden.Core.Classification;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>
/// AP-06 cases 6, 7, 11, 13: <see cref="ContactHarvester"/> reads the recipients of sent mail into the address book.
/// Reading counts: <c>MailsRead</c> = EMLs read, <c>MailsSkipped</c> = missing or unreadable EMLs.
/// </summary>
[Trait("Category", "feature:contacts")]
[Trait("Category", "feature:classifier")]
public sealed class ContactHarvesterTests
{
    private static Contact Find(IReadOnlyList<Contact> contacts, string address) =>
        contacts.Single(contact => string.Equals(contact.EmailAddress, address, StringComparison.OrdinalIgnoreCase));

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task First_run_adds_every_recipient_once_with_name_and_source_auto_collected()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        MailFolder sent = harness.Sent!;
        await harness.AddSentAsync(sent, 1, to: "Recipient A <recipient_a@example.test>", cc: "Recipient B <recipient_b@example.test>");
        await harness.AddSentAsync(sent, 2, to: "Team: Recipient D <recipient_d@example.test>, recipient_e@example.test;", bcc: "Recipient C <recipient_c@example.test>");
        await harness.AddSentAsync(sent, 3, to: "Recipient A <recipient_a@example.test>, \"Recipient F\" <recipient_f@example.test>");

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(MailsRead: 3, ContactsAdded: 6, MailsSkipped: 0), report);
        IReadOnlyList<Contact> contacts = await harness.ContactsAsync();
        Assert.Equal(6, contacts.Count);
        Assert.All(contacts, contact => Assert.Equal(ContactSource.AutoCollected, contact.Source));
        Assert.Equal("Recipient A", Find(contacts, "recipient_a@example.test").DisplayName);
        Assert.Equal("Recipient B", Find(contacts, "recipient_b@example.test").DisplayName);
        Assert.Equal("Recipient C", Find(contacts, "recipient_c@example.test").DisplayName);
        Assert.Equal("Recipient D", Find(contacts, "recipient_d@example.test").DisplayName);
        Assert.Equal("Recipient F", Find(contacts, "recipient_f@example.test").DisplayName);
        Assert.NotNull(Find(contacts, "recipient_e@example.test"));
    }

    // Case 6: To, Cc and Bcc each on their own, and the same address in two headers of one mail counts once
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("recipient_a@example.test", null, null)]
    [InlineData(null, "recipient_a@example.test", null)]
    [InlineData(null, null, "recipient_a@example.test")]
    [InlineData("recipient_a@example.test", "RECIPIENT_A@example.test", "recipient_a@example.test")]
    public async Task Each_recipient_header_is_harvested_and_a_repeated_address_counts_once(string? to, string? cc, string? bcc)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 1, to, cc, bcc);

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(1, report.ContactsAdded);
        Assert.Equal("recipient_a@example.test", Assert.Single(await harness.ContactsAsync()).EmailAddress, ignoreCase: true);
    }

    // Case 6: a mail without any recipient header is read and adds nothing
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mail_without_recipients_is_read_and_adds_nothing()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 1);

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(1, 0, 0), report);
        Assert.Empty(await harness.ContactsAsync());
    }

    // Case 6: the marker is stored per folder with the folder's UIDVALIDITY
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task First_run_stores_the_marker_with_validity_and_highest_uid()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 4, to: "recipient_a@example.test");
        await harness.AddSentAsync(harness.Sent!, 9, to: "recipient_b@example.test");

        await harness.HarvestAsync();

        ContactHarvestMarker marker = (await harness.HarvestMarkers.GetAsync(harness.Sent!.Id))!;
        Assert.Equal(ClassificationHarness.SentUidValidity, marker.UidValidity);
        Assert.Equal(9u, marker.HarvestedUpToUid);
    }

    // Case 7
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_run_reads_nothing_and_a_new_sent_mail_is_the_only_one_read()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        MailFolder sent = harness.Sent!;
        await harness.AddSentAsync(sent, 1, to: "recipient_a@example.test");
        await harness.AddSentAsync(sent, 2, to: "recipient_b@example.test");
        await harness.HarvestAsync();

        ContactHarvestReport second = await harness.HarvestAsync();
        await harness.AddSentAsync(sent, 3, to: "recipient_a@example.test", cc: "recipient_c@example.test");
        ContactHarvestReport third = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(0, 0, 0), second);
        Assert.Equal(new ContactHarvestReport(MailsRead: 1, ContactsAdded: 1, MailsSkipped: 0), third);
        Assert.Equal(3, (await harness.ContactsAsync()).Count);
        Assert.Equal(3u, (await harness.HarvestMarkers.GetAsync(harness.Sent!.Id))!.HarvestedUpToUid);
    }

    // Case 7: a deleted contact is not brought back by a run without new mail
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Removed_contact_stays_removed_while_no_new_sent_mail_arrives()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 1, to: "recipient_a@example.test");
        await harness.HarvestAsync();
        await harness.Contacts.RemoveAsync("recipient_a@example.test");

        await harness.HarvestAsync();

        Assert.Empty(await harness.ContactsAsync());
    }

    // Case 11: missing EML, garbage EML, no EML path -> skipped, marker moves on, the others are handled
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Missing_garbage_and_pathless_mails_are_skipped_and_the_marker_moves_on()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        MailFolder sent = harness.Sent!;
        await harness.AddMessageWithMissingFileAsync(sent, 1, "<missing_a@example.test>");
        await harness.AddRawAsync(sent, 2, [0x00, 0xFF, 0xFE, 0x01, 0x02, 0x80, 0x81, 0x00, 0x00, 0xFF], "<garbage_a@example.test>");
        await harness.AddRawAsync(sent, 3, eml: null, "<pathless_a@example.test>");
        await harness.AddSentAsync(sent, 4, to: "recipient_a@example.test");

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(3, report.MailsSkipped);
        Assert.Equal(1, report.MailsRead);
        Assert.Equal(1, report.ContactsAdded);
        Assert.Equal("recipient_a@example.test", Assert.Single(await harness.ContactsAsync()).EmailAddress, ignoreCase: true);
        Assert.Equal(4u, (await harness.HarvestMarkers.GetAsync(sent.Id))!.HarvestedUpToUid);
    }

    // Case 11: a skipped mail is not retried by the next run
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Skipped_mail_is_not_read_again_by_the_next_run()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddMessageWithMissingFileAsync(harness.Sent!, 1, "<missing_a@example.test>");
        await harness.HarvestAsync();

        ContactHarvestReport second = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(0, 0, 0), second);
    }

    // Case 11: an exception of the store is not swallowed as "skipped"
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Exception_of_the_contact_store_propagates()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 1, to: "recipient_a@example.test");
        ContactHarvester harvester = harness.CreateHarvester(contacts: new ThrowingContactStore(harness.Contacts));

        await Assert.ThrowsAsync<StorageException>(() => harvester.RunAsync(harness.Account.Id));
    }

    // Case 13
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Existing_manual_contact_with_another_name_stays_unchanged()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.Contacts.UpsertAsync(new Contact(0, "Recipient_A@example.test", "Manual Name", ContactSource.Manual));
        await harness.AddSentAsync(harness.Sent!, 1, to: "Other Name <recipient_a@EXAMPLE.test>, Recipient B <recipient_b@example.test>");

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(1, report.ContactsAdded);
        IReadOnlyList<Contact> contacts = await harness.ContactsAsync();
        Assert.Equal(2, contacts.Count);
        Contact manual = Find(contacts, "recipient_a@example.test");
        Assert.Equal("Manual Name", manual.DisplayName);
        Assert.Equal(ContactSource.Manual, manual.Source);
        Assert.Equal(ContactSource.AutoCollected, Find(contacts, "recipient_b@example.test").Source);
    }

    // Case 13: same for an imported contact
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Existing_imported_contact_keeps_its_name_and_source()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.Contacts.UpsertAsync(new Contact(0, "recipient_a@example.test", "Imported Name", ContactSource.Imported));
        await harness.AddSentAsync(harness.Sent!, 1, to: "Other Name <recipient_a@example.test>");

        await harness.HarvestAsync();

        Contact only = Assert.Single(await harness.ContactsAsync());
        Assert.Equal("Imported Name", only.DisplayName);
        Assert.Equal(ContactSource.Imported, only.Source);
    }

    // Decision: an unknown account id is an ArgumentException and stores nothing
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unknown_account_is_an_argument_exception_and_stores_nothing()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 1, to: "recipient_a@example.test");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => harness.CreateHarvester().RunAsync(Guid.NewGuid()));

        Assert.Empty(await harness.ContactsAsync());
        Assert.Null(await harness.HarvestMarkers.GetAsync(harness.Sent!.Id));
    }

    /// <summary>Contact store whose AddIfMissing fails like a broken database.</summary>
    private sealed class ThrowingContactStore(IContactStore inner) : IContactStore
    {
        public Task<IReadOnlyList<Contact>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public Task<Contact> UpsertAsync(Contact contact, CancellationToken ct = default) => inner.UpsertAsync(contact, ct);

        public Task<Contact> AddIfMissingAsync(Contact contact, CancellationToken ct = default) =>
            throw new StorageException("Injected contact store failure.");

        public Task RemoveAsync(string emailAddress, CancellationToken ct = default) => inner.RemoveAsync(emailAddress, ct);
    }
}
