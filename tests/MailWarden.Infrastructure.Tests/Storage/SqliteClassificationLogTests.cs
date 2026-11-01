using MailWarden.Core.Classification;

namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-07c D2 / cases 5 and 6: <see cref="SqliteClassificationLog"/> against real in-memory SQLite.</summary>
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:classifier")]
public sealed class SqliteClassificationLogTests
{
    private static readonly DateTimeOffset ClassifiedAt = new(2026, 10, 31, 14, 30, 15, TimeSpan.FromHours(2));

    private static ClassificationLogEntry NewEntry(
        Guid accountId,
        string messageKey,
        long folderId,
        uint uid = 11,
        ClassificationOutcome outcome = ClassificationOutcome.Enqueued,
        string? overrideText = null,
        IReadOnlyList<SignalResult>? signals = null) =>
        new(
            accountId,
            messageKey,
            folderId,
            uid,
            ClassifiedAt,
            ClassificationMode.Act,
            outcome,
            ClassificationAction.MoveToJunk,
            SpamTier.LikelySpam,
            40,
            AuthStatus.Failed,
            overrideText,
            signals ?? [new SignalResult("authentication", 40, "SPF and DKIM failed for 'paypal.test'.")]);

    private static async Task<(StorageFixture Storage, SqliteClassificationLog Log, Account Account, MailFolder Folder)> CreateAsync()
    {
        StorageFixture storage = await StorageFixture.CreateAsync();
        Account account = await storage.AddAccountAsync();
        MailFolder folder = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        return (storage, new SqliteClassificationLog(storage.Database), account, folder);
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Entry_round_trips_with_all_fields_and_the_signals_in_order()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            ClassificationLogEntry entry = NewEntry(
                account.Id,
                "m:aaaa",
                folder.Id,
                uid: 4242,
                overrideText: "Contact \"quoted\" reason, umlaut äöü",
                signals:
                [
                    new SignalResult("authentication", 40, "SPF and DKIM \"failed\" for 'paypal.test'."),
                    new SignalResult("random-domain", -5, "Reason with umlaut ä and unicode ☃ and a | pipe."),
                    new SignalResult("suspicious-tld", 0, "Zero points still stored."),
                ]);
            var marker = new ClassificationMarker(folder.Id, folder.UidValidity, 4242);

            await log.CommitAsync([entry], marker);

            ClassificationLogEntry loaded = Assert.IsType<ClassificationLogEntry>(await log.GetAsync(account.Id, "m:aaaa"));
            Assert.Equal(entry.AccountId, loaded.AccountId);
            Assert.Equal(entry.MessageKey, loaded.MessageKey);
            Assert.Equal(entry.SourceFolderId, loaded.SourceFolderId);
            Assert.Equal(entry.SourceUid, loaded.SourceUid);
            Assert.Equal(entry.ClassifiedAt, loaded.ClassifiedAt);
            Assert.Equal(entry.EffectiveMode, loaded.EffectiveMode);
            Assert.Equal(entry.Outcome, loaded.Outcome);
            Assert.Equal(entry.PlannedAction, loaded.PlannedAction);
            Assert.Equal(entry.Tier, loaded.Tier);
            Assert.Equal(entry.Score, loaded.Score);
            Assert.Equal(entry.AuthStatus, loaded.AuthStatus);
            Assert.Equal(entry.Override, loaded.Override);
            Assert.Equal(entry.Signals, loaded.Signals);
        }
    }

    // Case 5: entry of a mail that could not be read has no tier, no auth status, no signals
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Entry_without_tier_and_auth_status_round_trips_as_null()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            var entry = new ClassificationLogEntry(
                account.Id, "h:bbbb", folder.Id, 3, ClassifiedAt, ClassificationMode.RecordOnly,
                ClassificationOutcome.ReadFailed, ClassificationAction.None, null, 0, null, null, []);

            await log.CommitAsync([entry], new ClassificationMarker(folder.Id, folder.UidValidity, 3));

            ClassificationLogEntry loaded = Assert.IsType<ClassificationLogEntry>(await log.GetAsync(account.Id, "h:bbbb"));
            Assert.Null(loaded.Tier);
            Assert.Null(loaded.AuthStatus);
            Assert.Null(loaded.Override);
            Assert.Empty(loaded.Signals);
            Assert.Equal(ClassificationMode.RecordOnly, loaded.EffectiveMode);
            Assert.Equal(ClassificationOutcome.ReadFailed, loaded.Outcome);
        }
    }

    // Case 5: every enum value survives
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task All_outcomes_actions_and_tiers_round_trip()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            var entries = new List<ClassificationLogEntry>();
            int index = 0;
            foreach (ClassificationOutcome outcome in Enum.GetValues<ClassificationOutcome>())
            {
                foreach (ClassificationAction action in Enum.GetValues<ClassificationAction>())
                {
                    SpamTier tier = Enum.GetValues<SpamTier>()[index++ % 4];
                    entries.Add(new ClassificationLogEntry(
                        account.Id, $"m:{index}", folder.Id, (uint)index, ClassifiedAt, ClassificationMode.Act, outcome, action,
                        tier, index, AuthStatus.Unknown, null, []));
                }
            }

            await log.CommitAsync(entries, new ClassificationMarker(folder.Id, folder.UidValidity, (uint)index));

            foreach (ClassificationLogEntry entry in entries)
            {
                ClassificationLogEntry loaded = Assert.IsType<ClassificationLogEntry>(await log.GetAsync(account.Id, entry.MessageKey));
                Assert.Equal(entry.Outcome, loaded.Outcome);
                Assert.Equal(entry.PlannedAction, loaded.PlannedAction);
                Assert.Equal(entry.Tier, loaded.Tier);
                Assert.Equal(entry.Score, loaded.Score);
            }
        }
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unknown_key_and_unknown_account_give_null()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            await log.CommitAsync([NewEntry(account.Id, "m:known", folder.Id)], new ClassificationMarker(folder.Id, 1000, 11));

            Assert.Null(await log.GetAsync(account.Id, "m:unknown"));
            Assert.Null(await log.GetAsync(Guid.NewGuid(), "m:known"));
        }
    }

    // Case 5: the first entry for a key wins
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Duplicate_key_keeps_the_first_entry()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            ClassificationLogEntry first = NewEntry(account.Id, "m:dup", folder.Id, uid: 11, outcome: ClassificationOutcome.Enqueued);
            ClassificationLogEntry second = NewEntry(account.Id, "m:dup", folder.Id, uid: 99, outcome: ClassificationOutcome.Recorded, overrideText: "later");
            await log.CommitAsync([first], new ClassificationMarker(folder.Id, 1000, 11));

            await log.CommitAsync([second], new ClassificationMarker(folder.Id, 1000, 99));

            ClassificationLogEntry loaded = Assert.IsType<ClassificationLogEntry>(await log.GetAsync(account.Id, "m:dup"));
            Assert.Equal(first, loaded with { Signals = first.Signals });
            Assert.Equal(11u, loaded.SourceUid);
            Assert.Equal(ClassificationOutcome.Enqueued, loaded.Outcome);
            Assert.Null(loaded.Override);
            Assert.Equal(1L, await storage.CountRowsAsync("classification_log"));
            Assert.Equal(99u, (await log.GetMarkerAsync(folder.Id))!.ClassifiedUpToUid);
        }
    }

    // Case 5: the same key twice inside one commit
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Duplicate_key_inside_one_commit_keeps_the_first_entry()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            ClassificationLogEntry first = NewEntry(account.Id, "m:twin", folder.Id, uid: 5);
            ClassificationLogEntry second = NewEntry(account.Id, "m:twin", folder.Id, uid: 6);

            await log.CommitAsync([first, second], new ClassificationMarker(folder.Id, 1000, 6));

            Assert.Equal(5u, (await log.GetAsync(account.Id, "m:twin"))!.SourceUid);
            Assert.Equal(1L, await storage.CountRowsAsync("classification_log"));
        }
    }

    // Case 5: the key is scoped to the account
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Same_key_in_two_accounts_gives_two_entries()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            Account other = await storage.AddAccountAsync("Account_B");
            MailFolder otherFolder = await storage.AddFolderAsync(other.Id, FolderRole.Inbox, "INBOX");

            await log.CommitAsync([NewEntry(account.Id, "m:shared", folder.Id, uid: 1)], new ClassificationMarker(folder.Id, 1000, 1));
            await log.CommitAsync([NewEntry(other.Id, "m:shared", otherFolder.Id, uid: 2)], new ClassificationMarker(otherFolder.Id, 1000, 2));

            Assert.Equal(1u, (await log.GetAsync(account.Id, "m:shared"))!.SourceUid);
            Assert.Equal(2u, (await log.GetAsync(other.Id, "m:shared"))!.SourceUid);
        }
    }

    // Case 5: marker round trip
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Marker_is_null_until_set_and_round_trips_and_is_replaced_by_the_next_commit()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            MailFolder junk = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk");
            Assert.Null(await log.GetMarkerAsync(folder.Id));

            await log.CommitAsync([], new ClassificationMarker(folder.Id, 1000, 50));

            Assert.Equal(new ClassificationMarker(folder.Id, 1000, 50), await log.GetMarkerAsync(folder.Id));
            Assert.Null(await log.GetMarkerAsync(junk.Id));
            Assert.Equal(0L, await storage.CountRowsAsync("classification_log"));

            await log.CommitAsync([], new ClassificationMarker(folder.Id, 2000, 3));

            Assert.Equal(new ClassificationMarker(folder.Id, 2000, 3), await log.GetMarkerAsync(folder.Id));
            Assert.Equal(1L, await storage.CountRowsAsync("classification_marker"));
        }
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Large_uids_round_trip_without_overflow()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            var marker = new ClassificationMarker(folder.Id, uint.MaxValue, uint.MaxValue);

            await log.CommitAsync([NewEntry(account.Id, "m:big", folder.Id, uid: uint.MaxValue)], marker);

            Assert.Equal(marker, await log.GetMarkerAsync(folder.Id));
            Assert.Equal(uint.MaxValue, (await log.GetAsync(account.Id, "m:big"))!.SourceUid);
        }
    }

    // Case 5: one transaction, nothing is stored when the commit fails
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failing_commit_stores_neither_entries_nor_marker()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            var markerForMissingFolder = new ClassificationMarker(folder.Id + 1000, 1000, 5);

            await Assert.ThrowsAnyAsync<Exception>(
                () => log.CommitAsync([NewEntry(account.Id, "m:rollback", folder.Id)], markerForMissingFolder));

            Assert.Null(await log.GetAsync(account.Id, "m:rollback"));
            Assert.Equal(0L, await storage.CountRowsAsync("classification_log"));
            Assert.Equal(0L, await storage.CountRowsAsync("classification_marker"));
        }
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cancelled_token_throws()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => log.CommitAsync([NewEntry(account.Id, "m:cancel", folder.Id)], new ClassificationMarker(folder.Id, 1000, 1), new CancellationToken(canceled: true)));

            Assert.Equal(0L, await storage.CountRowsAsync("classification_log"));
        }
    }

    // ---- case 6: cascades --------------------------------------------------------------------------------------------

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Deleting_the_account_removes_its_log_entries_and_markers()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            Account other = await storage.AddAccountAsync("Account_B");
            MailFolder otherFolder = await storage.AddFolderAsync(other.Id, FolderRole.Inbox, "INBOX");
            await log.CommitAsync([NewEntry(account.Id, "m:one", folder.Id), NewEntry(account.Id, "m:two", folder.Id, uid: 12)], new ClassificationMarker(folder.Id, 1000, 12));
            await log.CommitAsync([NewEntry(other.Id, "m:other", otherFolder.Id)], new ClassificationMarker(otherFolder.Id, 1000, 11));

            await storage.Accounts.DeleteAsync(account.Id);

            Assert.Null(await log.GetAsync(account.Id, "m:one"));
            Assert.Null(await log.GetMarkerAsync(folder.Id));
            Assert.Equal(1L, await storage.CountRowsAsync("classification_log"));
            Assert.Equal(1L, await storage.CountRowsAsync("classification_marker"));
            Assert.NotNull(await log.GetAsync(other.Id, "m:other"));
            Assert.NotNull(await log.GetMarkerAsync(otherFolder.Id));
        }
    }

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Deleting_a_folder_removes_its_marker_and_leaves_the_other_markers()
    {
        (StorageFixture storage, SqliteClassificationLog log, Account account, MailFolder folder) = await CreateAsync();
        await using (storage)
        {
            MailFolder junk = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk");
            await log.CommitAsync([], new ClassificationMarker(folder.Id, 1000, 5));
            await log.CommitAsync([], new ClassificationMarker(junk.Id, 2000, 7));

            await storage.Folders.DeleteAsync(folder.Id);

            Assert.Null(await log.GetMarkerAsync(folder.Id));
            Assert.Equal(new ClassificationMarker(junk.Id, 2000, 7), await log.GetMarkerAsync(junk.Id));
            Assert.Equal(1L, await storage.CountRowsAsync("classification_marker"));
        }
    }
}
