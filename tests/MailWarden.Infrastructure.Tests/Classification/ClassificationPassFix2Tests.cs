using System.Text;
using MailWarden.Core.Classification;
using MailWarden.Core.Commands;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-07c fix.2 (G1, G2, G6, G7, G8) / cases 34, 35, 37-40, 42-44.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationPassFix2Tests
{
    private const string NoSenderText = "Message has no single usable sender";

    private static readonly DateTimeOffset AncientDate = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- case 34: sync state (G1) ------------------------------------------------------------------------------------

    // Case 34
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folder_without_sync_state_is_skipped_then_baselined_then_classified()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withSyncState: false);
        for (uint uid = 1; uid <= 5; uid++)
        {
            await harness.AddMailAsync(harness.Inbox, uid, MailKind.CertainSpam);
        }

        await harness.AddMailAsync(harness.Junk!, 1, MailKind.Trusted);
        await harness.AddMailAsync(harness.Junk!, 2, MailKind.Trusted);

        ClassificationPassReport first = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Null(await harness.MarkerAsync(harness.Inbox));
        Assert.Null(await harness.MarkerAsync(harness.Junk!));
        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(0, first.Classified);

        await harness.MarkSyncedAsync(harness.Inbox);
        await harness.MarkSyncedAsync(harness.Junk!);
        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(new ClassificationMarker(harness.Inbox.Id, ClassificationHarness.InboxUidValidity, 5), await harness.MarkerAsync(harness.Inbox));
        Assert.Equal(new ClassificationMarker(harness.Junk!.Id, ClassificationHarness.JunkUidValidity, 2), await harness.MarkerAsync(harness.Junk));
        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Empty(await harness.CommandsAsync());

        MailMessage fresh = await harness.AddMailAsync(harness.Inbox, 6, MailKind.CertainSpam);
        ClassificationPassReport third = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1, third.Classified);
        Assert.Equal(1L, await harness.LogCountAsync());
        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(fresh)).Outcome);
        Assert.Equal(new uint[] { 6 }, Assert.Single(await harness.CommandsAsync()).Uids);
    }

    // Case 34: the state is per folder
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Only_the_folder_without_sync_state_is_skipped()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withSyncState: false);
        await harness.MarkSyncedAsync(harness.Inbox);
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.CertainSpam);
        await harness.AddMailAsync(harness.Junk!, 1, MailKind.Trusted);

        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
        Assert.Null(await harness.MarkerAsync(harness.Junk!));
        Assert.Equal(1L, await harness.LogCountAsync());
        CommandAssert.NoMoveTo(await harness.CommandsAsync(), harness.Inbox);
    }

    // Case 35
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Uid_validity_reset_with_partial_reload_classifies_nothing()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        for (uint uid = 1; uid <= 8; uid++)
        {
            await harness.AddMailAsync(harness.Inbox, uid, MailKind.CertainSpam);
        }

        ClassificationMarker old = await harness.SeedMarkerAsync(harness.Inbox);
        Assert.Equal(8u, old.ClassifiedUpToUid);

        // The sync engine's reset: new validity stored, state deleted, old messages gone, mails reload step by step.
        const uint newValidity = ClassificationHarness.InboxUidValidity + 777;
        MailFolder reset = await harness.Storage.Folders.UpsertAsync(harness.Inbox with { UidValidity = newValidity });
        await harness.SyncStates.DeleteAsync(reset.Id);
        await harness.Storage.Messages.DeleteByUidsAsync(reset.Id, [1, 2, 3, 4, 5, 6, 7, 8]);
        for (uint uid = 1; uid <= 3; uid++)
        {
            await harness.AddMailAsync(reset, uid, MailKind.CertainSpam, $"<reload_{uid}@example.test>");
        }

        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(old, await harness.MarkerAsync(harness.Inbox));

        for (uint uid = 4; uid <= 10; uid++)
        {
            await harness.AddMailAsync(reset, uid, MailKind.CertainSpam, $"<reload_{uid}@example.test>");
        }

        await harness.MarkSyncedAsync(reset);
        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(0, report.Classified);
        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(new ClassificationMarker(reset.Id, newValidity, 10), await harness.MarkerAsync(harness.Inbox));
    }

    // ---- cases 37, 38: key with Received (G2) -----------------------------------------------------------------------------

    // Case 37
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Campaign_with_a_reused_message_id_and_different_received_is_classified_mail_by_mail()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        for (uint uid = 11; uid <= 13; uid++)
        {
            await harness.AddMailAsync(
                harness.Inbox, uid, MailKind.LikelySpam, "<campaign@example.test>", received: $"from mx{uid}.example.test by host.example.test id {uid}");
        }

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(3, report.Classified);
        Assert.Equal(0, report.SkippedKnown);
        Assert.Equal(3L, await harness.LogCountAsync());
        MailCommand command = CommandAssert.SingleMoveTo(await harness.CommandsAsync(), harness.Junk!);
        Assert.Equal(new uint[] { 11, 12, 13 }, command.Uids);
    }

    // Case 38
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mail_moved_back_by_hand_with_the_same_received_is_skipped()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        const string received = "from mx1.example.test by host.example.test id 1";
        await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit, "<same@example.test>", received: received);
        await harness.RunAsync(ClassificationHarness.Act);
        Assert.Single(await harness.CommandsAsync());
        await harness.AddMailAsync(harness.Inbox, 20, MailKind.Legit, "<same@example.test>", received: received);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1, report.SkippedKnown);
        Assert.Equal(0, report.Classified);
        Assert.Single(await harness.CommandsAsync());
        Assert.Equal(1L, await harness.LogCountAsync());
        Assert.Equal(20u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
    }

    // Case 36 / 38: whitespace of the Received header does not change the key
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Received_with_different_whitespace_gives_the_same_key()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit, "<same@example.test>", received: "from mx1.example.test by host.example.test id 1");
        await harness.RunAsync(ClassificationHarness.Act);
        await harness.AddMailAsync(
            harness.Inbox, 20, MailKind.Legit, "<same@example.test>", received: "  from   mx1.example.test\r\n\tby host.example.test   id 1 ");

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1, report.SkippedKnown);
        Assert.Single(await harness.CommandsAsync());
    }

    // Case 36: mails without Received keep a stable key (the same mail again is known)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mails_without_received_are_still_recognised_by_message_id()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit, "<same@example.test>");
        await harness.RunAsync(ClassificationHarness.Act);
        await harness.AddMailAsync(harness.Inbox, 20, MailKind.Legit, "<same@example.test>");

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1, report.SkippedKnown);
    }

    // Case 39
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Quarantine_mail_with_a_reused_message_id_but_another_received_is_never_deleted()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        const string receivedDecided = "from mx1.example.test by host.example.test id 1";
        await harness.AddMailAsync(harness.Inbox, 11, MailKind.CertainSpam, "<reused@example.test>", received: receivedDecided);
        await harness.RunAsync(ClassificationHarness.Act);
        harness.Clock.Now += TimeSpan.FromDays(40);
        // Same Message-ID, different delivery (attacker reuses the id of a mail the app decided): must not match the entry.
        MailMessage foreign = await harness.AddRawAsync(
            harness.Quarantine!, 1, ClassificationTestEml.For(MailKind.CertainSpam, "from mx9.example.test by host.example.test id 9"),
            "<reused@example.test>", AncientDate);
        // Positive control: the very delivery the app decided on (as after a sync of the moved mail) is deleted.
        MailMessage moved = await harness.AddRawAsync(
            harness.Quarantine!, 2, ClassificationTestEml.For(MailKind.CertainSpam, receivedDecided), "<reused@example.test>", AncientDate);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithRetention(30));

        IReadOnlyList<MailCommand> deletes = (await harness.CommandsAsync()).Where(command => command.Kind == MailCommandKind.DeletePermanently).ToList();
        MailCommand delete = Assert.Single(deletes);
        Assert.Equal(new[] { moved.Uid }, delete.Uids);
        Assert.DoesNotContain(foreign.Uid, delete.Uids);
        Assert.Equal(1, report.RetentionDeletesEnqueued);
    }

    // ---- case 40 on the pass level ----------------------------------------------------------------------------------------

    // Case 40
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Forged_dmarc_pass_in_an_encoded_word_does_not_rescue_a_junk_mail()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Junk!);
        string forged = "mx.test; spf=pass smtp.mailfrom=bounce =?utf-8?B?"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(";dmarc=pass header.from=paypal.test"))
            + "?= ; dmarc=fail header.from=paypal.test";
        MailMessage attack = await harness.AddRawAsync(
            harness.Junk!, 21,
            ClassificationTestEml.Build("Sender A <" + ClassificationTestEml.ContactAddress + ">", [forged], "Your paypal statement is ready."),
            "<attack@example.test>");
        MailMessage genuine = await harness.AddMailAsync(harness.Junk!, 22, MailKind.Trusted);

        await harness.RunAsync(ClassificationHarness.Act);

        ClassificationLogEntry attackEntry = await harness.RequireEntryAsync(attack);
        Assert.NotEqual(SpamTier.TrustedContact, attackEntry.Tier);
        Assert.NotEqual(AuthStatus.Authenticated, attackEntry.AuthStatus);
        Assert.Equal(ClassificationAction.None, attackEntry.PlannedAction);
        Assert.Equal(SpamTier.TrustedContact, (await harness.RequireEntryAsync(genuine)).Tier);
        MailCommand command = Assert.Single(await harness.CommandsAsync());
        Assert.Equal(new uint[] { 22 }, command.Uids);
        Assert.Equal(harness.Inbox.Id, command.TargetFolderId);
    }

    // ---- case 42: readable mail without one usable sender (G7) ----------------------------------------------------------------

    public static TheoryData<string> SenderlessFromValues() => new()
    {
        "Sender A <sender_a@example.test>, Sender B <sender_b@example.test>",
        "undisclosed-recipients:;",
    };

    private static byte[] Senderless(string from) => ClassificationTestEml.Build(from, [], "Body of a mail without a usable sender.");

    // Case 42
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(SenderlessFromValues))]
    public async Task Senderless_inbox_mail_goes_to_the_screener_in_act_mode(string from)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage mail = await harness.AddRawAsync(harness.Inbox, 11, Senderless(from), "<senderless@example.test>");

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        ClassificationLogEntry entry = await harness.RequireEntryAsync(mail);
        Assert.Equal(ClassificationOutcome.Enqueued, entry.Outcome);
        Assert.Equal(ClassificationAction.MoveToScreener, entry.PlannedAction);
        Assert.Null(entry.Tier);
        Assert.Equal(0, entry.Score);
        Assert.Equal(NoSenderText, entry.Override);
        Assert.Equal(new uint[] { 11 }, CommandAssert.SingleMoveTo(await harness.CommandsAsync(), harness.Screener!).Uids);
        Assert.Equal(0, report.Classified);
        Assert.Equal(0, report.ReadFailed);
        Assert.All(Enum.GetValues<SpamTier>(), tier => Assert.Equal(0, report.TierCounts.TryGetValue(tier, out int count) ? count : 0));
    }

    // Case 42, control: the same mail in record-only mode only records
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(SenderlessFromValues))]
    public async Task Senderless_inbox_mail_is_only_recorded_in_record_only_mode(string from)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage mail = await harness.AddRawAsync(harness.Inbox, 11, Senderless(from), "<senderless@example.test>");

        await harness.RunAsync(ClassificationHarness.RecordOnly);

        ClassificationLogEntry entry = await harness.RequireEntryAsync(mail);
        Assert.Equal(ClassificationOutcome.Recorded, entry.Outcome);
        Assert.Equal(ClassificationAction.MoveToScreener, entry.PlannedAction);
        Assert.Null(entry.Tier);
        Assert.Equal(NoSenderText, entry.Override);
        Assert.Empty(await harness.CommandsAsync());
    }

    // Case 42
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(SenderlessFromValues))]
    public async Task Senderless_junk_mail_stays_in_junk(string from)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Junk!);
        MailMessage mail = await harness.AddRawAsync(harness.Junk!, 5, Senderless(from), "<senderless_junk@example.test>");

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        ClassificationLogEntry entry = await harness.RequireEntryAsync(mail);
        Assert.Equal(ClassificationOutcome.NoAction, entry.Outcome);
        Assert.Equal(ClassificationAction.None, entry.PlannedAction);
        Assert.Null(entry.Tier);
        Assert.Equal(NoSenderText, entry.Override);
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(0, report.Classified);
    }

    // Case 42: no Screener on the server -> TargetMissing
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Senderless_inbox_mail_without_a_screener_gives_target_missing()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withScreener: false);
        harness.Provisioner.CreateFolders = false;
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage mail = await harness.AddRawAsync(harness.Inbox, 11, Senderless("undisclosed-recipients:;"), "<senderless@example.test>");

        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(ClassificationOutcome.TargetMissing, (await harness.RequireEntryAsync(mail)).Outcome);
        Assert.Empty(await harness.CommandsAsync());
    }

    // Case 42: bytes that are no message stay ReadFailed and unmoved (final)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unparsable_bytes_stay_read_failed_while_senderless_mail_is_moved()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage garbage = await harness.AddRawAsync(harness.Inbox, 11, SyncTestEml.Garbage(), "<garbage@example.test>");
        MailMessage senderless = await harness.AddRawAsync(harness.Inbox, 12, Senderless("undisclosed-recipients:;"), "<senderless@example.test>");

        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(ClassificationOutcome.ReadFailed, (await harness.RequireEntryAsync(garbage)).Outcome);
        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(senderless)).Outcome);
        Assert.Equal(new uint[] { 12 }, Assert.Single(await harness.CommandsAsync()).Uids);
    }

    // ---- case 43: retention does not repeat (G6) -----------------------------------------------------------------------------------

    private static async Task<MailMessage> SeedDueAsync(ClassificationHarness harness, uint uid)
    {
        MailMessage message = await harness.AddRawAsync(harness.Quarantine!, uid, null, $"<due_{uid}@example.test>", AncientDate);
        await harness.SeedEntryAsync(message, harness.Clock.GetUtcNow() - TimeSpan.FromDays(100));
        return message;
    }

    // Case 43
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Due_mail_with_a_pending_delete_is_not_enqueued_again()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedDueAsync(harness, 1);

        ClassificationPassReport first = await harness.RunAsync(ClassificationHarness.ActWithRetention(30));
        ClassificationPassReport second = await harness.RunAsync(ClassificationHarness.ActWithRetention(30));

        Assert.Equal(1, first.RetentionDeletesEnqueued);
        Assert.Equal(0, second.RetentionDeletesEnqueued);
        MailCommand only = Assert.Single(await harness.CommandsAsync());
        Assert.Equal(MailCommandKind.DeletePermanently, only.Kind);
        Assert.Equal(new uint[] { 1 }, only.Uids);
    }

    // Case 43: only the new mail gets a command
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Only_the_newly_due_mail_is_enqueued_when_another_delete_is_pending()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedDueAsync(harness, 1);
        await harness.RunAsync(ClassificationHarness.ActWithRetention(30));
        await SeedDueAsync(harness, 2);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithRetention(30));

        Assert.Equal(1, report.RetentionDeletesEnqueued);
        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(2, commands.Count);
        Assert.Equal(new[] { new uint[] { 1 }, new uint[] { 2 } }, commands.Select(command => command.Uids.ToArray()).ToArray());
    }

    // Case 43: a failed command still occupies its UIDs
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Due_mail_with_a_failed_delete_is_not_enqueued_again()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedDueAsync(harness, 1);
        await harness.RunAsync(ClassificationHarness.ActWithRetention(30));
        var failedView = new FailedStatusQueue(harness.Queue);
        Assert.All(await failedView.ListAsync(harness.Account.Id), command => Assert.Equal(MailCommandStatus.Failed, command.Status));

        ClassificationPassReport report = await harness.CreatePass(ClassificationHarness.ActWithRetention(30), queue: failedView)
            .RunAsync(harness.Account.Id);

        Assert.Equal(0, report.RetentionDeletesEnqueued);
        Assert.Single(await harness.CommandsAsync());
    }

    // ---- case 44: rejected deletes are counted (G8) ---------------------------------------------------------------------------------

    // Case 44
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Rejected_delete_is_counted_as_rejected_not_as_enqueued_and_does_not_throw(Type exceptionType)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedDueAsync(harness, 1);
        await SeedDueAsync(harness, 2);
        var rejecting = new RejectingMoveQueue(harness.Queue)
        {
            RejectDeletes = true,
            RejectWith = (Exception)Activator.CreateInstance(exceptionType, RejectingMoveQueue.RejectionMarker)!,
        };

        ClassificationPassReport report = await harness.CreatePass(ClassificationHarness.ActWithRetention(30), queue: rejecting)
            .RunAsync(harness.Account.Id);

        Assert.Equal(2, report.RetentionDeletesRejected);
        Assert.Equal(0, report.RetentionDeletesEnqueued);
        Assert.Empty(await harness.CommandsAsync());
        Assert.DoesNotContain(RejectingMoveQueue.RejectionMarker, report.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RejectingMoveQueue.RejectionMarker, await harness.DumpClassificationTablesAsync(), StringComparison.Ordinal);
    }

    // Case 44, positive control: the same data without rejection is enqueued and nothing is rejected
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Accepted_deletes_are_counted_as_enqueued_and_not_as_rejected()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await SeedDueAsync(harness, 1);
        await SeedDueAsync(harness, 2);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithRetention(30));

        Assert.Equal(2, report.RetentionDeletesEnqueued);
        Assert.Equal(0, report.RetentionDeletesRejected);
        Assert.Equal(new uint[] { 1, 2 }, Assert.Single(await harness.CommandsAsync()).Uids);
    }
}
