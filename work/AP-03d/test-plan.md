# AP-03d test plan (tester, step author.0 - written, not yet run)

Branch `test/ap-03d-mailbox-service`. Tests live in `tests\MailWarden.Infrastructure.Tests\Background\`, support code in
`TestSupport\MailboxServiceHarness.cs`. All are `feature:sync` (case 1 also `feature:storage`), 30 s per-test timeout.

## Case -> test
| # | Test |
|---|---|
| 1 | `DataFolderLockTests`: second acquire null, file exists while held / gone after Dispose, re-acquire; extras: independent roots, Dispose twice |
| 2 | `MailboxServiceLifecycleTests.Start_while_the_data_folder_lock_is_held_...` (no fake called, no delay, no status; start works after release) |
| 3 | `MailboxServiceCycleTests.Start_runs_executor_then_sync_...` |
| 4 | `...Poll_delay_completing_runs_the_next_cycle` |
| 5 | `...Trigger_runs_a_cycle_after_the_debounce_...` (debounce completed manually, poll delay never completed) |
| 6 | `...Three_triggers_during_a_running_cycle_...` (debounce auto-completed; 300 ms negative window) |
| 7 | `...Network_failures_back_off_1_2_4_8_16_30_30_...` and theory `Other_known_failures_...` (Protocol, transport-Authentication, non-interactive AuthenticationFailed, StorageException) |
| 8 | `...InteractionRequired_needs_sign_in_...` |
| 9 | `...Unexpected_exception_in_one_account_...` |
| 10 | `...Progress_reports_become_Running_statuses_...` |
| 11-14 | `MailboxServiceIdleTests` (4 tests) + extra for D4 step 5 (auth failure stops watcher until RetryAccount) |
| 15 | `MailboxServiceLifecycleTests.AccountsChanged_...` |
| 16 | `MailboxServiceLifecycleTests.Stop_during_a_running_cycle_...` |
| 17 | `MailboxServiceCycleTests.Status_never_contains_the_message_text_...` (theory: 4 exception kinds, marker also in inner exception and provider error code) |
| 18 | `MailboxServiceOptionsTests` (defaults, 8 invalid, 3 boundary) |
| 19 | not automated (planner) |

## Fake design
- `MailboxServiceHarness` owns the service plus fakes; one lock guards all recorded state. Every state change calls
  `Changed()`, which completes registered waiters (`WaitUntilAsync`, 20 s limit with description). No sleeps.
- Data root: `<test bin>\ap03d-data\<guid>`; deleted on dispose (best effort).
- Executor/sync fakes record `ServiceCall(kind, account, number)`; sync behaviour per account is a delegate
  (`SyncInvocation(Number, Progress, Ct)`) that can block, report progress, or return `Task.FromException`.
- `FakeServiceDelay`: each `DelayAsync` records a `DelayRequest(duration)` completed only by the test
  (`CompleteDelayAsync(duration)`, `CompleteAllPendingDelays()`); cancels with the token. Optional
  `AutoCompleteDebounce` completes delays equal to `TriggerDebounce` immediately (cases 6, 8, 9, extra idle test).
  Tests tell waits apart by duration: poll 15 min, debounce 2 s, backoff 1..30 min, idle renew 9 min (watcher only).
- `FakeIdleWatcherFactory`/`FakeIdleWatcher`: per-account script shared across reconnects (`IdleStep.Change`,
  `Timeout`, `Throw(ex)`); after the script the watcher blocks until cancelled or disposed. Records connects, maxWait
  values and disposal.
- Time: existing `FixedTimeProvider` (constant), so `NextRunAt = now + x` is asserted exactly.
- Negative checks ("no extra cycle") use a 300 ms real-time window; a bug can only make them miss, never flake.

## Assumptions / contract questions
1. LastErrorType for non-transport exceptions is `GetType().Name` (e.g. "AuthenticationFailedException",
   "StorageException"); case 9 only asserts it contains "InvalidOperationException". Name vs FullName is unspecified.
2. Initial status before the first cycle: tests wait for `Idle` with `LastSuccessAt != null`, so an initial Idle is allowed.
3. Delays: tests assume the poll wait requests exactly `PollInterval` and the trigger wait exactly `TriggerDebounce`
   through `IServiceDelay`, and the cycle backoff wait the backoff value. With a zero-length debounce, an implementation
   might skip the delay; the tests use the default 2 s.
4. Case 15/16: Stopped status is asserted for the removed account (15) and the account with the running cycle (16),
   not for idle accounts on StopAsync. Should every loop report Stopped?
5. NotSupported (case 14): the watcher disposal is not asserted (contract silent); only "no reconnect".
6. D4 step 5: "stop until RetryAccount" is assumed to mean RetryAccount reconnects the watcher (extra test).
7. DataFolderLock.Dispose twice must not throw (extra test, standard IDisposable).
8. Lock file name `mailwarden.lock` in the data root, and the root directory must already exist for TryAcquire
   (tests create it). What if the directory is missing?
9. Exception text of case 2 must contain "in use".
10. `tests\test-map.json` gained entries for `Infrastructure\Background` (sync + storage).

## fix.2
- Case 20: `MailboxServiceIdleTests.Other_authentication_failures_in_the_watcher_reconnect_after_the_backoff_...` (theory: Network, AccessDenied, Unknown).
- Case 21: `MailboxServiceIdleTests.InteractionRequired_in_the_watcher_stops_it_until_RetryAccount` (was the D4 step 5 extra test; already used InteractionRequired, only renamed).
