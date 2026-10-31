using MailWarden.Infrastructure.Background;

namespace MailWarden.Infrastructure.Tests.Background;

/// <summary>AP-03d cases 2, 15 and 16: data-folder lock at start, account list changes, and stopping.</summary>
[Trait("Category", "feature:sync")]
public sealed class MailboxServiceLifecycleTests
{
    // Case 2
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Start_while_the_data_folder_lock_is_held_throws_and_starts_nothing()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]);
        DataFolderLock? held = DataFolderLock.TryAcquire(harness.Paths);
        Assert.NotNull(held);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.StartAsync());
        await MailboxServiceHarness.NegativeCheckWindowAsync();

        Assert.Contains("in use", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.TotalCalls);
        Assert.Equal(0, harness.AccountListCalls);
        Assert.Equal(0, harness.ConnectCount(account.Id));
        Assert.Empty(harness.DelayRequests);
        Assert.Empty(harness.Statuses);

        held.Dispose();
        await harness.Service.StartAsync();
        await harness.WaitForSuccessAsync(account.Id);
    }

    // Case 15
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task AccountsChanged_starts_the_new_account_and_stops_the_removed_one()
    {
        Account removed = MailboxServiceHarness.NewAccount("A");
        Account kept = MailboxServiceHarness.NewAccount("B");
        Account added = MailboxServiceHarness.NewAccount("C");
        await using MailboxServiceHarness harness = new([removed, kept]);
        await harness.Service.StartAsync();
        await harness.WaitForSuccessAsync(removed.Id);
        await harness.WaitForSuccessAsync(kept.Id);
        await harness.WaitForConnectCountAsync(removed.Id, 1);
        await harness.WaitForConnectCountAsync(kept.Id, 1);

        harness.ReplaceAccounts(kept, added);
        await harness.Service.AccountsChangedAsync();

        await harness.WaitForSuccessAsync(added.Id);
        await harness.WaitForConnectCountAsync(added.Id, 1);
        await harness.WaitForStateAsync(removed.Id, AccountSyncState.Stopped);
        await harness.WaitUntilAsync(() => harness.DisposedWatcherCount(removed.Id) == 1, "watcher of the removed account disposed");
        await MailboxServiceHarness.NegativeCheckWindowAsync();

        Assert.Equal(1, harness.SyncCount(removed.Id));
        Assert.Equal(1, harness.SyncCount(kept.Id));
        Assert.Equal(1, harness.ConnectCount(kept.Id));
        Assert.Equal(0, harness.DisposedWatcherCount(kept.Id));
        Assert.Equal(1, harness.SyncCount(added.Id));
        Assert.Equal(1, harness.ConnectCount(added.Id));
    }

    // Case 16
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Stop_during_a_running_cycle_cancels_it_disposes_watchers_releases_the_lock_and_is_idempotent()
    {
        Account busy = MailboxServiceHarness.NewAccount("A");
        Account quiet = MailboxServiceHarness.NewAccount("B");
        await using MailboxServiceHarness harness = new([busy, quiet]);
        TaskCompletionSource cancellationSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.SetSyncBehavior(
            busy.Id,
            async invocation =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, invocation.Ct);
                }
                catch (OperationCanceledException)
                {
                    cancellationSeen.TrySetResult();
                    throw;
                }
            });
        await harness.Service.StartAsync();
        await harness.WaitForSyncCountAsync(busy.Id, 1);
        await harness.WaitForSuccessAsync(quiet.Id);
        await harness.WaitForConnectCountAsync(busy.Id, 1);
        await harness.WaitForConnectCountAsync(quiet.Id, 1);

        await harness.Service.StopAsync();

        await cancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(harness.AllWatchersDisposed());
        Assert.Equal(AccountSyncState.Stopped, harness.LatestStatus(busy.Id)?.State);
        using (DataFolderLock? reacquired = DataFolderLock.TryAcquire(harness.Paths))
        {
            Assert.NotNull(reacquired);
        }

        await harness.Service.StopAsync();
        await harness.Service.DisposeAsync();
    }
}
