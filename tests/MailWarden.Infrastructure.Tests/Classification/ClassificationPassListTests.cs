using MailWarden.Core.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>
/// AP-06 case 5 / D3: the pass reads the allow and block lists from the real store. Blocked beats everything (also a
/// contact), allowed beats everything except failed authentication.
/// </summary>
[Trait("Category", "feature:classifier")]
[Trait("Category", "feature:contacts")]
public sealed class ClassificationPassListTests
{
    // Sender of MailKind.Legit (all authentication passes) and of MailKind.LikelySpam (SPF and DKIM fail).
    private const string PassingSender = "stranger_a@" + ClassificationTestEml.PlausibleDomain;

    private const string FailingSender = "stranger_b@" + ClassificationTestEml.PlausibleDomain;

    private static async Task<SpamTier?> TierOfAsync(ClassificationHarness harness, MailMessage message) =>
        (await harness.RequireEntryAsync(message)).Tier;

    // Case 5: positive control, the mail is not spam without a list
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Without_lists_the_passing_stranger_is_not_certain_spam_and_not_trusted()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage legit = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit);

        await harness.RunAsync(ClassificationHarness.RecordOnly);

        Assert.Equal(SpamTier.LikelyLegit, await TierOfAsync(harness, legit));
    }

    // Case 5: blocked sender in the Inbox
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Blocked_address_in_the_inbox_is_certain_spam()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SenderLists.SetAsync(new SenderListEntry(PassingSender, false, ListMembership.Blocked));
        MailMessage legit = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit);

        await harness.RunAsync(ClassificationHarness.RecordOnly);

        ClassificationLogEntry entry = await harness.RequireEntryAsync(legit);
        Assert.Equal(SpamTier.CertainSpam, entry.Tier);
        Assert.Equal(AuthStatus.Authenticated, entry.AuthStatus);
    }

    // Case 5: a blocked domain beats a contact and a passing authentication
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Blocked_domain_beats_a_contact_of_that_domain()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage trusted = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Trusted);
        await harness.SenderLists.SetAsync(new SenderListEntry(ClassificationTestEml.PlausibleDomain, true, ListMembership.Blocked));

        await harness.RunAsync(ClassificationHarness.RecordOnly);

        Assert.Equal(SpamTier.CertainSpam, await TierOfAsync(harness, trusted));
    }

    // Case 5: positive control for the contact in the same setup
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Without_the_block_the_contact_mail_is_trusted()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage trusted = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Trusted);

        await harness.RunAsync(ClassificationHarness.RecordOnly);

        Assert.Equal(SpamTier.TrustedContact, await TierOfAsync(harness, trusted));
    }

    // Case 5: allowed sender with failed authentication is not trusted
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Allowed_address_with_failed_authentication_is_not_a_trusted_contact()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SenderLists.SetAsync(new SenderListEntry(FailingSender, false, ListMembership.Allowed));
        MailMessage failing = await harness.AddMailAsync(harness.Inbox, 11, MailKind.LikelySpam);

        await harness.RunAsync(ClassificationHarness.RecordOnly);

        ClassificationLogEntry entry = await harness.RequireEntryAsync(failing);
        Assert.Equal(AuthStatus.Failed, entry.AuthStatus);
        Assert.NotEqual(SpamTier.TrustedContact, entry.Tier);
    }

    // Case 5: positive control, the same allow entry with passing authentication is trusted
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Allowed_address_with_passing_authentication_is_a_trusted_contact()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SenderLists.SetAsync(new SenderListEntry(PassingSender, false, ListMembership.Allowed));
        MailMessage passing = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit);

        await harness.RunAsync(ClassificationHarness.RecordOnly);

        Assert.Equal(SpamTier.TrustedContact, await TierOfAsync(harness, passing));
    }

    // Case 5: allowed domain, the whole run in one pass with both mails
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Allowed_domain_trusts_passing_mail_but_not_failing_mail_in_the_same_pass()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SenderLists.SetAsync(new SenderListEntry(ClassificationTestEml.PlausibleDomain, true, ListMembership.Allowed));
        MailMessage passing = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit);
        MailMessage failing = await harness.AddMailAsync(harness.Inbox, 12, MailKind.LikelySpam);

        await harness.RunAsync(ClassificationHarness.RecordOnly);

        Assert.Equal(SpamTier.TrustedContact, await TierOfAsync(harness, passing));
        Assert.NotEqual(SpamTier.TrustedContact, await TierOfAsync(harness, failing));
    }

    // Case 5: Act mode moves a blocked mail to the quarantine (the list reaches the decision, not only the record)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Blocked_address_in_act_mode_is_moved_out_of_the_inbox()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SenderLists.SetAsync(new SenderListEntry(PassingSender, false, ListMembership.Blocked));
        MailMessage legit = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit);

        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(legit)).Outcome);
        Assert.Single(ClassificationHarness.MovesFrom(await harness.CommandsAsync(), harness.Inbox, harness.Quarantine!));
    }
}
