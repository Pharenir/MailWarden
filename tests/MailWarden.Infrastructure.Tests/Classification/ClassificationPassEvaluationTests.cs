using MailWarden.Core.Classification;
using MailWarden.Core.Commands;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>
/// AP-07c fix.4 J1-J3 / cases 52-54: an exception while evaluating a readable mail never moves it (no mass move to
/// Junk), is recorded without exception text, and the mail is not decided again once its key is known.
/// </summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationPassEvaluationTests
{
    private static readonly IReadOnlyList<ISpamSignal> ThrowingSignals = [new ThrowingSignal()];

    private static async Task<(MailMessage Inbox, MailMessage Junk, MailMessage Contact)> AddMailsAsync(ClassificationHarness harness)
    {
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SeedMarkerAsync(harness.Junk!);
        MailMessage inbox = await harness.AddMailAsync(harness.Inbox, 11, MailKind.CertainSpam);
        MailMessage contact = await harness.AddMailAsync(harness.Inbox, 12, MailKind.Trusted);
        MailMessage junk = await harness.AddMailAsync(harness.Junk!, 21, MailKind.Trusted);
        return (inbox, junk, contact);
    }

    // Case 52
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Throwing_signal_gives_evaluation_failed_and_moves_nothing()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        (MailMessage inbox, MailMessage junk, MailMessage contact) = await AddMailsAsync(harness);

        ClassificationPassReport report = await harness.CreatePass(ClassificationHarness.Act, signals: ThrowingSignals)
            .RunAsync(harness.Account.Id);

        Assert.Empty(await harness.CommandsAsync());
        foreach (MailMessage message in new[] { inbox, junk, contact })
        {
            ClassificationLogEntry entry = await harness.RequireEntryAsync(message);
            Assert.Equal(ClassificationOutcome.EvaluationFailed, entry.Outcome);
            Assert.Null(entry.Tier);
            Assert.Equal(ClassificationAction.None, entry.PlannedAction);
            Assert.Equal(message.Uid, entry.SourceUid);
        }

        Assert.Equal(3, report.EvaluationFailed);
        Assert.Equal(0, report.Classified);
        Assert.Equal(0, report.ReadFailed);
        Assert.Equal(0, report.MovesEnqueued);
        Assert.Equal(12u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
        Assert.Equal(21u, (await harness.MarkerAsync(harness.Junk!))!.ClassifiedUpToUid);
        Assert.Equal(3, (await harness.Storage.Messages.GetUidFlagsAsync(harness.Inbox.Id)).Count + (await harness.Storage.Messages.GetUidFlagsAsync(harness.Junk!.Id)).Count);
    }

    // Case 52: the exception text is nowhere
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Exception_text_of_the_signal_is_in_no_entry_and_not_in_the_report()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await AddMailsAsync(harness);

        ClassificationPassReport report = await harness.CreatePass(ClassificationHarness.Act, signals: ThrowingSignals)
            .RunAsync(harness.Account.Id);

        string stored = await harness.DumpClassificationTablesAsync();
        Assert.DoesNotContain(ThrowingSignal.FailureMarker, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(ThrowingSignal.FailureMarker, report.ToString(), StringComparison.Ordinal);
    }

    // Case 52: the same in record-only mode records the failure and nothing else
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Throwing_signal_in_record_only_mode_is_recorded_as_evaluation_failed()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        (MailMessage inbox, _, _) = await AddMailsAsync(harness);

        await harness.CreatePass(ClassificationHarness.RecordOnly, signals: ThrowingSignals).RunAsync(harness.Account.Id);

        ClassificationLogEntry entry = await harness.RequireEntryAsync(inbox);
        Assert.Equal(ClassificationOutcome.EvaluationFailed, entry.Outcome);
        Assert.Equal(ClassificationMode.RecordOnly, entry.EffectiveMode);
        Assert.Empty(await harness.CommandsAsync());
    }

    // Case 52, H1 stays: a readable mail without a usable sender never reaches the signals and still goes to Junk
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Senderless_mail_still_goes_to_junk_while_a_signal_throws()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage senderless = await harness.AddRawAsync(
            harness.Inbox, 11, ClassificationTestEml.Build("undisclosed-recipients:;", [], "Body."), "<senderless@example.test>");
        MailMessage normal = await harness.AddMailAsync(harness.Inbox, 12, MailKind.Legit);

        ClassificationPassReport report = await harness.CreatePass(ClassificationHarness.Act, signals: ThrowingSignals)
            .RunAsync(harness.Account.Id);

        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(senderless)).Outcome);
        Assert.Equal(ClassificationOutcome.EvaluationFailed, (await harness.RequireEntryAsync(normal)).Outcome);
        Assert.Equal(new uint[] { 11 }, Assert.Single(await harness.CommandsAsync()).Uids);
        Assert.Equal(1, report.EvaluationFailed);
    }

    // Case 53
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Same_mails_again_after_the_signal_is_fixed_are_known_and_stay()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        (MailMessage inbox, _, _) = await AddMailsAsync(harness);
        await harness.CreatePass(ClassificationHarness.Act, signals: ThrowingSignals).RunAsync(harness.Account.Id);
        // The same mails come back as new copies (same Message-ID, same Received) with new UIDs.
        await harness.AddMailAsync(harness.Inbox, 31, MailKind.CertainSpam, $"<CertainSpam_{harness.Inbox.Id}_11@example.test>");

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1, report.SkippedKnown);
        Assert.Equal(0, report.Classified);
        Assert.Equal(0, report.EvaluationFailed);
        Assert.Empty(await harness.CommandsAsync());
        Assert.Equal(ClassificationOutcome.EvaluationFailed, (await harness.RequireEntryAsync(inbox)).Outcome);
        Assert.Equal(31u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
    }

    // Case 54, positive control: the same setup without the throwing signal classifies and moves
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Same_setup_without_the_throwing_signal_classifies_and_moves()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        (MailMessage inbox, MailMessage junk, MailMessage contact) = await AddMailsAsync(harness);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(3, report.Classified);
        Assert.Equal(0, report.EvaluationFailed);
        Assert.Equal(SpamTier.CertainSpam, (await harness.RequireEntryAsync(inbox)).Tier);
        Assert.Equal(SpamTier.TrustedContact, (await harness.RequireEntryAsync(contact)).Tier);
        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(junk)).Outcome);
        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(new uint[] { 11 }, CommandAssert.SingleMoveTo(commands, harness.Quarantine!).Uids);
        Assert.Equal(new uint[] { 21 }, CommandAssert.SingleMoveTo(commands, harness.Inbox).Uids);
    }

    // J3: an explicit list of default signals behaves like null
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Explicit_default_signals_behave_like_the_default()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        (MailMessage inbox, _, _) = await AddMailsAsync(harness);

        await harness.CreatePass(ClassificationHarness.Act, signals: SpamClassifier.CreateDefaultSignals()).RunAsync(harness.Account.Id);

        Assert.Equal(SpamTier.CertainSpam, (await harness.RequireEntryAsync(inbox)).Tier);
    }
}
