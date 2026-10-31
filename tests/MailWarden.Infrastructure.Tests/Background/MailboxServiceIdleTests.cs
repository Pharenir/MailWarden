using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Background;

namespace MailWarden.Infrastructure.Tests.Background;

/// <summary>AP-03d cases 11-14: how the service reacts to its IDLE watcher (D4).</summary>
[Trait("Category", "feature:sync")]
public sealed class MailboxServiceIdleTests
{
    private static readonly TimeSpan Poll = MailboxServiceOptions.Default.PollInterval;
    private static readonly TimeSpan IdleRenew = MailboxServiceOptions.Default.IdleRenewInterval;
    private static readonly TimeSpan Debounce = MailboxServiceOptions.Default.TriggerDebounce;
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(1);

    // Case 11
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Change_reported_by_the_watcher_triggers_a_cycle_after_the_debounce()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]);
        harness.SetIdleScript(account.Id, IdleStep.Change);

        await harness.Service.StartAsync();
        await harness.WaitForPendingDelayAsync(Debounce);
        await harness.CompleteDelayAsync(Debounce);

        await harness.WaitForSyncCountAsync(account.Id, 2);
        Assert.DoesNotContain(harness.DelayRequests, request => request.Duration == Poll && request.Task.IsCompletedSuccessfully);
    }

    // Case 12
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Watcher_timeout_re_enters_idle_without_a_cycle()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]);
        harness.SetIdleScript(account.Id, IdleStep.Timeout);

        await harness.Service.StartAsync();
        await harness.WaitForSuccessAsync(account.Id);
        await harness.WaitForIdleWaitCountAsync(account.Id, 2);
        await MailboxServiceHarness.NegativeCheckWindowAsync();

        Assert.All(harness.WaitMaxWaits(account.Id), maxWait => Assert.Equal(IdleRenew, maxWait));
        Assert.False(harness.DelayRequests.Any(request => request.Duration == Debounce));
        Assert.Equal(1, harness.SyncCount(account.Id));
        Assert.Equal(1, harness.ConnectCount(account.Id));
    }

    // Case 13
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Network_failure_in_the_watcher_reconnects_after_the_backoff_and_cycles_are_unaffected()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]);
        harness.SetIdleScript(account.Id, IdleStep.Throw(new MailTransportException(MailTransportFailure.Network, "Injected network failure.")));

        await harness.Service.StartAsync();
        await harness.WaitForPendingDelayAsync(FirstBackoff);
        await harness.WaitForSuccessAsync(account.Id);
        Assert.Equal(1, harness.ConnectCount(account.Id));

        await harness.CompleteDelayAsync(FirstBackoff);

        await harness.WaitForConnectCountAsync(account.Id, 2);
        Assert.Equal(1, harness.DisposedWatcherCount(account.Id));
        Assert.Equal(1, harness.SyncCount(account.Id));
        Assert.Equal(AccountSyncState.Idle, harness.LatestStatus(account.Id)?.State);
    }

    // Case 14
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Server_without_IDLE_is_not_reconnected_and_polling_continues()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]);
        harness.SetIdleScript(account.Id, IdleStep.Throw(new NotSupportedException("Injected: no IDLE capability.")));

        await harness.Service.StartAsync();
        await harness.WaitForIdleWaitCountAsync(account.Id, 1);
        await harness.WaitForSuccessAsync(account.Id);
        await harness.CompleteDelayAsync(Poll);
        await harness.WaitForSyncCountAsync(account.Id, 2);
        await MailboxServiceHarness.NegativeCheckWindowAsync();

        Assert.Equal(1, harness.ConnectCount(account.Id));
        Assert.Equal(1, harness.WaitCallCount(account.Id));
        Assert.False(harness.HasPendingDelay(FirstBackoff));
    }

    // D4 step 5 (derived from the contract text, no numbered case)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Authentication_failure_in_the_watcher_stops_it_until_RetryAccount()
    {
        Account account = MailboxServiceHarness.NewAccount("A", MailProvider.Google);
        await using MailboxServiceHarness harness = new([account]) { AutoCompleteDebounce = true };
        harness.SetIdleScript(
            account.Id,
            IdleStep.Throw(new AuthenticationFailedException(MailProvider.Google, AuthFailureReason.InteractionRequired, "Injected sign-in required.")));

        await harness.Service.StartAsync();
        await harness.WaitForIdleWaitCountAsync(account.Id, 1);
        await harness.WaitForSuccessAsync(account.Id);
        harness.CompleteAllPendingDelays();
        await MailboxServiceHarness.NegativeCheckWindowAsync();
        Assert.Equal(1, harness.ConnectCount(account.Id));

        harness.Service.RetryAccount(account.Id);

        await harness.WaitForConnectCountAsync(account.Id, 2);
    }
}
