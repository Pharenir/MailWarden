using System.Text;
using MailWarden.Core.Classification;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>
/// AP-06 cases 8, 9, 10, 12, 18: what the harvester must not collect, which folders it reads, restart behaviour and
/// name cleaning. Every blocking rule has a positive control: a valid recipient in the same mail gets collected.
/// </summary>
[Trait("Category", "feature:contacts")]
[Trait("Category", "feature:classifier")]
public sealed class ContactHarvesterRulesTests
{
    private const string ControlAddress = "recipient_ok@example.test";

    private static async Task<IReadOnlyList<string>> AddressesAsync(ClassificationHarness harness) =>
        [.. (await harness.ContactsAsync()).Select(contact => contact.EmailAddress.ToLowerInvariant())];

    // Case 8: own addresses of all accounts
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Own_addresses_of_all_accounts_are_not_collected()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        Account second = await harness.Storage.AddAccountAsync("Account_B", "second_own_a@example.test");
        await harness.AddSentAsync(
            harness.Sent!,
            1,
            to: $"{harness.Account.EmailAddress.ToUpperInvariant()}, {ControlAddress}",
            cc: "Second_Own_A@Example.Test");

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(1, report.ContactsAdded);
        Assert.Equal([ControlAddress], await AddressesAsync(harness));
        Assert.NotNull(second);
    }

    // Case 8: role addresses, any letter case, with a positive control in the same mail
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("noreply")]
    [InlineData("no-reply")]
    [InlineData("donotreply")]
    [InlineData("do-not-reply")]
    [InlineData("mailer-daemon")]
    [InlineData("postmaster")]
    [InlineData("NoReply")]
    [InlineData("NO-REPLY")]
    [InlineData("MAILER-DAEMON")]
    [InlineData("PostMaster")]
    public async Task Role_addresses_are_not_collected_but_the_other_recipient_is(string localPart)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 1, to: $"{localPart}@example.test", cc: ControlAddress);

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(1, report.ContactsAdded);
        Assert.Equal([ControlAddress], await AddressesAsync(harness));
    }

    // Case 8: malformed and domain-less addresses, each in its own header so a parse problem cannot hide the control
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("not_an_address")]
    [InlineData("user_a@")]
    [InlineData("user_a@...")]
    [InlineData("@example.test")]
    [InlineData("<>")]
    public async Task Malformed_or_domainless_addresses_are_not_collected(string recipient)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 1, to: ControlAddress, cc: recipient);

        await harness.HarvestAsync();

        Assert.Equal([ControlAddress], await AddressesAsync(harness));
    }

    // Case 8: only the local part decides, "noreply" in the domain or as part of a longer name is a normal address
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Role_word_in_the_domain_does_not_exclude_the_address()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 1, to: "person_a@noreply.example.test");

        await harness.HarvestAsync();

        Assert.Equal(["person_a@noreply.example.test"], await AddressesAsync(harness));
    }

    // Case 9: positive control and the roles that must never be read
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Incoming_mail_in_other_folders_is_never_harvested_but_sent_mail_is()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        var others = new List<MailFolder> { harness.Inbox, harness.Junk!, harness.Quarantine!, harness.Screener! };
        foreach (FolderRole role in new[] { FolderRole.Archive, FolderRole.Trash })
        {
            others.Add(await harness.Storage.Folders.UpsertAsync(TestData.NewFolder(harness.Account.Id, $"folder_{role}", role, 7000)));
        }

        foreach (MailFolder folder in others)
        {
            await harness.MarkSyncedAsync(folder);
            await harness.AddSentAsync(folder, 1, to: $"incoming_{folder.Role}@example.test".ToLowerInvariant());
            await harness.AddMailAsync(folder, 2, MailKind.CertainSpam);
        }

        await harness.AddSentAsync(harness.Sent!, 1, to: ControlAddress);

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(1, 1, 0), report);
        Assert.Equal([ControlAddress], await AddressesAsync(harness));
        foreach (MailFolder folder in others)
        {
            Assert.Null(await harness.HarvestMarkers.GetAsync(folder.Id));
        }
    }

    // Case 9: the sender of incoming mail is not a contact either (spoofing note)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Sender_of_an_incoming_mail_does_not_become_a_contact()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.Trusted);

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(0, 0, 0), report);
        Assert.Empty(await harness.ContactsAsync());
    }

    // Case 10: folder without a completed sync -> skipped; positive control: harvested once the sync state exists
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Sent_folder_without_sync_state_is_skipped_until_it_has_one()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        MailFolder unsynced = await harness.Storage.Folders.UpsertAsync(TestData.NewFolder(harness.Account.Id, "Sent2", FolderRole.Sent, 8000));
        await harness.AddSentAsync(unsynced, 1, to: "unsynced_a@example.test");
        await harness.AddSentAsync(harness.Sent!, 1, to: ControlAddress);

        ContactHarvestReport first = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(1, 1, 0), first);
        Assert.Equal([ControlAddress], await AddressesAsync(harness));
        Assert.Null(await harness.HarvestMarkers.GetAsync(unsynced.Id));

        await harness.MarkSyncedAsync(unsynced);
        ContactHarvestReport second = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(1, 1, 0), second);
        Assert.Contains("unsynced_a@example.test", await AddressesAsync(harness));
    }

    // Case 10: another UIDVALIDITY in the marker -> from the start, the whole stock counts
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Marker_with_another_uid_validity_restarts_from_the_first_uid()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        MailFolder sent = harness.Sent!;
        await harness.AddSentAsync(sent, 1, to: "recipient_a@example.test");
        await harness.AddSentAsync(sent, 2, to: "recipient_b@example.test");
        await harness.AddSentAsync(sent, 3, to: "recipient_c@example.test");
        await harness.HarvestMarkers.SaveAsync(new ContactHarvestMarker(sent.Id, ClassificationHarness.SentUidValidity + 1, 3));

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(3, report.MailsRead);
        Assert.Equal(3, report.ContactsAdded);
        ContactHarvestMarker marker = (await harness.HarvestMarkers.GetAsync(sent.Id))!;
        Assert.Equal(ClassificationHarness.SentUidValidity, marker.UidValidity);
        Assert.Equal(3u, marker.HarvestedUpToUid);
    }

    // Case 10: positive control, the same marker with the matching validity skips the stock
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Marker_with_the_same_uid_validity_reads_only_newer_uids()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        MailFolder sent = harness.Sent!;
        await harness.AddSentAsync(sent, 1, to: "recipient_a@example.test");
        await harness.AddSentAsync(sent, 2, to: "recipient_b@example.test");
        await harness.AddSentAsync(sent, 3, to: "recipient_c@example.test");
        await harness.HarvestMarkers.SaveAsync(new ContactHarvestMarker(sent.Id, ClassificationHarness.SentUidValidity, 2));

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(1, 1, 0), report);
        Assert.Equal(["recipient_c@example.test"], await AddressesAsync(harness));
    }

    // Case 10: without a marker the whole stock counts (unlike the classifier)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Without_a_marker_the_whole_existing_stock_is_read()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        for (uint uid = 1; uid <= 5; uid++)
        {
            await harness.AddSentAsync(harness.Sent!, uid, to: $"recipient_{uid}@example.test");
        }

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(5, 5, 0), report);
    }

    // Case 12: 500 mails, cancellation after the first block of 200
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 4)]
    public async Task Cancellation_after_the_first_block_keeps_that_block_and_the_next_run_continues_without_duplicates()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        const int mailCount = 500;
        for (uint uid = 1; uid <= mailCount; uid++)
        {
            await harness.AddSentAsync(harness.Sent!, uid, to: $"recipient_{uid}@example.test");
        }

        using var source = new CancellationTokenSource();
        var cancelling = new CancellingContactStore(harness.Contacts, source, cancelOnCall: 201);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.CreateHarvester(contacts: cancelling).RunAsync(harness.Account.Id, source.Token));

        ContactHarvestMarker? marker = await harness.HarvestMarkers.GetAsync(harness.Sent!.Id);
        Assert.NotNull(marker);
        Assert.InRange(marker.HarvestedUpToUid, 200u, 201u);
        long contactsAfterCancel = (await harness.ContactsAsync()).Count;
        Assert.InRange(contactsAfterCancel, 200, 201);

        ContactHarvestReport resumed = await harness.HarvestAsync();

        Assert.Equal(mailCount - (int)marker.HarvestedUpToUid, resumed.MailsRead);
        IReadOnlyList<Contact> contacts = await harness.ContactsAsync();
        Assert.Equal(mailCount, contacts.Count);
        Assert.Equal(mailCount, contacts.Select(contact => contact.EmailAddress.ToLowerInvariant()).Distinct().Count());
        Assert.Equal((uint)mailCount, (await harness.HarvestMarkers.GetAsync(harness.Sent!.Id))!.HarvestedUpToUid);
    }

    // Case 12: a cancelled token before the start propagates and stores nothing
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Token_cancelled_before_the_run_propagates_and_adds_nothing()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 1, to: "recipient_a@example.test");
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.HarvestAsync(source.Token));

        Assert.Empty(await harness.ContactsAsync());
    }

    // Case 18: encoded display name with a bidi override, a zero-width space and a control character
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Control_and_bidi_characters_are_removed_from_the_stored_name()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        // "Re<U+202E>cip<U+200B>ie<U+0001>nt A" as RFC 2047 encoded word.
        await harness.AddSentAsync(harness.Sent!, 1, to: "=?utf-8?q?Re=E2=80=AEcip=E2=80=8Bie=01nt_A?= <recipient_a@example.test>");

        await harness.HarvestAsync();

        Assert.Equal("Recipient A", Assert.Single(await harness.ContactsAsync()).DisplayName);
    }

    // Case 18: at most 200 characters
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Very_long_name_is_cut_to_at_most_200_characters()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        string longName = new('x', 400);
        await harness.AddSentAsync(harness.Sent!, 1, to: $"\"{longName}\" <recipient_a@example.test>");

        await harness.HarvestAsync();

        string stored = Assert.Single(await harness.ContactsAsync()).DisplayName;
        Assert.InRange(stored.Length, 1, 200);
        Assert.StartsWith("xxxx", stored, StringComparison.Ordinal);
    }

    /// <summary>Delegates to the real store; at the given call it cancels the token and throws like a cancelled operation.</summary>
    private sealed class CancellingContactStore(IContactStore inner, CancellationTokenSource source, int cancelOnCall) : IContactStore
    {
        private int calls;

        public Task<IReadOnlyList<Contact>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public Task<Contact> UpsertAsync(Contact contact, CancellationToken ct = default) => inner.UpsertAsync(contact, ct);

        public Task<Contact> AddIfMissingAsync(Contact contact, CancellationToken ct = default)
        {
            calls++;
            if (calls == cancelOnCall)
            {
                source.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return inner.AddIfMissingAsync(contact, ct);
        }

        public Task RemoveAsync(string emailAddress, CancellationToken ct = default) => inner.RemoveAsync(emailAddress, ct);
    }
}
