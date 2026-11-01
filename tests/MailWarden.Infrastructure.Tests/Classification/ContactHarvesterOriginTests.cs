using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>
/// AP-06 fix.2 R3 / case 22: only mail that the account owner wrote is harvested. A mail is skipped (counted in
/// <c>MailsSkipped</c>) when From is not exactly one own account address, when Auto-Submitted is present and not "no",
/// or when the top Content-Type is multipart/report. Each blocking rule has a read control.
/// </summary>
[Trait("Category", "feature:contacts")]
[Trait("Category", "feature:classifier")]
public sealed class ContactHarvesterOriginTests
{
    private const string Recipient = "recipient_a@example.test";

    private static async Task<ContactHarvestReport> HarvestOneAsync(
        string? from = null, string? extraHeader = null, string contentType = "text/plain; charset=utf-8")
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.Storage.AddAccountAsync("Account_B", "second_own_a@example.test");
        await harness.AddSentAsync(harness.Sent!, 1, to: Recipient, from: from, extraHeader: extraHeader, contentType: contentType);

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(report.ContactsAdded, (await harness.ContactsAsync()).Count);
        Assert.Equal(1u, (await harness.HarvestMarkers.GetAsync(harness.Sent!.Id))!.HarvestedUpToUid);
        return report;
    }

    // Case 22: control, the default mail (From = own address with a name) is read
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Mail_from_the_own_address_is_read()
    {
        Assert.Equal(new ContactHarvestReport(1, 1, 0), await HarvestOneAsync());
    }

    // Case 22: From is another address
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("Stranger A <stranger_a@example.test>")]
    [InlineData("stranger_a@example.test")]
    public async Task Mail_whose_from_is_not_exactly_one_own_address_is_skipped(string from)
    {
        ContactHarvestReport report = await HarvestOneAsync(from);

        Assert.Equal(new ContactHarvestReport(0, 0, 1), report);
    }

    // Case 22: two addresses in From, one of them own -> not exactly one
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mail_with_two_from_addresses_one_of_them_own_is_skipped()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(
            harness.Sent!, 1, to: Recipient, from: $"{harness.Account.EmailAddress}, other_a@example.test");

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(0, 0, 1), report);
        Assert.Empty(await harness.ContactsAsync());
    }

    // Case 22: no From header at all
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mail_with_an_unusable_from_is_skipped()
    {
        ContactHarvestReport report = await HarvestOneAsync(from: "not_an_address");

        Assert.Equal(new ContactHarvestReport(0, 0, 1), report);
    }

    // Case 22: control, own address in another letter case, bare or with a name, and the address of the other account
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("SECOND_OWN_A@EXAMPLE.TEST")]
    [InlineData("Second <Second_Own_A@Example.Test>")]
    public async Task Own_address_in_any_letter_case_or_of_another_account_is_read(string from)
    {
        Assert.Equal(new ContactHarvestReport(1, 1, 0), await HarvestOneAsync(from));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Own_address_of_the_account_in_upper_case_is_read()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 1, to: Recipient, from: harness.Account.EmailAddress.ToUpperInvariant());

        Assert.Equal(new ContactHarvestReport(1, 1, 0), await harness.HarvestAsync());
    }

    // Case 22: Auto-Submitted other than "no" is skipped
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("Auto-Submitted: auto-replied")]
    [InlineData("Auto-Submitted: auto-generated")]
    [InlineData("auto-submitted: Auto-Replied")]
    public async Task Auto_submitted_mail_is_skipped(string header)
    {
        Assert.Equal(new ContactHarvestReport(0, 0, 1), await HarvestOneAsync(extraHeader: header));
    }

    // Case 22: "no" counts as a normal mail, in any letter case
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("Auto-Submitted: no")]
    [InlineData("Auto-Submitted: NO")]
    public async Task Auto_submitted_no_is_read(string header)
    {
        Assert.Equal(new ContactHarvestReport(1, 1, 0), await HarvestOneAsync(extraHeader: header));
    }

    // Case 22: multipart/report (delivery status notifications)
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("multipart/report; report-type=delivery-status; boundary=\"report_boundary_a\"")]
    [InlineData("Multipart/Report; boundary=report_boundary_a")]
    public async Task Multipart_report_is_skipped(string contentType)
    {
        Assert.Equal(new ContactHarvestReport(0, 0, 1), await HarvestOneAsync(contentType: contentType));
    }

    // Case 22: control, another multipart type is read
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Multipart_mixed_is_read()
    {
        Assert.Equal(
            new ContactHarvestReport(1, 1, 0),
            await HarvestOneAsync(contentType: "multipart/mixed; boundary=\"mixed_boundary_a\""));
    }

    // Case 22: a skipped mail does not stop the next one
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Skipped_mail_does_not_stop_the_following_mails()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false, withSent: true);
        await harness.AddSentAsync(harness.Sent!, 1, to: "skipped_a@example.test", from: "stranger_a@example.test");
        await harness.AddSentAsync(harness.Sent!, 2, to: "skipped_b@example.test", extraHeader: "Auto-Submitted: auto-replied");
        await harness.AddSentAsync(harness.Sent!, 3, to: Recipient);

        ContactHarvestReport report = await harness.HarvestAsync();

        Assert.Equal(new ContactHarvestReport(1, 1, 2), report);
        Assert.Equal(Recipient, Assert.Single(await harness.ContactsAsync()).EmailAddress, ignoreCase: true);
    }
}
