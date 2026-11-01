using MailWarden.Core.Classification;
using MailWarden.Core.Commands;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>
/// AP-07c safety decisions 1-5 (cases 11, 12, 15, 16, 26, 27, 28). Each negative check queries the queue itself and has
/// a positive control in which the same setup, with only the protecting condition changed, does produce a command.
/// </summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationPassSafetyTests
{
    private static readonly MailKind[] KindCycle = [MailKind.Legit, MailKind.CertainSpam, MailKind.LikelySpam, MailKind.Trusted];

    private static async Task SeedStockAsync(ClassificationHarness harness, MailFolder folder, int count)
    {
        for (uint uid = 1; uid <= count; uid++)
        {
            await harness.AddMailAsync(folder, uid, KindCycle[(int)((uid - 1) % KindCycle.Length)]);
        }
    }

    // ---- decision 2: existing mail is never classified (case 11) ---------------------------------------------

    // Case 11
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task First_pass_with_50_existing_inbox_mails_only_sets_the_marker()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedStockAsync(harness, harness.Inbox, 50);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.RecordOnly);

        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Equal(new ClassificationMarker(harness.Inbox.Id, ClassificationHarness.InboxUidValidity, 50), await harness.MarkerAsync(harness.Inbox));
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(0, report.Classified);
        Assert.Equal(0, report.MovesEnqueued);
        Assert.Equal(0, report.RetentionDeletesEnqueued);
    }

    // Case 11, positive control: the stock is baseline, the next new mail is classified and moved
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mail_arriving_after_the_first_pass_is_classified_and_moved_in_act_mode()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedStockAsync(harness, harness.Inbox, 50);
        await harness.RunAsync(ClassificationHarness.Act);
        Assert.Empty(await harness.CommandsAsync());
        MailMessage fresh = await harness.AddMailAsync(harness.Inbox, 51, MailKind.CertainSpam);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        MailCommand command = CommandAssert.SingleMoveTo(await harness.CommandsAsync(), harness.Quarantine!);
        Assert.Equal(new uint[] { 51 }, command.Uids);
        Assert.Equal(1, report.Classified);
        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(fresh)).Outcome);
    }

    // Case 11 in Act mode: even with every folder present and a contact, the existing stock stays untouched
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task First_pass_in_act_mode_does_not_touch_existing_mail_in_inbox_or_junk()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedStockAsync(harness, harness.Inbox, 20);
        await SeedStockAsync(harness, harness.Junk!, 20);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Equal(20u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
        Assert.Equal(20u, (await harness.MarkerAsync(harness.Junk!))!.ClassifiedUpToUid);
        Assert.Equal(0, report.MovesEnqueued);
        Assert.Equal(0, report.Classified);
    }

    // Case 11, edge: an empty folder also gets its marker
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task First_pass_over_an_empty_folder_sets_the_marker_to_zero()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();

        await harness.RunAsync(ClassificationHarness.RecordOnly);

        Assert.Equal(new ClassificationMarker(harness.Inbox.Id, ClassificationHarness.InboxUidValidity, 0), await harness.MarkerAsync(harness.Inbox));
        Assert.Equal(new ClassificationMarker(harness.Junk!.Id, ClassificationHarness.JunkUidValidity, 0), await harness.MarkerAsync(harness.Junk));
    }

    // ---- decision 1: default mode RecordOnly (case 12) -------------------------------------------------------

    private static async Task<(MailMessage Trusted, MailMessage Legit, MailMessage Spam)> AddThreeNewAsync(ClassificationHarness harness)
    {
        await SeedStockAsync(harness, harness.Inbox, 5);
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SeedMarkerAsync(harness.Junk!);
        MailMessage trusted = await harness.AddMailAsync(harness.Inbox, 6, MailKind.Trusted);
        MailMessage legit = await harness.AddMailAsync(harness.Inbox, 7, MailKind.Legit);
        MailMessage spam = await harness.AddMailAsync(harness.Inbox, 8, MailKind.LikelySpam);
        return (trusted, legit, spam);
    }

    // Case 12
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Record_only_stores_the_entries_but_enqueues_no_command_and_does_not_provision()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        (MailMessage trusted, MailMessage legit, MailMessage spam) = await AddThreeNewAsync(harness);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.RecordOnly);

        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(0, harness.Provisioner.Calls);
        Assert.Equal(3L, await harness.LogCountAsync());
        ClassificationLogEntry trustedEntry = await harness.RequireEntryAsync(trusted);
        ClassificationLogEntry legitEntry = await harness.RequireEntryAsync(legit);
        ClassificationLogEntry spamEntry = await harness.RequireEntryAsync(spam);
        Assert.Equal(ClassificationOutcome.NoAction, trustedEntry.Outcome);
        Assert.Equal(ClassificationAction.None, trustedEntry.PlannedAction);
        Assert.Equal(ClassificationOutcome.Recorded, legitEntry.Outcome);
        Assert.Equal(ClassificationAction.MoveToScreener, legitEntry.PlannedAction);
        Assert.Equal(ClassificationOutcome.Recorded, spamEntry.Outcome);
        Assert.Equal(ClassificationAction.MoveToJunk, spamEntry.PlannedAction);
        Assert.All(new[] { trustedEntry, legitEntry, spamEntry }, entry =>
        {
            Assert.Equal(ClassificationMode.RecordOnly, entry.EffectiveMode);
            Assert.Equal(harness.Clock.GetUtcNow(), entry.ClassifiedAt);
        });
        Assert.Equal(SpamTier.TrustedContact, trustedEntry.Tier);
        Assert.Equal(SpamTier.LikelyLegit, legitEntry.Tier);
        Assert.Equal(SpamTier.LikelySpam, spamEntry.Tier);
        Assert.Equal(8u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
        Assert.Equal(3, report.Classified);
        Assert.Equal(0, report.MovesEnqueued);
        Assert.False(report.ActSuspendedNoContacts);
    }

    // Case 12, positive control: the same three mails in Act mode do produce commands
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_same_new_mails_in_act_mode_enqueue_moves()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        (MailMessage _, MailMessage legit, MailMessage spam) = await AddThreeNewAsync(harness);

        await harness.RunAsync(ClassificationHarness.Act);

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(new uint[] { legit.Uid }, CommandAssert.SingleMoveTo(commands, harness.Screener!).Uids);
        Assert.Equal(new uint[] { spam.Uid }, CommandAssert.SingleMoveTo(commands, harness.Junk!).Uids);
    }

    // Case 12, decision 1: record-only never creates the app folders, even when they are missing
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Record_only_does_not_provision_missing_app_folders()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withScreener: false, withQuarantine: false);
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.Legit);
        await harness.AddMailAsync(harness.Inbox, 2, MailKind.CertainSpam);

        await harness.RunAsync(ClassificationHarness.RecordOnly);

        Assert.Equal(0, harness.Provisioner.Calls);
        Assert.DoesNotContain(await harness.Storage.Folders.ListAsync(harness.Account.Id), folder => folder.Role is FolderRole.Screener or FolderRole.Quarantine);
        Assert.Empty(await harness.CommandsAsync());
    }

    // ---- decision 3: Act is suspended while the address book is empty (cases 15, 16) ---------------------------

    private static async Task AddMailsForEveryTargetAsync(ClassificationHarness harness)
    {
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.Legit);
        await harness.AddMailAsync(harness.Inbox, 2, MailKind.LikelySpam);
        await harness.AddMailAsync(harness.Inbox, 3, MailKind.CertainSpam);
    }

    // Case 15
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Act_with_an_empty_address_book_behaves_like_record_only_and_reports_it()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(
            withScreener: false, withQuarantine: false, withContact: false);
        await AddMailsForEveryTargetAsync(harness);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.True(report.ActSuspendedNoContacts);
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(0, harness.Provisioner.Calls);
        Assert.Equal(0, report.MovesEnqueued);
        Assert.Equal(3L, await harness.LogCountAsync());
        Assert.Equal(3, report.Classified);
        Assert.Equal(3u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
        foreach (uint uid in new uint[] { 1, 2, 3 })
        {
            ClassificationLogEntry entry = await harness.RequireEntryAsync((await harness.Storage.Messages.GetByUidAsync(harness.Inbox.Id, uid))!);
            Assert.Equal(ClassificationMode.RecordOnly, entry.EffectiveMode);
            Assert.Equal(ClassificationOutcome.Recorded, entry.Outcome);
            Assert.NotEqual(ClassificationAction.None, entry.PlannedAction);
        }
    }

    // Case 15, positive control: with one contact the very same setup moves mail
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Act_with_one_contact_in_the_address_book_enqueues_moves()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(
            withScreener: false, withQuarantine: false, withContact: false);
        await AddMailsForEveryTargetAsync(harness);
        await harness.AddContactAsync("contact_b@example.test");

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.False(report.ActSuspendedNoContacts);
        Assert.Equal(1, harness.Provisioner.Calls);
        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(3, commands.Count(command => command.Kind == MailCommandKind.Move));
    }

    // Case 15, edge: a suspended pass is only for this pass; once a contact exists the following mail is moved
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mail_recorded_while_act_was_suspended_is_not_moved_later()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false);
        await AddMailsForEveryTargetAsync(harness);
        await harness.RunAsync(ClassificationHarness.Act);
        await harness.AddContactAsync("contact_b@example.test");
        MailMessage later = await harness.AddMailAsync(harness.Inbox, 4, MailKind.Legit);

        await harness.RunAsync(ClassificationHarness.Act);

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        MailCommand command = CommandAssert.SingleMoveTo(commands, harness.Screener!);
        Assert.Equal(new uint[] { later.Uid }, command.Uids);
        CommandAssert.NoMoveTo(commands, harness.Junk!);
        CommandAssert.NoMoveTo(commands, harness.Quarantine!);
    }

    // Case 16
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Act_stays_suspended_when_the_only_known_addresses_are_the_accounts_own()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false);
        await harness.Storage.AddAccountAsync("Account_B", "own_b@example.test");
        await AddMailsForEveryTargetAsync(harness);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.True(report.ActSuspendedNoContacts);
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(0, harness.Provisioner.Calls);
    }

    // ---- decision 5: retention (cases 26, 27, 28) --------------------------------------------------------------

    private static readonly DateTimeOffset VeryOldDate = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<MailMessage> SeedQuarantinedAsync(
        ClassificationHarness harness,
        uint uid,
        TimeSpan decisionAge,
        DateTimeOffset? messageDate = null,
        ClassificationOutcome outcome = ClassificationOutcome.Enqueued,
        ClassificationAction action = ClassificationAction.MoveToQuarantine,
        bool withEntry = true)
    {
        MailMessage message = await harness.AddRawAsync(
            harness.Quarantine!, uid, null, $"<quarantine_{uid}@example.test>", messageDate ?? harness.Clock.GetUtcNow());
        if (withEntry)
        {
            await harness.SeedEntryAsync(message, harness.Clock.GetUtcNow() - decisionAge, outcome, action);
        }

        return message;
    }

    // Case 26
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Retention_zero_never_deletes_even_ancient_quarantine_mail()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedQuarantinedAsync(harness, 1, TimeSpan.FromDays(400), VeryOldDate);
        await SeedQuarantinedAsync(harness, 2, TimeSpan.FromDays(3000));

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithRetention(0));

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Empty(commands);
        CommandAssert.NoDeletes(commands);
        Assert.Equal(0, report.RetentionDeletesEnqueued);
        Assert.Equal(2, (await harness.Storage.Messages.GetUidFlagsAsync(harness.Quarantine!.Id)).Count);
    }

    // Case 26, positive control: the same data with retention 30 does enqueue the delete
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_same_ancient_quarantine_mail_is_deleted_once_retention_is_switched_on()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedQuarantinedAsync(harness, 1, TimeSpan.FromDays(400), VeryOldDate);
        await SeedQuarantinedAsync(harness, 2, TimeSpan.FromDays(3000));
        Assert.Empty(await harness.CommandsAsync());

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithRetention(30));

        MailCommand command = Assert.Single(await harness.CommandsAsync());
        Assert.Equal(MailCommandKind.DeletePermanently, command.Kind);
        Assert.Equal(harness.Quarantine!.Id, command.FolderId);
        Assert.Equal(FakeFolderProvisioner.QuarantineUidValidity, command.FolderUidValidity);
        Assert.Equal(new uint[] { 1, 2 }, command.Uids);
        Assert.Equal(2, report.RetentionDeletesEnqueued);
    }

    // Case 27: both boundaries (exactly the retention days, one second less), decision time, entry kind
    public static TheoryData<string, double, bool, string, string, bool> RetentionMatrix() => new()
    {
        // description, decision age in days, message Date is ancient, outcome, action, expected deleted
        { "31 days old", 31, false, "Enqueued", "MoveToQuarantine", true },
        { "exactly 30 days old", 30, false, "Enqueued", "MoveToQuarantine", true },
        { "30 days minus one second", 30 - (1.0 / 86400), false, "Enqueued", "MoveToQuarantine", false },
        { "29 days old", 29, false, "Enqueued", "MoveToQuarantine", false },
        { "recent decision but ancient Date header", 1, true, "Enqueued", "MoveToQuarantine", false },
        { "old decision but recent Date header", 400, false, "Enqueued", "MoveToQuarantine", true },
        { "decision in the future", -5, true, "Enqueued", "MoveToQuarantine", false },
        { "old entry only recorded", 400, true, "Recorded", "MoveToQuarantine", false },
        { "old entry target missing", 400, true, "TargetMissing", "MoveToQuarantine", false },
        { "old entry enqueue failed", 400, true, "EnqueueFailed", "MoveToQuarantine", false },
        { "old entry no action", 400, true, "NoAction", "None", false },
        { "old entry moved to screener", 400, true, "Enqueued", "MoveToScreener", false },
        { "old entry moved to junk", 400, true, "Enqueued", "MoveToJunk", false },
        { "old entry moved to inbox", 400, true, "Enqueued", "MoveToInbox", false },
    };

    // Case 27
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(RetentionMatrix))]
    public async Task Retention_30_deletes_only_what_the_decision_time_and_the_entry_allow(
        string description, double decisionAgeDays, bool ancientDateHeader, string outcomeName, string actionName, bool expectedDeleted)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        MailMessage message = await SeedQuarantinedAsync(
            harness,
            7,
            TimeSpan.FromDays(decisionAgeDays),
            ancientDateHeader ? VeryOldDate : harness.Clock.GetUtcNow(),
            Enum.Parse<ClassificationOutcome>(outcomeName),
            Enum.Parse<ClassificationAction>(actionName));

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithRetention(30));
        Assert.False(string.IsNullOrEmpty(description));

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        if (expectedDeleted)
        {
            MailCommand command = Assert.Single(commands);
            Assert.Equal(MailCommandKind.DeletePermanently, command.Kind);
            Assert.Equal(new[] { message.Uid }, command.Uids);
            Assert.Equal(1, report.RetentionDeletesEnqueued);
        }
        else
        {
            Assert.Empty(commands);
            Assert.Equal(0, report.RetentionDeletesEnqueued);
        }
    }

    // Case 27: a mail without any entry is never deleted, however old
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Quarantine_mail_without_a_log_entry_is_never_deleted()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedQuarantinedAsync(harness, 1, TimeSpan.Zero, VeryOldDate, withEntry: false);
        await SeedQuarantinedAsync(harness, 2, TimeSpan.Zero, VeryOldDate, withEntry: false);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithRetention(30));

        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(0, report.RetentionDeletesEnqueued);
    }

    // Case 27: mixed set in one pass; only the eligible UIDs are in the command
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Retention_30_deletes_only_the_eligible_messages_of_a_mixed_quarantine()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedQuarantinedAsync(harness, 1, TimeSpan.FromDays(31));
        await SeedQuarantinedAsync(harness, 2, TimeSpan.FromDays(29), VeryOldDate);
        await SeedQuarantinedAsync(harness, 3, TimeSpan.FromDays(30));
        await SeedQuarantinedAsync(harness, 4, TimeSpan.Zero, VeryOldDate, withEntry: false);
        await SeedQuarantinedAsync(harness, 5, TimeSpan.FromDays(400), VeryOldDate, ClassificationOutcome.Recorded);

        await harness.RunAsync(ClassificationHarness.ActWithRetention(30));

        MailCommand command = Assert.Single(await harness.CommandsAsync());
        Assert.Equal(MailCommandKind.DeletePermanently, command.Kind);
        Assert.Equal(new uint[] { 1, 3 }, command.Uids);
    }

    // Case 27: a message of the same key that sits in the Inbox is not touched by retention
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Retention_never_deletes_from_other_folders_than_quarantine()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        MailMessage inInbox = await harness.AddRawAsync(harness.Inbox, 1, null, "<inbox_old@example.test>", VeryOldDate);
        await harness.SeedEntryAsync(inInbox, harness.Clock.GetUtcNow() - TimeSpan.FromDays(400));
        MailMessage inJunk = await harness.AddRawAsync(harness.Junk!, 1, null, "<junk_old@example.test>", VeryOldDate);
        await harness.SeedEntryAsync(inJunk, harness.Clock.GetUtcNow() - TimeSpan.FromDays(400));
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SeedMarkerAsync(harness.Junk!);

        await harness.RunAsync(ClassificationHarness.ActWithRetention(30));

        Assert.Empty(await harness.CommandsAsync());
    }

    // Case 28: not in RecordOnly, not while Act is suspended
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(ClassificationMode.RecordOnly, true)]
    [InlineData(ClassificationMode.Act, false)]
    public async Task Retention_is_off_in_record_only_and_in_suspended_act(ClassificationMode mode, bool hasContact)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: hasContact);
        await SeedQuarantinedAsync(harness, 1, TimeSpan.FromDays(400), VeryOldDate);

        ClassificationPassReport report = await harness.RunAsync(new ClassificationPolicy(mode, 30));

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Empty(commands);
        Assert.Equal(0, report.RetentionDeletesEnqueued);
        Assert.Equal(mode == ClassificationMode.Act, report.ActSuspendedNoContacts);
    }

    // Case 28, positive control: the same data with Act and a contact deletes
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Retention_runs_in_effective_act_with_the_same_data()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: true);
        await SeedQuarantinedAsync(harness, 1, TimeSpan.FromDays(400), VeryOldDate);

        await harness.RunAsync(new ClassificationPolicy(ClassificationMode.Act, 30));

        MailCommand command = Assert.Single(await harness.CommandsAsync());
        Assert.Equal(MailCommandKind.DeletePermanently, command.Kind);
        Assert.Equal(new uint[] { 1 }, command.Uids);
    }

    // Case 26-28: a classification pass never deletes a mail on its own, whatever the tier
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Classification_moves_but_never_deletes_certain_spam()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.CertainSpam);

        await harness.RunAsync(ClassificationHarness.ActWithRetention(30));

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        CommandAssert.NoDeletes(commands);
        Assert.Equal(MailCommandKind.Move, Assert.Single(commands).Kind);
        Assert.Single(await harness.Storage.Messages.GetUidFlagsAsync(harness.Inbox.Id));
    }
}
