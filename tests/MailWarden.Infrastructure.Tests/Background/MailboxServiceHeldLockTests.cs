namespace MailWarden.Infrastructure.Tests.Background;

/// <summary>AP-04a case 26 (D6): <c>MailboxService</c> with an externally held data-folder lock.</summary>
[Trait("Category", "feature:sync")]
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:ui")]
public sealed class MailboxServiceHeldLockTests
{
    private const string LockFileName = "mailwarden.lock";

    // Case 26
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Service_with_held_lock_starts_runs_a_cycle_and_leaves_the_lock_held_after_stop()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using var harness = new MailboxServiceHarness([account], heldLockFactory: DataFolderLock.TryAcquire);
        Assert.NotNull(harness.HeldLock);
        string lockFile = Path.Combine(harness.Paths.Root, LockFileName);

        // Without heldLock the service would find the lock taken (by the test) and refuse to start.
        await harness.Service.StartAsync();
        await harness.WaitForSuccessAsync(account.Id);
        Assert.True(File.Exists(lockFile));

        await harness.Service.StopAsync();

        Assert.True(File.Exists(lockFile));
        Assert.Null(DataFolderLock.TryAcquire(harness.Paths));
        harness.HeldLock.Dispose();
        Assert.False(File.Exists(lockFile));
        DataFolderLock? again = DataFolderLock.TryAcquire(harness.Paths);
        Assert.NotNull(again);
        again.Dispose();
    }

    // Case 26: disposing the service must not release a lock it does not own either.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Disposing_the_service_leaves_the_held_lock_in_place()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        var harness = new MailboxServiceHarness([account], heldLockFactory: DataFolderLock.TryAcquire);
        DataFolderLock held = harness.HeldLock!;
        string lockFile = Path.Combine(harness.Paths.Root, LockFileName);
        await harness.Service.StartAsync();
        await harness.WaitForSuccessAsync(account.Id);

        await harness.Service.DisposeAsync();

        Assert.True(File.Exists(lockFile));
        Assert.Null(DataFolderLock.TryAcquire(harness.Paths));
        held.Dispose();
        await harness.DisposeAsync();
    }

    // Case 26 control: without a held lock the existing behaviour is unchanged (the service owns the lock).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Service_without_held_lock_takes_the_lock_itself_and_releases_it_on_stop()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using var harness = new MailboxServiceHarness([account]);
        string lockFile = Path.Combine(harness.Paths.Root, LockFileName);

        await harness.Service.StartAsync();
        await harness.WaitForSuccessAsync(account.Id);
        Assert.True(File.Exists(lockFile));

        await harness.Service.StopAsync();

        Assert.False(File.Exists(lockFile));
    }
}
