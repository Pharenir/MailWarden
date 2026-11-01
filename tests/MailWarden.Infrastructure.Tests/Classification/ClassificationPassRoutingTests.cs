using MailWarden.Core.Classification;
using MailWarden.Core.Commands;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-07c cases 13, 14, 17, 18, 23, 24, 25: routing in Act mode, app folders, rejections, command size.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationPassRoutingTests
{
    // Case 13
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Each_tier_from_the_inbox_is_routed_to_its_target_in_act_mode()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage trusted = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Trusted);
        MailMessage legit = await harness.AddMailAsync(harness.Inbox, 12, MailKind.Legit);
        MailMessage spam = await harness.AddMailAsync(harness.Inbox, 13, MailKind.LikelySpam);
        MailMessage certain = await harness.AddMailAsync(harness.Inbox, 14, MailKind.CertainSpam);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(3, commands.Count);
        Assert.All(commands, command =>
        {
            Assert.Equal(MailCommandKind.Move, command.Kind);
            Assert.Equal(harness.Inbox.Id, command.FolderId);
            Assert.Equal(ClassificationHarness.InboxUidValidity, command.FolderUidValidity);
        });
        Assert.Equal(new uint[] { 12 }, CommandAssert.SingleMoveTo(commands, harness.Screener!).Uids);
        Assert.Equal(new uint[] { 13 }, CommandAssert.SingleMoveTo(commands, harness.Junk!).Uids);
        Assert.Equal(new uint[] { 14 }, CommandAssert.SingleMoveTo(commands, harness.Quarantine!).Uids);

        ClassificationLogEntry trustedEntry = await harness.RequireEntryAsync(trusted);
        Assert.Equal(ClassificationOutcome.NoAction, trustedEntry.Outcome);
        Assert.Equal(ClassificationAction.None, trustedEntry.PlannedAction);
        Assert.Equal(SpamTier.TrustedContact, trustedEntry.Tier);
        Assert.False(string.IsNullOrWhiteSpace(trustedEntry.Override));
        Assert.Equal(AuthStatus.Authenticated, trustedEntry.AuthStatus);

        await AssertEnqueuedAsync(harness, legit, ClassificationAction.MoveToScreener, SpamTier.LikelyLegit);
        await AssertEnqueuedAsync(harness, spam, ClassificationAction.MoveToJunk, SpamTier.LikelySpam);
        await AssertEnqueuedAsync(harness, certain, ClassificationAction.MoveToQuarantine, SpamTier.CertainSpam);
        Assert.Equal(14u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
        Assert.Equal(0, harness.Provisioner.Calls);
        Assert.Equal(3, report.MovesEnqueued); // 3 commands of 1 UID each: same count under either reading
        Assert.Equal(4, report.Classified);
        Assert.False(report.ActSuspendedNoContacts);
    }

    private static async Task AssertEnqueuedAsync(
        ClassificationHarness harness, MailMessage message, ClassificationAction action, SpamTier tier)
    {
        ClassificationLogEntry entry = await harness.RequireEntryAsync(message);
        Assert.Equal(ClassificationOutcome.Enqueued, entry.Outcome);
        Assert.Equal(action, entry.PlannedAction);
        Assert.Equal(tier, entry.Tier);
        Assert.Equal(ClassificationMode.Act, entry.EffectiveMode);
        Assert.Equal(harness.Account.Id, entry.AccountId);
        Assert.Equal(message.FolderId, entry.SourceFolderId);
        Assert.Equal(message.Uid, entry.SourceUid);
        Assert.Equal(harness.Clock.GetUtcNow(), entry.ClassifiedAt);
        Assert.NotEmpty(entry.Signals);
        Assert.All(entry.Signals, signal => Assert.False(string.IsNullOrWhiteSpace(signal.Reason)));
        Assert.Equal(entry.Score, entry.Signals.Sum(signal => signal.Points));
    }

    // Case 14
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Trusted_contact_in_junk_is_rescued_to_the_inbox_and_other_tiers_stay()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Junk!);
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage trusted = await harness.AddMailAsync(harness.Junk!, 21, MailKind.Trusted);
        MailMessage legit = await harness.AddMailAsync(harness.Junk!, 22, MailKind.Legit);
        MailMessage spam = await harness.AddMailAsync(harness.Junk!, 23, MailKind.LikelySpam);
        MailMessage certain = await harness.AddMailAsync(harness.Junk!, 24, MailKind.CertainSpam);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        MailCommand command = Assert.Single(await harness.CommandsAsync());
        Assert.Equal(MailCommandKind.Move, command.Kind);
        Assert.Equal(harness.Junk!.Id, command.FolderId);
        Assert.Equal(ClassificationHarness.JunkUidValidity, command.FolderUidValidity);
        Assert.Equal(harness.Inbox.Id, command.TargetFolderId);
        Assert.Equal(new uint[] { 21 }, command.Uids);

        ClassificationLogEntry rescued = await harness.RequireEntryAsync(trusted);
        Assert.Equal(ClassificationOutcome.Enqueued, rescued.Outcome);
        Assert.Equal(ClassificationAction.MoveToInbox, rescued.PlannedAction);
        foreach ((MailMessage message, SpamTier tier) in new[]
        {
            (legit, SpamTier.LikelyLegit), (spam, SpamTier.LikelySpam), (certain, SpamTier.CertainSpam),
        })
        {
            ClassificationLogEntry entry = await harness.RequireEntryAsync(message);
            Assert.Equal(ClassificationOutcome.NoAction, entry.Outcome);
            Assert.Equal(ClassificationAction.None, entry.PlannedAction);
            Assert.Equal(tier, entry.Tier);
        }

        Assert.Equal(24u, (await harness.MarkerAsync(harness.Junk))!.ClassifiedUpToUid);
        Assert.Equal(4, report.Classified);
    }

    // Case 14 / role rule: Inbox is handled before Junk, both in one pass
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Inbox_and_junk_are_handled_in_the_same_pass()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Junk!);
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.CertainSpam);
        await harness.AddMailAsync(harness.Junk!, 1, MailKind.Trusted);

        await harness.RunAsync(ClassificationHarness.Act);

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(2, commands.Count);
        Assert.Equal(new uint[] { 1 }, CommandAssert.SingleMoveTo(commands, harness.Quarantine!).Uids);
        Assert.Equal(new uint[] { 1 }, CommandAssert.SingleMoveTo(commands, harness.Inbox).Uids);
    }

    // Folder rule: only Inbox and Junk are classified
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Other_folders_are_never_classified()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        MailFolder archive = await harness.Storage.AddFolderAsync(harness.Account.Id, FolderRole.Archive, "Archive");
        MailFolder sent = await harness.Storage.AddFolderAsync(harness.Account.Id, FolderRole.Sent, "Sent");
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(archive, 1, MailKind.CertainSpam);
        await harness.AddMailAsync(sent, 1, MailKind.CertainSpam);
        await harness.AddMailAsync(harness.Screener!, 1, MailKind.CertainSpam);
        await harness.AddMailAsync(harness.Quarantine!, 1, MailKind.CertainSpam);

        await harness.RunAsync(ClassificationHarness.Act);
        await harness.AddMailAsync(archive, 2, MailKind.CertainSpam);
        await harness.AddMailAsync(harness.Screener!, 2, MailKind.Legit);
        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Empty(await harness.CommandsAsync());
        foreach (MailFolder folder in new[] { archive, sent, harness.Screener!, harness.Quarantine! })
        {
            Assert.Null(await harness.MarkerAsync(folder));
        }
    }

    // Folders that were never synced (UIDVALIDITY 0) are skipped completely
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folder_with_uid_validity_zero_is_skipped()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withJunk: false);
        MailFolder junk = await harness.Storage.Folders.UpsertAsync(TestData.NewFolder(harness.Account.Id, "Junk", FolderRole.Junk, 0));
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddRawAsync(junk, 1, ClassificationTestEml.For(MailKind.Trusted), "<unsynced_1@example.test>");

        await harness.RunAsync(ClassificationHarness.Act);
        await harness.AddRawAsync(junk, 2, ClassificationTestEml.For(MailKind.Trusted), "<unsynced_2@example.test>");
        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Null(await harness.MarkerAsync(junk));
        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Empty(await harness.CommandsAsync());
    }

    // ---- case 17: app folders ----------------------------------------------------------------------------------

    // Case 17
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Missing_app_folder_is_provisioned_once_before_the_moves(bool withScreener, bool withQuarantine)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(
            withScreener: withScreener, withQuarantine: withQuarantine);
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.Legit);
        await harness.AddMailAsync(harness.Inbox, 2, MailKind.CertainSpam);
        int commandsAtProvisioning = -1;
        harness.Provisioner.OnCall = async () => commandsAtProvisioning = (await harness.CommandsAsync()).Count;

        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1, harness.Provisioner.Calls);
        Assert.Equal(0, commandsAtProvisioning);
        IReadOnlyList<MailFolder> folders = await harness.Storage.Folders.ListAsync(harness.Account.Id);
        MailFolder screener = Assert.Single(folders, folder => folder.Role == FolderRole.Screener);
        MailFolder quarantine = Assert.Single(folders, folder => folder.Role == FolderRole.Quarantine);
        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(new uint[] { 1 }, CommandAssert.SingleMoveTo(commands, screener).Uids);
        Assert.Equal(new uint[] { 2 }, CommandAssert.SingleMoveTo(commands, quarantine).Uids);
    }

    // Case 17
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_pass_with_the_folders_present_does_not_provision_again()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withScreener: false, withQuarantine: false);
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.Legit);
        await harness.RunAsync(ClassificationHarness.Act);
        Assert.Equal(1, harness.Provisioner.Calls);
        await harness.AddMailAsync(harness.Inbox, 2, MailKind.Legit);

        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1, harness.Provisioner.Calls);
        Assert.Equal(2, (await harness.CommandsAsync()).Count);
    }

    // Case 17: nothing missing, nothing to provision
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Pass_with_both_app_folders_present_never_calls_the_provisioner()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.Legit);

        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(0, harness.Provisioner.Calls);
    }

    // Case 17: provisioning receives the account of the pass
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Provisioner_gets_the_account_of_the_pass()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withScreener: false, withQuarantine: false);
        Guid? provisionedAccount = null;
        var recording = new RecordingProvisioner(harness.Provisioner, account => provisionedAccount = account.Id);
        await harness.SeedMarkerAsync(harness.Inbox);
        await harness.AddMailAsync(harness.Inbox, 1, MailKind.Legit);

        await harness.CreatePass(ClassificationHarness.Act, provisioner: recording).RunAsync(harness.Account.Id);

        Assert.Equal(harness.Account.Id, provisionedAccount);
    }

    private sealed class RecordingProvisioner(IMailFolderProvisioner inner, Action<Account> onCall) : IMailFolderProvisioner
    {
        public Task EnsureAppFoldersAsync(Account account, CancellationToken ct = default)
        {
            onCall(account);
            return inner.EnsureAppFoldersAsync(account, ct);
        }
    }

    // ---- case 18: provisioner fails ----------------------------------------------------------------------------

    // Case 18
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Provisioner_failure_propagates_and_leaves_everything_untouched_and_the_next_pass_succeeds()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withScreener: false, withQuarantine: false);
        ClassificationMarker seeded = await harness.SeedMarkerAsync(harness.Inbox);
        await harness.SeedMarkerAsync(harness.Junk!);
        MailMessage legit = await harness.AddMailAsync(harness.Inbox, 1, MailKind.Legit);
        MailMessage certain = await harness.AddMailAsync(harness.Inbox, 2, MailKind.CertainSpam);
        harness.Provisioner.ThrowOnCall = new MailTransportException(MailTransportFailure.Network, ThrowingEmlStore.FailureMarker);

        MailTransportException failure = await Assert.ThrowsAsync<MailTransportException>(
            () => harness.RunAsync(ClassificationHarness.Act));

        Assert.Equal(MailTransportFailure.Network, failure.Failure);
        Assert.Equal(1, harness.Provisioner.Calls);
        Assert.Equal(0L, await harness.LogCountAsync());
        Assert.Equal(seeded, await harness.MarkerAsync(harness.Inbox));
        Assert.Empty(await harness.CommandsAsync());

        harness.Provisioner.ThrowOnCall = null;
        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(2, report.Classified);
        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(legit)).Outcome);
        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(certain)).Outcome);
        Assert.Equal(2, (await harness.CommandsAsync()).Count);
        Assert.Equal(2u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
    }

    // ---- case 23: target folder missing ------------------------------------------------------------------------

    // Case 23
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Missing_junk_folder_gives_target_missing_and_the_other_mails_continue()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withJunk: false);
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage spam = await harness.AddMailAsync(harness.Inbox, 11, MailKind.LikelySpam);
        MailMessage legit = await harness.AddMailAsync(harness.Inbox, 12, MailKind.Legit);
        MailMessage certain = await harness.AddMailAsync(harness.Inbox, 13, MailKind.CertainSpam);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.Act);

        ClassificationLogEntry spamEntry = await harness.RequireEntryAsync(spam);
        Assert.Equal(ClassificationOutcome.TargetMissing, spamEntry.Outcome);
        Assert.Equal(ClassificationAction.MoveToJunk, spamEntry.PlannedAction);
        Assert.Equal(SpamTier.LikelySpam, spamEntry.Tier);
        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(legit)).Outcome);
        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(certain)).Outcome);
        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.Equal(2, commands.Count);
        Assert.DoesNotContain(11u, CommandAssert.AllUids(commands));
        Assert.Equal(13u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
        Assert.Equal(0, harness.Provisioner.Calls);
        Assert.Equal(3, report.Classified);
    }

    // Case 23: the server could not create Screener; the mail stays and the others continue
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Screener_that_is_still_missing_after_provisioning_gives_target_missing()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync(withScreener: false);
        harness.Provisioner.CreateFolders = false;
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage legit = await harness.AddMailAsync(harness.Inbox, 1, MailKind.Legit);
        MailMessage certain = await harness.AddMailAsync(harness.Inbox, 2, MailKind.CertainSpam);

        await harness.RunAsync(ClassificationHarness.Act);

        Assert.Equal(1, harness.Provisioner.Calls);
        Assert.Equal(ClassificationOutcome.TargetMissing, (await harness.RequireEntryAsync(legit)).Outcome);
        Assert.Equal(ClassificationOutcome.Enqueued, (await harness.RequireEntryAsync(certain)).Outcome);
        MailCommand command = Assert.Single(await harness.CommandsAsync());
        Assert.Equal(harness.Quarantine!.Id, command.TargetFolderId);
    }

    // ---- case 24: queue rejects a command ------------------------------------------------------------------------

    // Case 24
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(typeof(ArgumentException), FolderRole.Screener)]
    [InlineData(typeof(InvalidOperationException), FolderRole.Screener)]
    [InlineData(typeof(ArgumentException), FolderRole.Junk)]
    [InlineData(typeof(InvalidOperationException), FolderRole.Quarantine)]
    public async Task Rejected_command_gives_enqueue_failed_for_its_mails_and_the_other_targets_still_enqueue(
        Type exceptionType, FolderRole rejectedRole)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        MailMessage legit = await harness.AddMailAsync(harness.Inbox, 11, MailKind.Legit);
        MailMessage spam = await harness.AddMailAsync(harness.Inbox, 12, MailKind.LikelySpam);
        MailMessage certain = await harness.AddMailAsync(harness.Inbox, 13, MailKind.CertainSpam);
        var rejecting = new RejectingMoveQueue(harness.Queue)
        {
            RejectWith = (Exception)Activator.CreateInstance(exceptionType, RejectingMoveQueue.RejectionMarker)!,
        };
        MailFolder rejected = rejectedRole switch
        {
            FolderRole.Screener => harness.Screener!,
            FolderRole.Junk => harness.Junk!,
            _ => harness.Quarantine!,
        };
        rejecting.RejectedTargets.Add(rejected.Id);

        ClassificationPassReport report = await harness.CreatePass(ClassificationHarness.Act, queue: rejecting).RunAsync(harness.Account.Id);

        (MailMessage Message, MailFolder Target)[] expectations =
        [
            (legit, harness.Screener!), (spam, harness.Junk!), (certain, harness.Quarantine!),
        ];
        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        foreach ((MailMessage message, MailFolder target) in expectations)
        {
            ClassificationLogEntry entry = await harness.RequireEntryAsync(message);
            if (target.Id == rejected.Id)
            {
                Assert.Equal(ClassificationOutcome.EnqueueFailed, entry.Outcome);
                CommandAssert.NoMoveTo(commands, target);
            }
            else
            {
                Assert.Equal(ClassificationOutcome.Enqueued, entry.Outcome);
                Assert.Equal(new[] { message.Uid }, CommandAssert.SingleMoveTo(commands, target).Uids);
            }
        }

        Assert.Equal(2, commands.Count);
        Assert.Equal(13u, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
        Assert.Equal(3, report.Classified);
        string stored = await harness.DumpClassificationTablesAsync();
        Assert.DoesNotContain(RejectingMoveQueue.RejectionMarker, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(RejectingMoveQueue.RejectionMarker, report.ToString(), StringComparison.Ordinal);
    }

    // ---- case 25: command size -------------------------------------------------------------------------------------

    private static async Task AddBulkAsync(ClassificationHarness harness, int count)
    {
        string sharedPath = harness.Eml.RelativePathFor(harness.Account.Id, harness.Inbox.Id, 1);
        byte[] eml = ClassificationTestEml.For(MailKind.Legit);
        for (uint uid = 1; uid <= count; uid++)
        {
            await harness.AddRawAsync(harness.Inbox, uid, eml, $"<bulk_{uid}@example.test>", sharedEmlPath: sharedPath);
        }
    }

    // Case 25 (bulk insert of rows is slower than the standard limit). AP-07d: the brake maximum (1000 moves per folder and
    // pass) equals the command size, so only 1000 mails can still move; 1001 and more are held (ClassificationPassBrakeTests).
    [Theory(Timeout = 120_000)]
    [InlineData(1000, 1)]
    public async Task Moves_are_split_into_commands_of_at_most_1000_uids_and_cover_every_mail(int mailCount, int minimumCommands)
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        await harness.SeedMarkerAsync(harness.Inbox);
        await AddBulkAsync(harness, mailCount);

        ClassificationPassReport report = await harness.RunAsync(ClassificationHarness.ActWithLimit(1000));

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.True(commands.Count >= minimumCommands, $"{commands.Count} commands for {mailCount} mails");
        Assert.All(commands, command =>
        {
            Assert.Equal(MailCommandKind.Move, command.Kind);
            Assert.Equal(harness.Screener!.Id, command.TargetFolderId);
            Assert.InRange(command.Uids.Count, 1, 1000);
        });
        uint[] covered = CommandAssert.AllUids(commands).OrderBy(uid => uid).ToArray();
        Assert.Equal(Enumerable.Range(1, mailCount).Select(uid => (uint)uid).ToArray(), covered);
        Assert.Equal((long)mailCount, await harness.LogCountAsync());
        Assert.Equal(mailCount, report.Classified);
        Assert.Equal((uint)mailCount, (await harness.MarkerAsync(harness.Inbox))!.ClassifiedUpToUid);
        Assert.Equal(mailCount, report.MovesEnqueued);
    }

    // Case 25, retention flavour: the same limit holds for deletes
    [Fact(Timeout = 120_000)]
    public async Task Retention_deletes_are_split_into_commands_of_at_most_1000_uids()
    {
        await using ClassificationHarness harness = await ClassificationHarness.CreateAsync();
        const int mailCount = 1500;
        string sharedPath = harness.Eml.RelativePathFor(harness.Account.Id, harness.Quarantine!.Id, 1);
        var entries = new List<ClassificationLogEntry>();
        for (uint uid = 1; uid <= mailCount; uid++)
        {
            MailMessage message = await harness.AddRawAsync(harness.Quarantine!, uid, ClassificationTestEml.For(MailKind.CertainSpam), $"<old_{uid}@example.test>", sharedEmlPath: sharedPath);
            entries.Add(new ClassificationLogEntry(
                harness.Account.Id, MessageKey.For(message, null), harness.Inbox.Id, uid,
                harness.Clock.GetUtcNow() - TimeSpan.FromDays(100), ClassificationMode.Act, ClassificationOutcome.Enqueued,
                ClassificationAction.MoveToQuarantine, SpamTier.CertainSpam, 110, AuthStatus.Failed, null,
                [new SignalResult("authentication", 50, "Seeded reason.")]));
        }

        await harness.Log.CommitAsync(entries, new ClassificationMarker(harness.Inbox.Id, harness.Inbox.UidValidity, 0));

        await harness.RunAsync(ClassificationHarness.ActWithRetention(30));

        IReadOnlyList<MailCommand> commands = await harness.CommandsAsync();
        Assert.True(commands.Count >= 2);
        Assert.All(commands, command =>
        {
            Assert.Equal(MailCommandKind.DeletePermanently, command.Kind);
            Assert.Equal(harness.Quarantine!.Id, command.FolderId);
            Assert.InRange(command.Uids.Count, 1, 1000);
        });
        Assert.Equal(
            Enumerable.Range(1, mailCount).Select(uid => (uint)uid).ToArray(),
            CommandAssert.AllUids(commands).OrderBy(uid => uid).ToArray());
    }
}
