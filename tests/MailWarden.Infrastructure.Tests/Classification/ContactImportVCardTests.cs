using System.Text;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-06 D6 / cases 16, 18: <see cref="ContactImportFile.ImportVCardAsync"/>. All addresses are artificial.</summary>
[Trait("Category", "feature:contacts")]
public sealed class ContactImportVCardTests
{
    private const int TenMegabytes = 10 * 1024 * 1024;

    private static MemoryStream Utf8(string text, bool withBom = false)
    {
        byte[] preamble = withBom ? Encoding.UTF8.GetPreamble() : [];
        return new MemoryStream([.. preamble, .. Encoding.UTF8.GetBytes(text)]);
    }

    private static string Card(string body) => "BEGIN:VCARD\r\nVERSION:3.0\r\n" + body + "END:VCARD\r\n";

    private static async Task<(ContactImportReport Report, IReadOnlyList<Contact> Contacts)> ImportAsync(Stream file)
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var contacts = new SqliteContactStore(storage.Database);
        ContactImportReport report = await ContactImportFile.ImportVCardAsync(file, contacts);
        return (report, await contacts.ListAsync());
    }

    // Case 16
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Simple_card_becomes_one_imported_contact_with_its_name()
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8(Card("FN:Contact A\r\nEMAIL;TYPE=INTERNET:contact_a@example.test\r\n")));

        Assert.Equal(1, report.Added);
        Assert.Equal(0, report.Rejected);
        Assert.Equal(0, report.AlreadyKnown);
        Contact only = Assert.Single(contacts);
        Assert.Equal("contact_a@example.test", only.EmailAddress, ignoreCase: true);
        Assert.Equal("Contact A", only.DisplayName);
        Assert.Equal(ContactSource.Imported, only.Source);
    }

    // Case 16: folded lines (RFC 6350: CRLF followed by one space or tab is removed)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folded_name_and_folded_address_lines_are_unfolded()
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8(Card("FN:Contact \r\n Folded A\r\nEMAIL:contact_folded_a@exam\r\n\tple.test\r\n")));

        Assert.Equal(1, report.Added);
        Contact only = Assert.Single(contacts);
        Assert.Equal("contact_folded_a@example.test", only.EmailAddress, ignoreCase: true);
        Assert.Equal("Contact Folded A", only.DisplayName);
    }

    // Case 16: two EMAIL lines -> two contacts with the name of FN
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Card_with_two_email_lines_gives_two_contacts_with_the_same_name()
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8(Card("FN:Contact B\r\nEMAIL;TYPE=WORK:contact_b1@example.test\r\nEMAIL;TYPE=HOME:contact_b2@example.test\r\n")));

        Assert.Equal(2, report.Added);
        Assert.Equal(2, contacts.Count);
        Assert.All(contacts, contact => Assert.Equal("Contact B", contact.DisplayName));
        Assert.All(contacts, contact => Assert.Equal(ContactSource.Imported, contact.Source));
    }

    // Case 16: a card without EMAIL adds nothing and does not stop the file; positive control: the next card is imported
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Card_without_email_adds_nothing_and_the_next_card_is_still_imported()
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8(
            Card("FN:No Email A\r\nTEL:+100000000\r\n") + Card("FN:Contact C\r\nEMAIL:contact_c@example.test\r\n")));

        Assert.Equal(1, report.Added);
        Assert.Equal(1, report.Rejected);
        Assert.Equal(report.Read, report.Added + report.AlreadyKnown + report.Rejected);
        Assert.Equal("contact_c@example.test", Assert.Single(contacts).EmailAddress, ignoreCase: true);
    }

    // Case 16 (decision): entries over the limit of 50,000 are Rejected
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Entries_over_the_limit_of_50000_are_rejected()
    {
        var text = new StringBuilder();
        for (int index = 0; index < 50_001; index++)
        {
            text.Append("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Bad\r\nEMAIL:not_an_address\r\nEND:VCARD\r\n");
        }

        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8(text.ToString()));

        Assert.Equal(0, report.Added);
        Assert.Empty(contacts);
        Assert.Equal(50_001, report.Rejected);
    }

    // Case 16: umlaut in FN, with and without BOM
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Umlaut_in_the_name_survives_utf8_with_and_without_bom(bool withBom)
    {
        (_, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8(Card("FN:Jörg Müller\r\nEMAIL:joerg_a@example.test\r\n"), withBom));

        Assert.Equal("Jörg Müller", Assert.Single(contacts).DisplayName);
    }

    // Case 16: UTF-16 with BOM, both byte orders
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf16_with_bom_is_read(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(Card("FN:Jörg Müller\r\nEMAIL:joerg_a@example.test\r\n"))];

        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(new MemoryStream(bytes));

        Assert.Equal(1, report.Added);
        Contact only = Assert.Single(contacts);
        Assert.Equal("joerg_a@example.test", only.EmailAddress, ignoreCase: true);
        Assert.Equal("Jörg Müller", only.DisplayName);
    }

    // Case 16: LF-only line endings and several cards
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Several_cards_with_lf_line_endings_are_all_read()
    {
        string text = string.Concat(Enumerable.Range(1, 3).Select(
            index => $"BEGIN:VCARD\nVERSION:3.0\nFN:Contact {index}\nEMAIL:contact_{index}@example.test\nEND:VCARD\n"));

        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8(text));

        Assert.Equal(3, report.Added);
        Assert.Equal(3, contacts.Count);
    }

    // Case 16: a malformed address is rejected and never stops the file
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("not_an_address")]
    [InlineData("user_a@")]
    [InlineData("@example.test")]
    public async Task Malformed_address_is_rejected_and_the_following_card_is_imported(string badAddress)
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8(
            Card($"FN:Bad A\r\nEMAIL:{badAddress}\r\n") + Card("FN:Contact C\r\nEMAIL:contact_c@example.test\r\n")));

        Assert.Equal(1, report.Rejected);
        Assert.Equal(1, report.Added);
        Assert.Equal("contact_c@example.test", Assert.Single(contacts).EmailAddress, ignoreCase: true);
    }

    // Case 16: addresses that are already known are counted and left unchanged (case-insensitive)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Known_address_is_counted_as_already_known_and_keeps_its_data()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);
        await store.UpsertAsync(new Contact(0, "contact_a@example.test", "Manual Name", ContactSource.Manual));

        ContactImportReport report = await ContactImportFile.ImportVCardAsync(
            Utf8(Card("FN:Other Name\r\nEMAIL:CONTACT_A@example.test\r\nEMAIL:contact_new@example.test\r\n")), store);

        Assert.Equal(1, report.AlreadyKnown);
        Assert.Equal(1, report.Added);
        Assert.Equal(report.Read, report.Added + report.AlreadyKnown + report.Rejected);
        IReadOnlyList<Contact> contacts = await store.ListAsync();
        Assert.Equal(2, contacts.Count);
        Contact manual = contacts.Single(contact => contact.Source == ContactSource.Manual);
        Assert.Equal("Manual Name", manual.DisplayName);
    }

    // Case 16: the same address twice in one file
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Same_address_twice_in_a_file_is_added_once()
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8(
            Card("FN:Contact A\r\nEMAIL:contact_a@example.test\r\n") + Card("FN:Contact A2\r\nEMAIL:contact_a@example.test\r\n")));

        Assert.Equal(1, report.Added);
        Assert.Equal(1, report.AlreadyKnown);
        Assert.Single(contacts);
    }

    // Case 16: a card without FN still gives a contact
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Card_without_a_name_still_gives_a_contact()
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8(Card("EMAIL:contact_a@example.test\r\n")));

        Assert.Equal(1, report.Added);
        Assert.Single(contacts);
    }

    // Case 16: empty file
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_file_imports_nothing()
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(new MemoryStream());

        Assert.Equal(0, report.Added);
        Assert.Empty(contacts);
    }

    // Case 16: size limit with positive control just below it
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task File_over_ten_megabytes_is_refused_and_one_below_is_read()
    {
        string note = new('n', 1024);
        string cardText = Card("FN:Contact A\r\nEMAIL:contact_a@example.test\r\n");
        var small = new StringBuilder(cardText);
        while (small.Length < TenMegabytes / 2)
        {
            small.Append("X-NOTE:").Append(note).Append("\r\n");
        }

        (ContactImportReport okReport, _) = await ImportAsync(Utf8(small.ToString()));
        Assert.Equal(1, okReport.Added);

        var large = new StringBuilder(cardText);
        while (large.Length <= TenMegabytes)
        {
            large.Append("X-NOTE:").Append(note).Append("\r\n");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(Utf8(large.ToString())));
    }

    // Case 16: cancellation
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cancelled_token_stops_the_import()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContactImportFile.ImportVCardAsync(
            Utf8(Card("FN:Contact A\r\nEMAIL:contact_a@example.test\r\n")), store, source.Token));
    }

    // Case 18
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Control_and_bidi_characters_are_removed_from_the_imported_name()
    {
        (_, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8(Card("FN:Re‮cip​ie\u0007nt A\r\nEMAIL:contact_a@example.test\r\n")));

        Assert.Equal("Recipient A", Assert.Single(contacts).DisplayName);
    }
}
