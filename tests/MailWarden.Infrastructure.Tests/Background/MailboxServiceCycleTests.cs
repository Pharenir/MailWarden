using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Background;

/// <summary>AP-03d cases 3-10 and 17: the per-account cycle, schedule, triggers, failure handling and status content.</summary>
[Trait("Category", "feature:sync")]
public sealed class MailboxServiceCycleTests
{
    private const string SecretMarker = "SECRET_MARKER_5c1e44";

    private static readonly TimeSpan Poll = MailboxServiceOptions.Default.PollInterval;
    private static readonly TimeSpan Debounce = MailboxServiceOptions.Default.TriggerDebounce;

    private static Task Fail(Exception failure) => Task.FromException(failure);

    private static MailTransportException Network(string message = "Injected network failure.") =>
        new(MailTransportFailure.Network, message);

    private static AuthenticationFailedException SignInRequired(string message = "Injected sign-in required.") =>
        new(MailProvider.Google, AuthFailureReason.InteractionRequired, message);

    // Case 3
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Start_runs_executor_then_sync_per_account_and_reports_Running_then_Idle()
    {
        Account first = MailboxServiceHarness.NewAccount("A");
        Account second = MailboxServiceHarness.NewAccount("B", MailProvider.Microsoft);
        await using MailboxServiceHarness harness = new([first, second]);

        await harness.Service.StartAsync();
        AccountSyncStatus idleFirst = await harness.WaitForSuccessAsync(first.Id);
        AccountSyncStatus idleSecond = await harness.WaitForSuccessAsync(second.Id);

        foreach (Account account in new[] { first, second })
        {
            Assert.Equal(new[] { "exec", "sync" }, harness.Calls.Where(call => call.AccountId == account.Id).Select(call => call.Kind));
            List<AccountSyncStatus> history = [.. harness.Statuses.Where(status => status.AccountId == account.Id)];
            int runningIndex = history.FindIndex(status => status.State == AccountSyncState.Running);
            int idleIndex = history.FindIndex(status => status.State == AccountSyncState.Idle && status.LastSuccessAt is not null);
            Assert.True(runningIndex >= 0, "A Running status was expected.");
            Assert.True(runningIndex < idleIndex, "Running must precede the successful Idle.");
        }

        DateTimeOffset now = harness.Time.GetUtcNow();
        foreach (AccountSyncStatus idle in new[] { idleFirst, idleSecond })
        {
            Assert.Equal(now, idle.LastSuccessAt);
            Assert.Equal(now + Poll, idle.NextRunAt);
            Assert.Null(idle.LastErrorType);
            Assert.Null(idle.Progress);
        }

        Assert.Contains(harness.Service.CurrentStatus, status => status.AccountId == first.Id && status.State == AccountSyncState.Idle);
        Assert.Contains(harness.Service.CurrentStatus, status => status.AccountId == second.Id && status.State == AccountSyncState.Idle);
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Poll_delay_completing_runs_the_next_cycle()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]);
        await harness.Service.StartAsync();
        await harness.WaitForSuccessAsync(account.Id);

        await harness.CompleteDelayAsync(Poll);

        await harness.WaitForSyncCountAsync(account.Id, 2);
        await harness.WaitForExecuteCountAsync(account.Id, 2);
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Trigger_runs_a_cycle_after_the_debounce_and_before_the_poll_interval()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]);
        await harness.Service.StartAsync();
        await harness.WaitForSuccessAsync(account.Id);

        harness.Service.TriggerAccount(account.Id);
        await harness.WaitForPendingDelayAsync(Debounce);
        Assert.Equal(1, harness.SyncCount(account.Id));

        await harness.CompleteDelayAsync(Debounce);

        await harness.WaitForSyncCountAsync(account.Id, 2);
        Assert.DoesNotContain(harness.DelayRequests, request => request.Duration == Poll && request.Task.IsCompletedSuccessfully);
    }

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Three_triggers_during_a_running_cycle_cause_exactly_one_extra_cycle()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]) { AutoCompleteDebounce = true };
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.SetSyncBehavior(account.Id, invocation => invocation.Number == 1 ? gate.Task.WaitAsync(invocation.Ct) : Task.CompletedTask);
        await harness.Service.StartAsync();
        await harness.WaitForSyncCountAsync(account.Id, 1);

        harness.Service.TriggerAccount(account.Id);
        harness.Service.TriggerAccount(account.Id);
        harness.Service.TriggerAccount(account.Id);
        gate.SetResult();

        await harness.WaitForSyncCountAsync(account.Id, 2);
        await MailboxServiceHarness.NegativeCheckWindowAsync();
        Assert.Equal(2, harness.SyncCount(account.Id));
        Assert.Equal(2, harness.ExecuteCount(account.Id));
    }

    // Case 7
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Network_failures_back_off_1_2_4_8_16_30_30_minutes_and_success_resets()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]);
        harness.SetSyncBehavior(account.Id, invocation => invocation.Number is <= 7 or 9 ? Fail(Network()) : Task.CompletedTask);
        DateTimeOffset now = harness.Time.GetUtcNow();
        int[] backoffMinutes = [1, 2, 4, 8, 16, 30, 30];

        await harness.Service.StartAsync();
        for (int index = 0; index < backoffMinutes.Length; index++)
        {
            TimeSpan expected = TimeSpan.FromMinutes(backoffMinutes[index]);
            AccountSyncStatus waiting = await harness.WaitForStatusAsync(
                account.Id,
                status => status.State == AccountSyncState.WaitingAfterError && status.NextRunAt == now + expected,
                $"WaitingAfterError, next run in {expected}",
                fromIndex: 0);
            Assert.Equal("MailTransportException(Network)", waiting.LastErrorType);

            await harness.CompleteDelayAsync(expected);
            await harness.WaitForSyncCountAsync(account.Id, index + 2);
        }

        AccountSyncStatus recovered = await harness.WaitForSuccessAsync(account.Id);
        Assert.Null(recovered.LastErrorType);
        Assert.Equal(now + Poll, recovered.NextRunAt);

        int mark = harness.StatusMark();
        await harness.CompleteDelayAsync(Poll);
        AccountSyncStatus failedAgain = await harness.WaitForStatusAsync(
            account.Id, status => status.State == AccountSyncState.WaitingAfterError, "WaitingAfterError after reset", mark);
        Assert.Equal(now + TimeSpan.FromMinutes(1), failedAgain.NextRunAt);
    }

    // Case 7 (failure classes of D2)
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("protocol", "MailTransportException(Protocol)")]
    [InlineData("transport-auth", "MailTransportException(Authentication)")]
    [InlineData("auth-access-denied", "AuthenticationFailedException")]
    [InlineData("storage", "StorageException")]
    public async Task Other_known_failures_wait_with_the_first_backoff_and_name_only_the_error_type(string kind, string expectedType)
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]);
        Exception failure = kind switch
        {
            "protocol" => new MailTransportException(MailTransportFailure.Protocol, "Injected protocol failure."),
            "transport-auth" => new MailTransportException(MailTransportFailure.Authentication, "Injected login failure."),
            "auth-access-denied" => new AuthenticationFailedException(MailProvider.Microsoft, AuthFailureReason.AccessDenied, "Injected denied."),
            _ => new MailWarden.Core.Storage.StorageException("Injected storage failure."),
        };
        harness.SetSyncBehavior(account.Id, invocation => invocation.Number == 1 ? Fail(failure) : Task.CompletedTask);

        await harness.Service.StartAsync();
        AccountSyncStatus waiting = await harness.WaitForStateAsync(account.Id, AccountSyncState.WaitingAfterError);

        Assert.Equal(expectedType, waiting.LastErrorType);
        Assert.Equal(harness.Time.GetUtcNow() + TimeSpan.FromMinutes(1), waiting.NextRunAt);
    }

    // Case 8
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task InteractionRequired_needs_sign_in_stops_polling_and_RetryAccount_runs_a_cycle()
    {
        Account account = MailboxServiceHarness.NewAccount("A", MailProvider.Google);
        await using MailboxServiceHarness harness = new([account]) { AutoCompleteDebounce = true };
        harness.SetSyncBehavior(account.Id, invocation => invocation.Number == 1 ? Fail(SignInRequired()) : Task.CompletedTask);

        await harness.Service.StartAsync();
        AccountSyncStatus needsSignIn = await harness.WaitForStateAsync(account.Id, AccountSyncState.NeedsSignIn);
        Assert.Equal("AuthenticationFailedException", needsSignIn.LastErrorType);

        harness.CompleteAllPendingDelays();
        await MailboxServiceHarness.NegativeCheckWindowAsync();
        Assert.Equal(1, harness.SyncCount(account.Id));
        Assert.Equal(AccountSyncState.NeedsSignIn, harness.LatestStatus(account.Id)?.State);

        harness.Service.RetryAccount(account.Id);

        await harness.WaitForSyncCountAsync(account.Id, 2);
        AccountSyncStatus recovered = await harness.WaitForSuccessAsync(account.Id);
        Assert.Null(recovered.LastErrorType);
    }

    // Case 9
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unexpected_exception_in_one_account_leaves_the_other_account_running()
    {
        Account broken = MailboxServiceHarness.NewAccount("A");
        Account healthy = MailboxServiceHarness.NewAccount("B");
        await using MailboxServiceHarness harness = new([broken, healthy]) { AutoCompleteDebounce = true };
        harness.SetSyncBehavior(broken.Id, _ => Fail(new InvalidOperationException("Injected unexpected failure.")));

        await harness.Service.StartAsync();
        AccountSyncStatus waiting = await harness.WaitForStateAsync(broken.Id, AccountSyncState.WaitingAfterError);
        await harness.WaitForSuccessAsync(healthy.Id);

        Assert.Contains("InvalidOperationException", waiting.LastErrorType, StringComparison.Ordinal);
        Assert.Equal(harness.Time.GetUtcNow() + TimeSpan.FromMinutes(1), waiting.NextRunAt);

        harness.Service.TriggerAccount(healthy.Id);
        await harness.WaitForSyncCountAsync(healthy.Id, 2);
        Assert.Equal(1, harness.SyncCount(broken.Id));
        Assert.Equal(AccountSyncState.WaitingAfterError, harness.LatestStatus(broken.Id)?.State);
    }

    // Case 10
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Progress_reports_become_Running_statuses_with_Progress_and_Progress_is_null_afterwards()
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]);
        SyncProgress reported = new(account.Id, FolderId: 7, FolderRole.Inbox, FolderNumber: 1, FolderCount: 3, Processed: 4, Candidates: 10);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.SetSyncBehavior(
            account.Id,
            async invocation =>
            {
                invocation.Progress?.Report(reported);
                await gate.Task.WaitAsync(invocation.Ct);
            });

        await harness.Service.StartAsync();
        AccountSyncStatus running = await harness.WaitForStatusAsync(
            account.Id, status => status.State == AccountSyncState.Running && status.Progress is not null, "Running with Progress");
        gate.SetResult();
        AccountSyncStatus idle = await harness.WaitForSuccessAsync(account.Id);

        Assert.Equal(reported, running.Progress);
        Assert.Null(idle.Progress);
        Assert.Null(harness.LatestStatus(account.Id)?.Progress);
    }

    // Case 17
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("network")]
    [InlineData("sign-in")]
    [InlineData("unexpected")]
    [InlineData("auth-code")]
    public async Task Status_never_contains_the_message_text_of_an_exception(string kind)
    {
        Account account = MailboxServiceHarness.NewAccount("A");
        await using MailboxServiceHarness harness = new([account]);
        Exception failure = kind switch
        {
            "network" => new MailTransportException(MailTransportFailure.Network, SecretMarker, new IOException(SecretMarker)),
            "sign-in" => new AuthenticationFailedException(MailProvider.Google, AuthFailureReason.InteractionRequired, SecretMarker, SecretMarker),
            "auth-code" => new AuthenticationFailedException(MailProvider.Microsoft, AuthFailureReason.Network, SecretMarker, SecretMarker, new IOException(SecretMarker)),
            _ => new InvalidOperationException(SecretMarker),
        };
        harness.SetSyncBehavior(account.Id, _ => Fail(failure));
        AccountSyncState expectedState = kind == "sign-in" ? AccountSyncState.NeedsSignIn : AccountSyncState.WaitingAfterError;

        await harness.Service.StartAsync();
        await harness.WaitForStateAsync(account.Id, expectedState);
        await MailboxServiceHarness.NegativeCheckWindowAsync();

        Assert.NotEmpty(harness.Statuses);
        Assert.All(harness.Statuses, status => Assert.DoesNotContain(SecretMarker, status.ToString(), StringComparison.Ordinal));
        Assert.All(harness.Service.CurrentStatus, status => Assert.DoesNotContain(SecretMarker, status.ToString(), StringComparison.Ordinal));
    }
}
