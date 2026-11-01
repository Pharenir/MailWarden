using System.Text;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-06 D6 / cases 17, 18: <see cref="ContactImportFile.ImportCsvAsync"/>. All values are artificial.</summary>
[Trait("Category", "feature:contacts")]
public sealed class ContactImportCsvTests
{
    private const int TenMegabytes = 10 * 1024 * 1024;

    private static MemoryStream Utf8(string text, bool withBom = false)
    {
        byte[] preamble = withBom ? Encoding.UTF8.GetPreamble() : [];
        return new MemoryStream([.. preamble, .. Encoding.UTF8.GetBytes(text)]);
    }

    private static async Task<(ContactImportReport Report, IReadOnlyList<Contact> Contacts)> ImportAsync(Stream file)
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var contacts = new SqliteContactStore(storage.Database);
        ContactImportReport report = await ContactImportFile.ImportCsvAsync(file, contacts);
        return (report, await contacts.ListAsync());
    }

    private static Contact Find(IReadOnlyList<Contact> contacts, string address) =>
        contacts.Single(contact => string.Equals(contact.EmailAddress, address, StringComparison.OrdinalIgnoreCase));

    // Case 17
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Comma_separated_file_gives_imported_contacts_with_names()
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8("Name,Email\r\nContact A,contact_a@example.test\r\nContact B,contact_b@example.test\r\n"));

        Assert.Equal(2, report.Added);
        Assert.Equal(0, report.Rejected);
        Assert.Equal("Contact A", Find(contacts, "contact_a@example.test").DisplayName);
        Assert.All(contacts, contact => Assert.Equal(ContactSource.Imported, contact.Source));
    }

    // Case 17: ';' detected from the header
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Semicolon_separated_file_is_detected_from_the_header()
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8("Name;E-Mail\r\nContact A;contact_a@example.test\r\nContact B;contact_b@example.test\r\n"));

        Assert.Equal(2, report.Added);
        Assert.Equal("Contact B", Find(contacts, "contact_b@example.test").DisplayName);
    }

    // Case 17: column names in any letter case and in any order
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("EMAIL,NAME")]
    [InlineData("e-mail,name")]
    [InlineData("E-Mail Address,Display Name")]
    [InlineData("display name,e-mail address")]
    public async Task Header_names_are_case_insensitive_and_known_variants_are_accepted(string header)
    {
        string[] columns = header.Split(',');
        bool emailFirst = columns[0].Contains("mail", StringComparison.OrdinalIgnoreCase);
        string row = emailFirst ? "contact_a@example.test,Contact A" : "Contact A,contact_a@example.test";

        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8($"{header}\r\n{row}\r\n"));

        Assert.Equal(1, report.Added);
        Contact only = Assert.Single(contacts);
        Assert.Equal("contact_a@example.test", only.EmailAddress, ignoreCase: true);
        Assert.Equal("Contact A", only.DisplayName);
    }

    // Case 17: quotes with the separator inside, doubled quotes
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(',')]
    [InlineData(';')]
    public async Task Quoted_fields_may_hold_the_separator_and_doubled_quotes(char separator)
    {
        string text =
            $"Name{separator}Email\r\n" +
            $"\"Doe{separator} Jane\"{separator}contact_a@example.test\r\n" +
            $"\"Say \"\"hi\"\" Joe\"{separator}contact_b@example.test\r\n";

        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8(text));

        Assert.Equal(2, report.Added);
        Assert.Equal($"Doe{separator} Jane", Find(contacts, "contact_a@example.test").DisplayName);
        Assert.Equal("Say \"hi\" Joe", Find(contacts, "contact_b@example.test").DisplayName);
    }

    // Case 17: a quoted field with a line break does not split the row; positive control: the next row is read
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Quoted_field_with_a_line_break_stays_one_row()
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8("Name,Email\r\n\"Contact\r\nA\",contact_a@example.test\r\nContact B,contact_b@example.test\r\n"));

        Assert.Equal(2, report.Added);
        Assert.Equal(2, contacts.Count);
    }

    // Case 17: Outlook-style header: name from first and last name, address from "E-mail Address"
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Outlook_style_header_builds_the_name_from_first_and_last_name()
    {
        const string header = "First Name,Middle Name,Last Name,Title,Company,Business Phone,E-mail Address,E-mail Display Name";
        string text =
            header + "\r\n" +
            "Jane,,Doe,,Example Corp,+100000000,contact_a@example.test,Jane Doe\r\n" +
            "Joe,,Roe,,Example Corp,+100000001,contact_b@example.test,Joe Roe\r\n";

        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8(text));

        Assert.Equal(2, report.Added);
        Assert.Equal("Jane Doe", Find(contacts, "contact_a@example.test").DisplayName);
        Assert.Equal("Joe Roe", Find(contacts, "contact_b@example.test").DisplayName);
    }

    // Case 17: Display Name wins over first/last name when both exist (assumption: the explicit full name is the better one)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Display_name_column_is_used_when_present()
    {
        (_, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8("First Name,Last Name,Display Name,Email\r\nJane,Doe,Dr. Jane Doe,contact_a@example.test\r\n"));

        Assert.Equal("Dr. Jane Doe", Assert.Single(contacts).DisplayName);
    }

    // Case 17: no usable header -> InvalidDataException that names the problem, not the values
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("alpha,beta\r\nsecret_value_a,secret_value_b\r\n")]
    [InlineData("Name,Phone\r\nsecret_value_a,secret_value_b\r\n")]
    [InlineData("secret_value_a@example.test,Contact A\r\nsecret_value_b@example.test,Contact B\r\n")]
    [InlineData("")]
    [InlineData("\r\n\r\n")]
    public async Task File_without_a_usable_header_is_refused_without_naming_values(string text)
    {
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(Utf8(text)));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        Assert.DoesNotContain("secret_value", exception.Message, StringComparison.Ordinal);
    }

    // Case 17: malformed address -> rejected, never stops the file; positive controls before and after
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Malformed_addresses_are_rejected_and_the_other_rows_are_imported()
    {
        string text =
            "Name,Email\r\n" +
            "Contact A,contact_a@example.test\r\n" +
            "Bad A,not_an_address\r\n" +
            "Bad B,user_a@\r\n" +
            "Bad C,@example.test\r\n" +
            "Contact B,contact_b@example.test\r\n";

        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8(text));

        Assert.Equal(3, report.Rejected);
        Assert.Equal(2, report.Added);
        Assert.Equal(report.Read, report.Added + report.AlreadyKnown + report.Rejected);
        Assert.Equal(2, contacts.Count);
    }

    // Case 17: known and repeated addresses
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Known_and_repeated_addresses_count_as_already_known()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);
        await store.UpsertAsync(new Contact(0, "contact_a@example.test", "Manual Name", ContactSource.Manual));

        ContactImportReport report = await ContactImportFile.ImportCsvAsync(
            Utf8("Name,Email\r\nOther Name,CONTACT_A@example.test\r\nContact B,contact_b@example.test\r\nContact B2,contact_b@example.test\r\n"),
            store);

        Assert.Equal(1, report.Added);
        Assert.Equal(2, report.AlreadyKnown);
        IReadOnlyList<Contact> contacts = await store.ListAsync();
        Assert.Equal("Manual Name", Find(contacts, "contact_a@example.test").DisplayName);
        Assert.Equal("Contact B", Find(contacts, "contact_b@example.test").DisplayName);
    }

    // Case 17: encodings
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Utf8_with_bom_and_umlaut_is_read_and_the_bom_does_not_spoil_the_first_column()
    {
        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8("Name,Email\r\nJörg Müller,joerg_a@example.test\r\n", withBom: true));

        Assert.Equal(1, report.Added);
        Assert.Equal("Jörg Müller", Assert.Single(contacts).DisplayName);
    }

    // Case 17
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Utf8_without_bom_and_umlaut_is_read()
    {
        (_, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8("Name;Email\r\nJörg Müller;joerg_a@example.test\r\n"));

        Assert.Equal("Jörg Müller", Assert.Single(contacts).DisplayName);
    }

    // Case 17
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf16_with_bom_is_read(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("Name,Email\r\nJörg Müller,joerg_a@example.test\r\n")];

        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(new MemoryStream(bytes));

        Assert.Equal(1, report.Added);
        Assert.Equal("Jörg Müller", Assert.Single(contacts).DisplayName);
    }

    // Case 17: size limit, positive control below it
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task File_over_ten_megabytes_is_refused_and_one_below_is_read()
    {
        const string header = "Name,Email,Notes\r\n";
        string filler = new('n', 1024);
        var small = new StringBuilder(header).Append("Contact A,contact_a@example.test,x\r\n");
        while (small.Length < TenMegabytes / 2)
        {
            small.Append("Filler,filler_a@example.test,").Append(filler).Append("\r\n");
        }

        (ContactImportReport okReport, _) = await ImportAsync(Utf8(small.ToString()));
        Assert.True(okReport.Added >= 1);

        var large = new StringBuilder(header).Append("Contact A,contact_a@example.test,x\r\n");
        while (large.Length <= TenMegabytes)
        {
            large.Append("Filler,filler_a@example.test,").Append(filler).Append("\r\n");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(Utf8(large.ToString())));
    }

    // Case 25 (R6): an unterminated quote costs only its own physical line
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unterminated_quote_rejects_only_that_line_and_the_other_rows_are_read()
    {
        string text =
            "Name,Email\r\n" +
            "Contact A,contact_a@example.test\r\n" +
            "\"Broken Name,contact_x@example.test\r\n" +
            "Contact C,contact_c@example.test\r\n" +
            "Contact D,contact_d@example.test\r\n" +
            "Contact E,contact_e@example.test\r\n";

        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8(text));

        Assert.Equal(1, report.Rejected);
        Assert.Equal(4, report.Added);
        Assert.Equal(4, contacts.Count);
        Assert.DoesNotContain(contacts, contact => contact.EmailAddress.StartsWith("contact_x", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(Find(contacts, "contact_e@example.test"));
    }

    // Decision: text after a closing quote makes the field malformed: only that line is Rejected
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Text_after_a_closing_quote_rejects_that_line_only()
    {
        string text =
            "Name,Email\r\n" +
            "Contact A,contact_a@example.test\r\n" +
            "\"a\"b,contact_x@example.test\r\n" +
            "Contact C,contact_c@example.test\r\n";

        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8(text));

        Assert.Equal(1, report.Rejected);
        Assert.Equal(2, report.Added);
        Assert.DoesNotContain(contacts, contact => contact.EmailAddress.StartsWith("contact_x", StringComparison.OrdinalIgnoreCase));
    }

    // Decision: a quote that only closes cleanly in a later line is one multi-line field (control)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Quote_that_closes_in_a_later_line_is_a_multi_line_field()
    {
        string text =
            "Name,Email\r\n" +
            "\"Line one\r\nLine two\",contact_a@example.test\r\n" +
            "Contact C,contact_c@example.test\r\n";

        (ContactImportReport report, _) = await ImportAsync(Utf8(text));

        Assert.Equal(0, report.Rejected);
        Assert.Equal(2, report.Added);
    }

    // Case 25: control, the same file with the quote closed rejects nothing
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Closed_quote_in_the_same_position_rejects_nothing()
    {
        string text =
            "Name,Email\r\n" +
            "Contact A,contact_a@example.test\r\n" +
            "\"Broken Name\",contact_x@example.test\r\n" +
            "Contact C,contact_c@example.test\r\n";

        (ContactImportReport report, _) = await ImportAsync(Utf8(text));

        Assert.Equal(0, report.Rejected);
        Assert.Equal(3, report.Added);
    }

    // Case 17 (decision): entries over the limit of 50,000 are Rejected
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Entries_over_the_limit_of_50000_are_rejected()
    {
        var text = new StringBuilder("Name,Email\r\n");
        for (int index = 0; index < 50_001; index++)
        {
            text.Append("Bad,not_an_address\r\n");
        }

        (ContactImportReport report, IReadOnlyList<Contact> contacts) = await ImportAsync(Utf8(text.ToString()));

        Assert.Equal(0, report.Added);
        Assert.Empty(contacts);
        Assert.Equal(50_001, report.Rejected);
    }

    // Case 17: cancellation
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cancelled_token_stops_the_import()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteContactStore(storage.Database);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContactImportFile.ImportCsvAsync(
            Utf8("Name,Email\r\nContact A,contact_a@example.test\r\n"), store, source.Token));
    }

    // Case 18
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Control_and_bidi_characters_are_removed_from_the_imported_name()
    {
        (_, IReadOnlyList<Contact> contacts) = await ImportAsync(
            Utf8("Name,Email\r\nRe‮cip​ie\u0007nt A,contact_a@example.test\r\n"));

        Assert.Equal("Recipient A", Assert.Single(contacts).DisplayName);
    }
}
