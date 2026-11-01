using System.Text;
using MailWarden.Core.Classification;
using MailWarden.Core.Commands;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-07c cases 19-22, 29, 30: one decision per message, UIDVALIDITY, unreadable mail, cancellation, report.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationPassDecisionTests
{
    // Unknown account
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unknown_account_is_an_argument_exception_and_stores_nothing()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        ClassificationPass pass = harness.CreatePass(ClassificationHarness.Act);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => pass.RunAsync(Guid.NewGuid()));

        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Equal(0, harness.Provisioner.Calls);
    }

    // ---- case 19 / 20: one decision per message --------------------------------------------------------------

    // Case 19
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Known_key_arriving_again_is_skipped_without_a_second_action()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage first = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit, "<same_mail@example.test>");
        await harness.RunAsync(ClassificationHarness.Act);
        IReadOnlyList<MailCommand> commandsAfterFirstPass = await harness.CommandsAsync();
        Assert.Single(commandsAfterFirstPass);
        ClassificationLogEntry original = await harness.RequireEntryAsync(first);

        // The mail comes back as a new copy (moved back by hand on another device, or a Gmail label copy).
        MailMessage comeback = await harness.AddMailAsync(harness.Inbox, 20, MailKind.Legit, "<same_mail@example.test>");
        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1, report.SkippedKnown);
        Assert.Equal(0, report.Classified);
        Assert.Equal(0, report.MovesEnqueued);
        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        MailCommand only = Assert.Single(commands);
        Assert.Equal(new uint[] { 11 }, only.Uids);
        Assert.Equal(1L, await harness.LogCountAsync());
        ClassificationLogEntry stillOriginal = await harness.RequireEntryAsync(comeback);
        Assert.Equal(original.SourceUid, stillOriginal.SourceUid);
        Assert.Equal(original.Outcome, stillOriginal.Outcome);
        Assert.Equal(original.ClassifiedAt, stillOriginal.ClassifiedAt);
        Assert.Equal(20u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
    }

    // Case 19, decision 4: a mail of a tier that would be moved stays where Dennis put it
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Known_key_that_was_only_recorded_is_not_acted_on_later_in_act_mode()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 11, MailKind.CertainSpam, "<recorded@example.test>");
        await harness.RunAsync(ClassificationHarness.RecordOnly);
        await harness.AddMailAsync(harness.Inbox, 12, MailKind.CertainSpam, "<recorded@example.test>");

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1, report.SkippedKnown);
        Assert.Empty(await harness.CommandsAsync());
    }

    // Case 20
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Same_message_id_twice_among_new_mails_is_decided_once()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage firstCopy = await harness.AddMailAsync(harness.Inbox, 11, MailKind.LikelySpam, "<twin@example.test>");
        await harness.AddMailAsync(harness.Inbox, 12, MailKind.LikelySpam, "<twin@example.test>");

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1L, await harness.LogCountAsync());
        Assert.Equal(11u, (await harness.RequireEntryAsync(firstCopy)).SourceUid);
        Assert.Equal(1, report.Classified);
        Assert.Equal(1, report.SkippedKnown);
        MailCommand command = Assert.Single(await harness.CommandsAsync());
        Assert.Equal(harness.Junk!.Id, command.TargetFolderId);
        Assert.Equal(new uint[] { 11 }, command.Uids);
        Assert.Equal(12u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
    }

    // Case 20: a copy in Junk (Gmail label copy) is covered by the Inbox decision of the same pass
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Copy_in_junk_with_the_same_message_id_is_not_decided_again()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SeedMarkerAsync(harness.Junk!);
        MailMessage inboxCopy = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Trusted, "<label_copy@example.test>");
        await harness.AddMailAsync(harness.Junk!, 5, MailKind.Trusted, "<label_copy@example.test>");

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1L, await harness.LogCountAsync());
        Assert.Equal(harness.Inbox.Id, (await harness.RequireEntryAsync(inboxCopy)).SourceFolderId);
        Assert.Equal(1, report.SkippedKnown);
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(5u, (await harness.MarkerAsync(harness.Junk!))!.ClassifiedUpToUid);
    }

    // Case 20 / key without Message-ID: the h-key identifies the copy as well
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Copies_without_message_id_share_the_hash_key()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        byte[] eml = ClassificationTestEml.For(MailKind.Legit);
        await harness.AddRawAsync(harness.Inbox, 11, eml, null);
        await harness.AddRawAsync(harness.Inbox, 12, eml, null);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1L, await harness.LogCountAsync());
        Assert.Equal(1, report.SkippedKnown);
        Assert.Equal(new uint[] { 11 }, Assert.Single(await harness.CommandsAsync()).Uids);
    }

    // ---- case 21: UIDVALIDITY ----------------------------------------------------------------------------------

    // Case 21
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Changed_uid_validity_classifies_nothing_and_resets_the_marker()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        var staleMarker = new ClassificationMarker(harness.Inbox.Id, ClassificationHarness.InboxUidValidity + 1, 2);
        await harness.Log.CommitAsync([], staleMarker);
        for (uint uid = 1; uid <= 8; uid++)
        {
            await harness.AddMailAsync(harness.Inbox, uid, MailKind.CertainSpam);
        }

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(new ClassificationMarker(harness.Inbox.Id, ClassificationHarness.InboxUidValidity, 8), await harness.MarkerAsync(harness.Inbox));
        Assert.Equal(0, report.Classified);
    }

    // Case 21, positive control: with the matching validity the same UIDs above the marker are classified
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Matching_uid_validity_classifies_the_uids_above_the_marker_only()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.Log.CommitAsync([], new ClassificationMarker(harness.Inbox.Id, ClassificationHarness.InboxUidValidity, 5));
        for (uint uid = 1; uid <= 8; uid++)
        {
            await harness.AddMailAsync(harness.Inbox, uid, MailKind.CertainSpam);
        }

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(3, report.Classified);
        Assert.Equal(3L, await harness.LogCountAsync());
        Assert.Equal(new uint[] { 6, 7, 8 }, Assert.Single(await harness.CommandsAsync()).Uids);
        Assert.Equal(8u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
    }

    // ---- case 22: unreadable mail --------------------------------------------------------------------------------

    // Case 22
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unreadable_mails_are_recorded_as_read_failed_without_action_and_without_exception_text()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage missingFile = await harness.AddMessageWithMissingFileAsync(harness.Inbox, 11, "<missing_file@example.test>");
        MailMessage noPath = await harness.AddRawAsync(harness.Inbox, 12, null, "<no_path@example.test>");
        MailMessage garbage = await harness.AddRawAsync(harness.Inbox, 13, SyncTestEml.Garbage(), "<garbage@example.test>");
        MailMessage noFrom = await harness.AddRawAsync(
            harness.Inbox, 14, ClassificationTestEml.Build(null, [], "Body without sender."), "<no_from@example.test>");
        MailMessage unreadable = await harness.AddRawAsync(
            harness.Inbox, 15, ClassificationTestEml.For(MailKind.Legit), "<unreadable_io@example.test>");
        MailMessage denied = await harness.AddRawAsync(
            harness.Inbox, 16, ClassificationTestEml.For(MailKind.Legit), "<unreadable_denied@example.test>");
        MailMessage noDomain = await harness.AddRawAsync(
            harness.Inbox, 17, ClassificationTestEml.Build("sender_a", [], "Body."), "<no_domain@example.test>");
        MailMessage legit = await harness.AddMailAsync(harness.Inbox, 18, MailKind.Legit);
        MailMessage certain = await harness.AddMailAsync(harness.Inbox, 19, MailKind.CertainSpam);
        var throwing = new ThrowingEmlStore(harness.Eml)
        {
            OnOpen = path =>
            {
                if (path == harness.Eml.RelativePathFor(harness.Account.Id, harness.Inbox.Id, 15))
                {
                    return new IOException(ThrowingEmlStore.FailureMarker);
                }

                return path == harness.Eml.RelativePathFor(harness.Account.Id, harness.Inbox.Id, 16)
                    ? new UnauthorizedAccessException(ThrowingEmlStore.FailureMarker)
                    : null;
            },
        };

        ClassificationPassReport report = await harness.CreatePass(ClassificationHarness.Act, eml: throwing).RunAsync(harness.Account.Id);

        foreach (MailMessage failed in new[] { missingFile, noPath, garbage, noFrom, unreadable, denied, noDomain })
        {
            ClassificationLogEntry entry = await harness.RequireEntryAsync(failed);
            Assert.Equal(ClassificationOutcome.ReadFailed, entry.Outcome);
            Assert.Null(entry.Tier);
            Assert.Null(entry.AuthStatus);
            Assert.Equal(ClassificationAction.None, entry.PlannedAction);
            Assert.Null(entry.Override);
            Assert.Empty(entry.Signals);
            Assert.Equal(failed.Uid, entry.SourceUid);
        }

        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(legit)).Outcome);
        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(certain)).Outcome);
        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(2, commands.Count);
        Assert.Equal(new uint[] { 18 }, CommandAssert.SingleMoveTo(commands, harness.Screener!).Uids);
        Assert.Equal(new uint[] { 19 }, CommandAssert.SingleMoveTo(commands, harness.Quarantine!).Uids);
        Assert.Equal(7, report.ReadFailed);
        Assert.Equal(2, report.Classified);
        Assert.Equal(19u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
        string stored = await harness.DumpClassificationTablesAsync();
        Assert.DoesNotContain(ThrowingEmlStore.FailureMarker, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(ThrowingEmlStore.FailureMarker, report.ToString(), StringComparison.Ordinal);
    }

    // Case 22: unreadable mail in record-only mode is recorded too, and nothing moves
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unreadable_mail_in_record_only_mode_is_recorded_and_nothing_moves()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage garbage = await harness.AddRawAsync(harness.Inbox, 1, SyncTestEml.Garbage(), "<garbage@example.test>");

        await harness.RunAsync(ClassificationHarness.RecordOnly);

        ClassificationLogEntry entry = await harness.RequireEntryAsync(garbage);
        Assert.Equal(ClassificationOutcome.ReadFailed, entry.Outcome);
        Assert.Equal(ClassificationMode.RecordOnly, entry.EffectiveMode);
        Assert.Empty(await harness.CommandsAsync());
    }

    // Case 22: a mail that failed to read is not tried again
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Read_failed_mail_is_not_retried_by_the_next_pass()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddRawAsync(harness.Inbox, 1, SyncTestEml.Garbage(), "<garbage@example.test>");
        await harness.RunAsync(ClassificationHarness.Act);

        ClassificationPassReport second = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(0, second.ReadFailed);
        Assert.Equal(0, second.Classified);
        Assert.Equal(1L, await harness.LogCountAsync());
    }

    // ---- case 29: cancellation -------------------------------------------------------------------------------------

    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cancellation_between_folders_keeps_the_committed_folder_and_handles_the_other_next_time()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        ClassificationMarker inboxSeed = await harness.SeedMarkerAsync(harness.Inbox);
        ClassificationMarker junkSeed = await harness.SeedMarkerAsync(harness.Junk!);
        MailMessage inboxMail = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit);
        MailMessage junkMail = await harness.AddMailAsync(harness.Junk!, 21, MailKind.Trusted);
        using var cancellation = new CancellationTokenSource();
        string junkPath = harness.Eml.RelativePathFor(harness.Account.Id, harness.Junk!.Id, 21);
        var cancelling = new ThrowingEmlStore(harness.Eml)
        {
            OnOpen = path =>
            {
                if (path != junkPath)
                {
                    return null;
                }

                cancellation.Cancel();
                return new OperationCanceledException(cancellation.Token);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.CreatePass(ClassificationHarness.Act, eml: cancelling).RunAsync(harness.Account.Id, cancellation.Token));

        Assert.Equal(inboxSeed with { ClassifiedUpToUid = 11 }, await harness.MarkerAsync(harness.Inbox));
        Assert.Equal(junkSeed, await harness.MarkerAsync(harness.Junk));
        Assert.NotNull(await harness.EntryAsync(inboxMail));
        Assert.Null(await harness.EntryAsync(junkMail));
        Assert.Equal(1L, await harness.LogCountAsync());
        Assert.Equal(new uint[] { 11 }, CommandAssert.SingleMoveTo(await harness.CommandsAsync(), harness.Screener!).Uids);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1, report.Classified);
        Assert.Equal(0, report.SkippedKnown);
        Assert.Equal(0, report.ReadFailed);
        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(2, commands.Count);
        Assert.Equal(new uint[] { 11 }, CommandAssert.SingleMoveTo(commands, harness.Screener!).Uids);
        Assert.Equal(new uint[] { 21 }, CommandAssert.SingleMoveTo(commands, harness.Inbox).Uids);
        Assert.Equal(21u, (await harness.MarkerAsync(harness.Junk))!.ClassifiedUpToUid);
        Assert.Equal(2L, await harness.LogCountAsync());
    }

    // Case 29: a token that is cancelled before the start stores nothing
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Pre_cancelled_token_throws_and_stores_nothing()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        ClassificationMarker seed = await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.CertainSpam);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.RunAsync(ClassificationHarness.Act, new CancellationToken(canceled: true)));

        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Equal(seed, await harness.MarkerAsync(harness.Inbox));
        Assert.Empty(await harness.CommandsAsync());
    }

    // Case 29: a cancellation while reading a mail is never recorded as a read failure
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cancellation_while_reading_a_mail_is_not_recorded_as_read_failed()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        ClassificationMarker seed = await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.Legit);
        using var cancellation = new CancellationTokenSource();
        var cancelling = new ThrowingEmlStore(harness.Eml)
        {
            OnOpen = _ =>
            {
                cancellation.Cancel();
                return new OperationCanceledException(cancellation.Token);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.CreatePass(ClassificationHarness.Act, eml: cancelling).RunAsync(harness.Account.Id, cancellation.Token));

        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Equal(seed, await harness.MarkerAsync(harness.Inbox));
        Assert.Empty(await harness.CommandsAsync());
    }

    // ---- case 30: report counts ----------------------------------------------------------------------------------------

    // Case 30
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Report_counts_match_the_decisions_of_the_pass()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SeedMarkerAsync(harness.Junk!);
        await harness.AddMailAsync(harness.Inbox, 11, MailKind.Trusted);
        await harness.AddMailAsync(harness.Inbox, 12, MailKind.Legit);
        await harness.AddMailAsync(harness.Inbox, 13, MailKind.Legit);
        await harness.AddMailAsync(harness.Inbox, 14, MailKind.LikelySpam, "<report_spam@example.test>");
        await harness.AddMailAsync(harness.Inbox, 15, MailKind.CertainSpam);
        await harness.AddMessageWithMissingFileAsync(harness.Inbox, 16, "<report_missing@example.test>");
        await harness.AddMailAsync(harness.Inbox, 17, MailKind.LikelySpam, "<report_spam@example.test>");
        await harness.AddMailAsync(harness.Junk!, 21, MailKind.Trusted);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(6, report.Classified);
        Assert.Equal(1, report.SkippedKnown);
        Assert.Equal(1, report.ReadFailed);
        Assert.Equal(0, report.RetentionDeletesEnqueued);
        Assert.False(report.ActSuspendedNoContacts);
        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(4, commands.Count);
        Assert.Equal(5, report.MovesEnqueued);
        Assert.Equal(5, CommandAssert.AllUids(commands).Length);
        Assert.Equal(2, TierCount(report, SpamTier.TrustedContact));
        Assert.Equal(2, TierCount(report, SpamTier.LikelyLegit));
        Assert.Equal(1, TierCount(report, SpamTier.LikelySpam));
        Assert.Equal(1, TierCount(report, SpamTier.CertainSpam));
    }

    // Case 30: a pass without news reports zeros
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Pass_without_new_mail_reports_zeros()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(0, report.Classified);
        Assert.Equal(0, report.SkippedKnown);
        Assert.Equal(0, report.ReadFailed);
        Assert.Equal(0, report.MovesEnqueued);
        Assert.Equal(0, report.RetentionDeletesEnqueued);
        Assert.False(report.ActSuspendedNoContacts);
        Assert.All(Enum.GetValues<SpamTier>(), tier => Assert.Equal(0, TierCount(report, tier)));
    }

    private static int TierCount(ClassificationPassReport report, SpamTier tier) =>
        report.TierCounts.TryGetValue(tier, out int count) ? count : 0;

    // Case 22 / report: the report text carries no mail content
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Report_text_contains_no_subject_or_address()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.Trusted);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        string text = report.ToString();
        Assert.DoesNotContain("sender_a@", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sample_subject", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.UTF8.GetString(ClassificationTestEml.For(MailKind.Trusted)), text, StringComparison.Ordinal);
    }
}
