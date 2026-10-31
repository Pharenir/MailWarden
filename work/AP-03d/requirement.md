---
id: AP-03d
slug: mailbox-service
title: Background mailbox service – schedule, IDLE on the inbox, data-folder lock, status
test-scope: S3
test-filter: "Category=feature:sync|Category=smoke"
base: main @ 09b33b7 + this requirement (builder and tester)
status: ready (2026-10-31)
---

# AP-03d – Background mailbox service

## Goal
A long-running service keeps all accounts current without user action:
- **Cycle:** it executes queued commands and then runs a sync pass per account (one cycle).
- **Schedule:** it repeats the cycle on a schedule.
- **New mail:** it reacts within seconds to new mail in the inbox (IMAP IDLE).
- **Queued commands:** it runs them soon after they are queued.
- **Status:** it reports a status per account for the UI (AP-04).
- **One process:** one data folder is used by one process at a time (secure N3 of AP-03c).

The app wires it up in AP-04; until then a probe runs it.

## Context
- Building blocks (unchanged unless stated):
  - `SyncEngine.SyncAccountAsync(accountId, IProgress<SyncProgress>?, ct)` (AP-03a/b);
  - `CommandExecutor.ExecutePendingAsync(accountId, ct)` (AP-03c);
  - `IMailTransportFactory`, `ImapSession` (AP-03b B6), `DataPaths`, `IAccountStore`.
- Failure types:
  - `MailTransportException` (Network / Protocol / Authentication);
  - `AuthenticationFailedException` (AP-02a, reason `InteractionRequired` = sign-in needed).
- Outlook.com and Gmail end an IDLE after about 29 minutes. RFC 2177 recommends re-issuing IDLE at least every 29
  minutes; 9 minutes is used here.
- Deferred to this AP: secure N3 of AP-03c (lock per data folder; probe + app/service in parallel).

## Non-goals
- No UI, no tray icon, no Windows service, no autostart (AP-04 / AP-13).
- No logging to files (the AP-04 logging rule); the service only exposes status.
- No IDLE on folders other than the inbox; other folders follow the schedule.
- No new packages. The tests control time through the delay abstraction in D3, not through
  `Microsoft.Extensions.TimeProvider.Testing`.

## Contract (binding)

### D1 – data-folder lock (`MailWarden.Infrastructure.Storage`)
```csharp
/// Cross-process lock of one data folder: an exclusively opened file "<root>\mailwarden.lock" (FileShare.None,
/// DeleteOnClose). Held for the lifetime of the returned object.
public sealed class DataFolderLock : IDisposable
{
    /// Null when another process (or another instance in this process) holds the lock.
    public static DataFolderLock? TryAcquire(DataPaths paths);
    public void Dispose();
}
```
- `MailboxService.StartAsync` acquires it. If it fails → `InvalidOperationException` ("data folder is in use"), and
  nothing starts. The lock is released at `StopAsync`/`DisposeAsync`.
- The planner updates the probes (`sync-probe`, `command-probe`, `shiftdel-probe`) to acquire it too.

### D2 – one cycle per account
```csharp
namespace MailWarden.Infrastructure.Background;

public enum AccountSyncState { Idle = 1, Running = 2, WaitingAfterError = 3, NeedsSignIn = 4, Stopped = 5 }

/// Status for the UI. LastErrorType: exception type name and, for MailTransportException, its Failure value
/// (e.g. "MailTransportException(Network)"); never a message.
public sealed record AccountSyncStatus(Guid AccountId, AccountSyncState State, DateTimeOffset? LastSuccessAt,
    string? LastErrorType, DateTimeOffset? NextRunAt, SyncProgress? Progress);
```
- **Cycle** = `ExecutePendingAsync(accountId)`, then `SyncAccountAsync(accountId, progress)`. Progress reports are
  forwarded as status updates (State Running, Progress set).
- **Success:** State Idle, `LastSuccessAt` = now, `LastErrorType` null, `NextRunAt` = now + `PollInterval`.
  Per-folder errors inside the `SyncReport` do not make a cycle fail; the AP-04 UI shows them later.
- **Failure:**
  - `AuthenticationFailedException` with reason `InteractionRequired` → `NeedsSignIn`; no automatic retry until
    `RetryAccount(accountId)`.
  - Any other `AuthenticationFailedException`, `MailTransportException` or `StorageException` →
    `WaitingAfterError` with backoff 1, 2, 4, 8, 16, 30, 30 … minutes (consecutive failures; success resets).
  - `OperationCanceledException` from `StopAsync` → `Stopped`.
  - Any other exception → also `WaitingAfterError`, with the backoff. The service never crashes because of one
    account.

### D3 – service
```csharp
public sealed record MailboxServiceOptions(TimeSpan PollInterval, TimeSpan IdleRenewInterval, TimeSpan TriggerDebounce)
{
    public static MailboxServiceOptions Default { get; } = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(9), TimeSpan.FromSeconds(2));
}   // PollInterval ≥ 1 min, IdleRenewInterval 1..28 min, TriggerDebounce 0..60 s, else ArgumentOutOfRangeException

/// Waiting abstraction so tests can control time: completes after the delay or when cancelled.
public interface IServiceDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class MailboxService : IAsyncDisposable
{
    public MailboxService(DataPaths paths, IAccountStore accounts, Func<Guid, CancellationToken, Task<CommandRunReport>> executeCommands,
        Func<Guid, IProgress<SyncProgress>?, CancellationToken, Task<SyncReport>> syncAccount,
        IMailIdleWatcherFactory idleWatchers, MailboxServiceOptions options, IServiceDelay delay, TimeProvider timeProvider);

    public event EventHandler<AccountSyncStatus>? StatusChanged;   // raised on a thread-pool thread
    public IReadOnlyList<AccountSyncStatus> CurrentStatus { get; }

    /// Acquires the data-folder lock (D1), reads the accounts and starts one loop + one IDLE watcher per account.
    public Task StartAsync(CancellationToken ct = default);
    /// Cancels all loops and watchers, waits for them (running cycles see cancellation), releases the lock. Idempotent.
    public Task StopAsync();

    /// Triggers a cycle for the account soon (after TriggerDebounce); used after enqueueing commands and by "refresh".
    public void TriggerAccount(Guid accountId);
    /// Leaves NeedsSignIn / WaitingAfterError and runs a cycle soon.
    public void RetryAccount(Guid accountId);
    /// Re-reads the account list: starts loops for new accounts, stops loops of removed ones.
    public Task AccountsChangedAsync(CancellationToken ct = default);
}
```
- **Per-account loop:** run a cycle at start. Then wait for the first of these:
  - `NextRunAt`;
  - a trigger (`TriggerAccount`, an IDLE change or `RetryAccount`), followed by the debounce;
  - stop.

  Then run the next cycle. One cycle per account at a time. Accounts run independently and in parallel. A trigger
  during a running cycle causes exactly one more cycle afterwards; triggers coalesce.
- **Waits:** every wait goes through `IServiceDelay`, never `Task.Delay` directly. `NextRunAt` and the backoff come
  from the `TimeProvider`.
- **Composition:** the `Func` parameters keep the service testable. The composition root passes
  `CommandExecutor.ExecutePendingAsync` and `SyncEngine.SyncAccountAsync`. A static helper
  `MailboxService.Create(DataPaths, …real dependencies…)` is optional and is the builder's choice.

### D4 – IDLE watcher
```csharp
namespace MailWarden.Core.Sync;

public interface IMailIdleWatcher : IAsyncDisposable
{
    /// Waits on the inbox until the server reports a change (new message, expunge, flag change) or maxWait elapsed.
    /// Returns true on a change, false on timeout. Throws MailTransportException on connection problems and
    /// NotSupportedException when the server has no IDLE capability.
    Task<bool> WaitForChangeAsync(TimeSpan maxWait, CancellationToken ct = default);
}

public interface IMailIdleWatcherFactory
{
    /// Connects (silent token), opens the inbox read-only (EXAMINE).
    Task<IMailIdleWatcher> ConnectAsync(Account account, CancellationToken ct = default);
}
```
- **Watcher loop per account (service):**
  1. Connect.
  2. Repeat `WaitForChangeAsync(IdleRenewInterval)`. `true` → `TriggerAccount`. `false` → just re-IDLE.
  3. On `MailTransportException` → dispose, then reconnect after the D2 backoff of the watcher itself (it is
     separate from the cycle backoff).
  4. On `NotSupportedException` → stop the watcher for this account; polling stays.
  5. On `AuthenticationFailedException` → stop the watcher until `RetryAccount`.
- **MailKit implementation** (`MailKitIdleWatcher` + factory, Infrastructure.Sync):
  - `ImapSession` connect and EXAMINE of the folder with role Inbox. The factory resolves the folder name via the
    folder list and `FolderRoleResolver`, like the sync.
  - `ImapClient.IdleAsync(doneToken, ct)`; done is signalled on `CountChanged`, `MessageExpunged`,
    `MessageFlagsChanged` or after `maxWait`.
  - Read-only: there is never a write command on this connection.

### D5 – Probe (`_scratch\service-probe`, builder writes, planner runs)
`ServiceProbe <dataRoot> [minutes]` runs the real service for all accounts of the data root for N minutes (default
10).
- **Output:** one line per status change, without names: account index, provider, state, last error type, next run
  time, progress counts.
- **Stop:** Ctrl+C stops it cleanly.
- **Lock:** a second instance on the same data root fails with "data folder is in use".

## Expected behaviour (test cases; fakes for executor/sync/idle/delay/time)
| # | Case | Expected |
|---|---|---|
| 1 | DataFolderLock: acquire, second TryAcquire (same process) | second returns null; after Dispose acquire works again; lock file gone after Dispose |
| 2 | StartAsync while the lock is held | InvalidOperationException, no loop started, no fake called |
| 3 | Start with 2 accounts | each gets a cycle: executeCommands then syncAccount (order per account); status Running → Idle with LastSuccessAt, NextRunAt = now + PollInterval |
| 4 | Poll: delay for PollInterval completes | second cycle runs |
| 5 | TriggerAccount during the wait | cycle after TriggerDebounce, before PollInterval |
| 6 | Three triggers during a running cycle | exactly one extra cycle afterwards |
| 7 | Network failure in sync | WaitingAfterError, LastErrorType "MailTransportException(Network)", NextRunAt now + 1 min; consecutive failures 2, 4, 8, 16, 30, 30 min; success resets |
| 8 | AuthenticationFailedException InteractionRequired | NeedsSignIn, no further cycle on poll; RetryAccount → cycle runs |
| 9 | Unexpected exception (InvalidOperationException) in one account | that account WaitingAfterError; the other account keeps running |
| 10 | Progress forwarding | status updates with Progress set during the cycle, Progress null after |
| 11 | Idle watcher returns true | TriggerAccount effect (cycle after debounce) |
| 12 | Idle watcher returns false | WaitForChangeAsync called again (re-IDLE), no cycle |
| 13 | Idle watcher throws Network | watcher reconnects after backoff (factory called again); cycles unaffected |
| 14 | Idle watcher NotSupported | watcher not reconnected; polling continues |
| 15 | AccountsChangedAsync: one account added, one removed | new loop + watcher started; removed loop stopped (Stopped status), its watcher disposed |
| 16 | StopAsync during a running cycle | cancellation reaches the fake; all loops end; all watchers disposed; lock released; StopAsync twice is fine |
| 17 | Status never contains a message text (fakes throw with marker text) | marker absent from every status |
| 18 | Options validation | out-of-range values → ArgumentOutOfRangeException; Default values as stated |
| 19 | Real accounts (planner, ServiceProbe 10 min, all three accounts) | every account Idle after the first cycle, no errors; IDLE renewed (second WaitForChange after 9 min); a test mail sent by Dennis during the run triggers a cycle within seconds; a second ServiceProbe on the same root fails with "in use"; Ctrl+C stops cleanly |

## Done when
- Build `-warnaserror` green; S3 green (central building blocks: Core interfaces).
- Review APPROVE. Security review only if the reviewer finds a trigger. The service does not write to the server
  itself; the commands come from AP-03c.
- Documenter: README (background service, poll interval, IDLE, lock), PLAN rows AP-03d.
- Case 19 green (planner, with Dennis sending one test mail).
