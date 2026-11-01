using MailWarden.Core.Classification;
using MailWarden.Core.Commands;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-07d D2 / cases 8-11: the move brake of <see cref="ClassificationPass"/> with real SQLite and queue.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationPassBrakeTests
{
    private static readonly DateTimeOffset VeryOldDate = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Adds <paramref name="count"/> certain-spam mails (planned move: Quarantine) with UIDs 11, 12, ...</summary>
    private static async Task<List<MailMessage>> AddCertainSpamAsync(ClassificationHarness harness, MailFolder folder, int count, uint firstUid = 11)
    {
        var added = new List<MailMessage>();
        for (uint offset = 0; offset < count; offset++)
        {
            added.Add(await harness.AddMailAsync(folder, firstUid + offset, MailKind.CertainSpam));
        }

        return added;
    }

    // Case 8 (positive control): exactly the limit still moves.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Exactly_the_limit_of_planned_moves_is_enqueued(int limit)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        List<MailMessage> mails = await AddCertainSpamAsync(harness, harness.Inbox, limit);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithLimit(limit));

        MailCommand command = CommandAssert.SingleMoveTo(await harness.CommandsAsync(), harness.Quarantine!);
        Assert.Equal(mails.Select(mail => mail.Uid).Order().ToArray(), command.Uids.Order().ToArray());
        Assert.Equal(limit, report.MovesEnqueued);
        Assert.Equal(0, report.BrakeHeld);
        foreach (MailMessage mail in mails)
        {
            Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(mail)).Outcome);
        }
    }

    // Case 8: one more than the limit holds all of them back.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(1)]
    [InlineData(3)]
    [Trait("Category", "smoke")]
    public async Task One_more_than_the_limit_enqueues_none_and_records_all_as_held(int limit)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        List<MailMessage> mails = await AddCertainSpamAsync(harness, harness.Inbox, limit + 1);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithLimit(limit));

        Assert.Empty(await harness.Queue.ListAsync(harness.Account.Id));
        Assert.Equal(limit + 1, report.BrakeHeld);
        Assert.Equal(0, report.MovesEnqueued);
        foreach (MailMessage mail in mails)
        {
            ClassificationLogEntry entry = await harness.RequireEntryAsync(mail);
            Assert.Equal(ClassificationOutcome.BrakeHeld, entry.Outcome);
            Assert.Equal(ClassificationAction.MoveToQuarantine, entry.PlannedAction);
            Assert.Equal(SpamTier.CertainSpam, entry.Tier);
            Assert.Equal(ClassificationMode.Act, entry.EffectiveMode);
        }

        Assert.Equal(mails.Max(mail => mail.Uid), (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
        Assert.Equal(limit + 1L, await harness.LogCountAsync());
    }

    // Case 8 / D2: with the maximum limit of 1000, 1001 planned moves are held as a whole (replaces the old split into 2 commands).
    [Fact(Timeout = 120_000)]
    public async Task More_than_the_maximum_limit_is_held_as_a_whole()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await AddCertainSpamAsync(harness, harness.Inbox, 1001);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithLimit(1000));

        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(1001, report.BrakeHeld);
        Assert.Equal(0, report.MovesEnqueued);
    }

    // Case 8: the limit counts planned moves of the folder, whatever their target (here Screener and Quarantine).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Moves_to_different_targets_of_one_folder_count_together()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit);
        await harness.AddMailAsync(harness.Inbox, 12, MailKind.Legit);
        await harness.AddMailAsync(harness.Inbox, 13, MailKind.CertainSpam);
        await harness.AddMailAsync(harness.Inbox, 14, MailKind.CertainSpam);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithLimit(3));

        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(4, report.BrakeHeld);
    }

    // Case 8 control for the sum above: three moves to two targets are exactly the limit.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Moves_to_different_targets_at_the_limit_all_go_through()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit);
        await harness.AddMailAsync(harness.Inbox, 12, MailKind.CertainSpam);
        await harness.AddMailAsync(harness.Inbox, 13, MailKind.CertainSpam);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithLimit(3));

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(new uint[] { 11 }, CommandAssert.SingleMoveTo(commands, harness.Screener!).Uids);
        Assert.Equal(new uint[] { 12, 13 }, CommandAssert.SingleMoveTo(commands, harness.Quarantine!).Uids.Order().ToArray());
        Assert.Equal(3, report.MovesEnqueued);
        Assert.Equal(0, report.BrakeHeld);
    }

    // Case 8: mails that plan no move do not count against the limit.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Mails_without_a_planned_move_do_not_count()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 11, MailKind.Trusted);
        await harness.AddMailAsync(harness.Inbox, 12, MailKind.Trusted);
        await harness.AddMailAsync(harness.Inbox, 13, MailKind.Trusted);
        await harness.AddMailAsync(harness.Inbox, 14, MailKind.CertainSpam);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithLimit(1));

        Assert.Equal(new uint[] { 14 }, CommandAssert.SingleMoveTo(await harness.CommandsAsync(), harness.Quarantine!).Uids);
        Assert.Equal(1, report.MovesEnqueued);
        Assert.Equal(0, report.BrakeHeld);
    }

    // Case 9: the limit is per folder. Inbox below the limit moves, Junk above it is held.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Inbox_within_the_limit_moves_while_junk_above_it_is_held()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SeedMarkerAsync(harness.Junk!);
        await AddCertainSpamAsync(harness, harness.Inbox, 2);
        var rescued = new List<MailMessage>();
        for (uint uid = 21; uid <= 24; uid++)
        {
            rescued.Add(await harness.AddMailAsync(harness.Junk!, uid, MailKind.Trusted));
        }

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithLimit(3));

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        MailCommand inboxMove = Assert.Single(commands);
        Assert.Equal(harness.Inbox.Id, inboxMove.FolderId);
        Assert.Equal(harness.Quarantine!.Id, inboxMove.TargetFolderId);
        Assert.Equal(2, inboxMove.Uids.Count);
        Assert.Equal(2, report.MovesEnqueued);
        Assert.Equal(4, report.BrakeHeld);
        foreach (MailMessage mail in rescued)
        {
            ClassificationLogEntry entry = await harness.RequireEntryAsync(mail);
            Assert.Equal(ClassificationOutcome.BrakeHeld, entry.Outcome);
            Assert.Equal(ClassificationAction.MoveToInbox, entry.PlannedAction);
        }

        Assert.Equal(24u, (await harness.MarkerAsync(harness.Junk!))!.ClassifiedUpToUid);
    }

    // Case 9, reverse: Inbox above the limit is held while Junk within it moves.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Inbox_above_the_limit_is_held_while_junk_within_it_moves()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SeedMarkerAsync(harness.Junk!);
        await AddCertainSpamAsync(harness, harness.Inbox, 4);
        await harness.AddMailAsync(harness.Junk!, 21, MailKind.Trusted);
        await harness.AddMailAsync(harness.Junk!, 22, MailKind.Trusted);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithLimit(3));

        MailCommand rescue = Assert.Single(await harness.CommandsAsync());
        Assert.Equal(harness.Junk!.Id, rescue.FolderId);
        Assert.Equal(harness.Inbox.Id, rescue.TargetFolderId);
        Assert.Equal(2, report.MovesEnqueued);
        Assert.Equal(4, report.BrakeHeld);
    }

    // Case 10
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task Record_only_with_200_mails_holds_nothing_and_enqueues_nothing()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        List<MailMessage> mails = await AddCertainSpamAsync(harness, harness.Inbox, 200);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.RecordOnly);

        Assert.Equal(0, report.BrakeHeld);
        Assert.Equal(0, report.MovesEnqueued);
        Assert.Equal(200, report.Classified);
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(200L, await harness.LogCountAsync());
        ClassificationLogEntry sample = await harness.RequireEntryAsync(mails[0]);
        Assert.Equal(ClassificationOutcome.Recorded, sample.Outcome);
    }

    // Case 10: Act that rests (empty address book) is effectively RecordOnly, so the brake does not apply either.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Suspended_act_holds_nothing()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withContact: false);
        await harness.SeedMarkerAsync(harness.Inbox);
        List<MailMessage> mails = await AddCertainSpamAsync(harness, harness.Inbox, 5);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithLimit(1));

        Assert.True(report.ActSuspendedNoContacts);
        Assert.Equal(0, report.BrakeHeld);
        Assert.Empty(await harness.CommandsAsync());
        foreach (MailMessage mail in mails)
        {
            Assert.Equal(ClassificationOutcome.Recorded, (await harness.RequireEntryAsync(mail)).Outcome);
        }
    }

    // Case 11: after a hold the same mail again is known; nothing is decided or moved on the next pass.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Same_mail_again_after_a_hold_is_skipped_as_known()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage held = await harness.AddMailAsync(harness.Inbox, 11, MailKind.CertainSpam, "<held_mail@example.test>");
        await harness.AddMailAsync(harness.Inbox, 12, MailKind.CertainSpam);
        ClassificationPassReport first = await harness.RunAsync(ClassificationHarness.ActWithLimit(1));
        Assert.Equal(2, first.BrakeHeld);

        await harness.AddMailAsync(harness.Inbox, 20, MailKind.CertainSpam, "<held_mail@example.test>");
        ClassificationPassReport second = await harness.RunAsync(ClassificationHarness.ActWithLimit(1));

        Assert.Equal(1, second.SkippedKnown);
        Assert.Equal(0, second.Classified);
        Assert.Equal(0, second.BrakeHeld);
        Assert.Equal(0, second.MovesEnqueued);
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(ClassificationOutcome.BrakeHeld, (await harness.RequireEntryAsync(held)).Outcome);
        Assert.Equal(2L, await harness.LogCountAsync());
    }

    // Case 11: a pass without new mail after a hold does nothing, and a higher limit does not release old holds.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Held_mails_are_not_decided_again_even_with_a_higher_limit()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await AddCertainSpamAsync(harness, harness.Inbox, 4);
        await harness.RunAsync(ClassificationHarness.ActWithLimit(3));

        ClassificationPassReport second = await harness.RunAsync(ClassificationHarness.ActWithLimit(100));

        Assert.Equal(0, second.Classified);
        Assert.Equal(0, second.MovesEnqueued);
        Assert.Equal(0, second.BrakeHeld);
        Assert.Empty(await harness.CommandsAsync());
    }

    // D2: retention deletes are not part of the brake.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Retention_deletes_are_not_held_by_the_brake()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        for (uint uid = 1; uid <= 3; uid++)
        {
            MailMessage message = await harness.AddRawAsync(
                harness.Quarantine!, uid, ClassificationTestEml.For(MailKind.CertainSpam), $"<quarantine_{uid}@example.test>", VeryOldDate);
            await harness.SeedEntryAsync(message, harness.Clock.GetUtcNow() - TimeSpan.FromDays(400));
        }

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithRetention(30) with { MaxMovesPerPass = 1 });

        Assert.Equal(3, report.RetentionDeletesEnqueued);
        Assert.Equal(0, report.BrakeHeld);
        MailCommand command = Assert.Single(await harness.CommandsAsync());
        Assert.Equal(MailCommandKind.DeletePermanently, command.Kind);
        Assert.Equal(3, command.Uids.Count);
    }
}
