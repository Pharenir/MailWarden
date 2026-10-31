using MailWarden.Core.Sync;

namespace MailWarden.Infrastructure.Tests.Storage;

/// <summary>AP-03a case 23: <see cref="SqliteFolderSyncStateStore"/>.</summary>
[Trait("Category", "feature:storage")]
public sealed class SqliteFolderSyncStateStoreTests
{
    private static readonly DateTimeOffset SyncedAt = new(2026, 10, 31, 12, 30, 45, TimeSpan.FromHours(2));

    // Case 23
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Save_then_get_returns_the_saved_state()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteFolderSyncStateStore(storage.Database);
        Account account = await storage.AddAccountAsync();
        MailFolder folder = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        var state = new FolderSyncState(folder.Id, 77, 1234, SyncedAt);

        await store.SaveAsync(state);

        FolderSyncState? loaded = await store.GetAsync(folder.Id);
        Assert.Equal(state, loaded);
        Assert.Equal(SyncedAt.UtcDateTime, loaded!.LastSyncedAt!.Value.UtcDateTime);
    }

    // Case 23: null timestamp
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Null_last_synced_at_round_trips_as_null()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteFolderSyncStateStore(storage.Database);
        Account account = await storage.AddAccountAsync();
        MailFolder folder = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");

        await store.SaveAsync(new FolderSyncState(folder.Id, 5, 0, null));

        FolderSyncState? loaded = await store.GetAsync(folder.Id);
        Assert.NotNull(loaded);
        Assert.Null(loaded.LastSyncedAt);
        Assert.Equal(0UL, loaded.HighestModSeq);
    }

    // Case 23: overwrite
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_save_overwrites_without_adding_a_row()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteFolderSyncStateStore(storage.Database);
        Account account = await storage.AddAccountAsync();
        MailFolder folder = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        await store.SaveAsync(new FolderSyncState(folder.Id, 5, 10, SyncedAt));

        var updated = new FolderSyncState(folder.Id, 9, 12, SyncedAt.AddHours(3));
        await store.SaveAsync(updated);

        Assert.Equal(updated, await store.GetAsync(folder.Id));
        Assert.Equal(1, await storage.CountRowsAsync("folder_sync_state"));
    }

    // Case 23: unknown folder
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Get_of_unknown_folder_returns_null()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteFolderSyncStateStore(storage.Database);

        Assert.Null(await store.GetAsync(424242));
    }

    // Case 23: unknown folder on save
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Save_for_unknown_folder_throws_StorageException_and_stores_nothing()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteFolderSyncStateStore(storage.Database);

        await Assert.ThrowsAnyAsync<StorageException>(
            () => store.SaveAsync(new FolderSyncState(424242, 1, 1, SyncedAt)));

        Assert.Equal(0, await storage.CountRowsAsync("folder_sync_state"));
    }

    // Case 23: delete
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_removes_only_the_given_state_and_missing_state_is_a_no_op()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteFolderSyncStateStore(storage.Database);
        Account account = await storage.AddAccountAsync();
        MailFolder doomed = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        MailFolder survivor = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk");
        MailFolder withoutState = await storage.AddFolderAsync(account.Id, FolderRole.Sent, "Sent");
        await store.SaveAsync(new FolderSyncState(doomed.Id, 1, 1, SyncedAt));
        await store.SaveAsync(new FolderSyncState(survivor.Id, 2, 2, SyncedAt));

        await store.DeleteAsync(doomed.Id);
        await store.DeleteAsync(withoutState.Id);
        await store.DeleteAsync(987654);

        Assert.Null(await store.GetAsync(doomed.Id));
        Assert.Equal(2u, (await store.GetAsync(survivor.Id))!.HighestSyncedUid);
        Assert.Equal(1, await storage.CountRowsAsync("folder_sync_state"));
    }

    // Case 23: states are independent per folder
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task States_of_different_folders_do_not_interfere()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteFolderSyncStateStore(storage.Database);
        Account account = await storage.AddAccountAsync();
        MailFolder first = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        MailFolder second = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk");

        await store.SaveAsync(new FolderSyncState(first.Id, 11, 111, SyncedAt));
        await store.SaveAsync(new FolderSyncState(second.Id, 22, 222, null));

        Assert.Equal(11u, (await store.GetAsync(first.Id))!.HighestSyncedUid);
        Assert.Equal(222UL, (await store.GetAsync(second.Id))!.HighestModSeq);
    }

    // Case 23: cascade on folder delete
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Deleting_the_folder_cascades_to_its_sync_state()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteFolderSyncStateStore(storage.Database);
        Account account = await storage.AddAccountAsync();
        MailFolder doomed = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        MailFolder survivor = await storage.AddFolderAsync(account.Id, FolderRole.Junk, "Junk");
        await store.SaveAsync(new FolderSyncState(doomed.Id, 1, 1, SyncedAt));
        await store.SaveAsync(new FolderSyncState(survivor.Id, 2, 2, SyncedAt));

        await storage.Folders.DeleteAsync(doomed.Id);

        Assert.Null(await store.GetAsync(doomed.Id));
        Assert.NotNull(await store.GetAsync(survivor.Id));
        Assert.Equal(1, await storage.CountRowsAsync("folder_sync_state"));
    }

    // Case 23: cascade on account delete (account -> folders -> sync state)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Deleting_the_account_cascades_to_sync_states()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteFolderSyncStateStore(storage.Database);
        Account account = await storage.AddAccountAsync();
        MailFolder folder = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        await store.SaveAsync(new FolderSyncState(folder.Id, 1, 1, SyncedAt));

        await storage.Accounts.DeleteAsync(account.Id);

        Assert.Equal(0, await storage.CountRowsAsync("folder_sync_state"));
    }

    // Case 23: value range (UIDs are 32 bit, mod-seq up to the SQLite integer maximum)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Large_uid_and_mod_seq_values_round_trip()
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteFolderSyncStateStore(storage.Database);
        Account account = await storage.AddAccountAsync();
        MailFolder folder = await storage.AddFolderAsync(account.Id, FolderRole.Inbox, "INBOX");
        var state = new FolderSyncState(folder.Id, uint.MaxValue, (ulong)long.MaxValue, SyncedAt);

        await store.SaveAsync(state);

        Assert.Equal(state, await store.GetAsync(folder.Id));
    }
}
