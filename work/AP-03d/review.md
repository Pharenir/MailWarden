# AP-03d – review.1 (reviewer)

Scope: `git diff f69779c 4a78c79 -- src work/AP-03d/impl-notes.md PLAN.md` plus `_scratch\service-probe\Program.cs`
(worktree, git-ignored). `tests\` not read. HEAD bb1220b has no `src`/`PLAN.md` change since 4a78c79.
Build `dotnet build MailWarden.sln -warnaserror` at bb1220b: 0 warnings, 0 errors (incl. test projects).

## Verdict: APPROVE

No blocker. S1 (stop must not skip waiting for the loops) is cheap and should be fixed in this AP. S2–S4 can be fixed
now or carried to AP-04 as planner decisions.

## Checked (OK)

- **Lost triggers / coalescing:** `WakeSignal` (`MailboxService.cs:716`) is level-triggered. `WhenSet` re-checks the
  flag under the same lock as `Set`, so a trigger between the `IsSet` check (`:526`) and `WhenAny` (`:542`) completes
  the wait at once. `Reset` happens only in the owning loop, right before the cycle (`:396-397`, `:564`). The effect:
  - triggers during a cycle leave one set flag, which means exactly one extra cycle;
  - triggers during the debounce are served by the cycle that follows;
  - `WhenAny` can never spin on a completed waiter whose flag is cleared.
- **Stop without deadlock:**
  - `StopAsync` snapshots and clears the worker map under `stateGate`, then cancels and awaits outside the lock. The
    loops never take `lifecycleGate`.
  - `worker.Stopped` is in every `WhenAny`.
  - Stop during a cycle: OCE goes to the fake. Stop during the debounce: the token-aware `DelayAsync`.
  - A second or concurrent `StopAsync` waits on the gate, sees `Stopped` and returns. `DisposeAsync` before start is a
    no-op.
  - `AccountsChangedAsync` vs `StopAsync` are serialised by the gate. After stop, `AccountsChangedAsync` throws
    `InvalidOperationException` (documented, fail fast).
- **Locks:** `stateGate` is never held across an await or while raising. `UpdateStatus` (`:649`) stores under the lock
  and raises after it. `WakeSignal` completes its TCS inside its lock, but with `RunContinuationsAsynchronously`.
  `lifecycleGate` is an async gate by design.
- **Tasks observed:**
  - Loop and watcher tasks are awaited in `WaitForWorkerAsync` (`:341`).
  - An abandoned timer is cancelled and observed.
  - `WaitForChangeAsync` is always awaited.
  - Exception: see NIT 1.
- **D2:**
  - `Backoff` (`:316`) gives 1, 2, 4, 8, 16, 30, 30 … min. Only success resets the counter. NeedsSignIn has no timer,
    and only `Retry` ends its wait.
  - Cancellation is narrowed by `when (ct.IsCancellationRequested)`. A foreign OCE becomes `WaitingAfterError`.
  - OOM is excluded everywhere.
  - `DescribeError` (`:323`) gives only the type name, or `MailTransportException(<Failure>)`. No message reaches
    `AccountSyncStatus`.
  - Progress: the direct `IProgress` plus `Close()` in `finally`. `SyncEngine` reports inline (`SyncEngine.cs:289ff`),
    so no late report can overwrite Idle.
- **D4 watcher loop:**
  - The watcher is disposed in `finally` (`:621-626`) before any backoff.
  - The reconnect uses its own counter, reset only after a successful wait.
  - `NotSupported` ends the loop and polling stays.
  - Auth waits for `WatcherRetry` or stop. A `RetryAccount` during the failing session is not lost (`:564`, then set).
- **`MailKitIdleWatcher`:**
  - **Read-only:** `ImapSession.ConnectAsync` (CONNECT, AUTHENTICATE), `GetFoldersAsync(StatusItems.None)` (LIST),
    `OpenAsync(ReadOnly)` (EXAMINE), IDLE/DONE, LOGOUT. No write command exists on this connection.
  - **IDLE:** the capability is checked per wait (`:56`). `maxWait` = `options.IdleRenewInterval` (`MailboxService.cs:596`).
  - **Done token:**
    - fires on `maxWait`, on a folder event and on stop;
    - `abort` follows 10 s later (`:75-77`), so a dead connection ends as OCE, which is mapped to `Network`
      (`MailKitTransport.Classify` `:416`). On stop it ends as OCE that passes through (`ct` cancelled);
    - `changePending` keeps a change that arrives between DONE and the tagged OK.
  - **Event handlers:** subscribed after EXAMINE and detached first in `DisposeAsync` (`:109-111`). Dispose is
    idempotent. On a failed open, the factory disposes the client (`MailKitIdleWatcherFactory.cs:38-42`).
  - **Disposal order** (`using` registration vs. CTS): the registration's `Dispose` waits for a running callback, so
    `CancelAfter` never hits a disposed `abortSource`.
- **`DataFolderLock`:**
  - opened with `FileShare.None` + `DeleteOnClose`;
  - only sharing/lock violation (32/33) gives null, everything else propagates;
  - released in the `StartAsync` catch (`:159-163`) and in the `StopAsync` `finally` (`:204-210`);
  - a missing root is created (`:241`), which is fine because the service creates the data folder anyway;
  - the OS releases the lock on a crash.
- **Plan:** rows 1–5 ✅ match the diff. Row 6 ⚠️ is correct (probe not run).
- **Builder decisions:**
  - **D-1** (`TriggerAccount` in error states) agreed: an IDLE storm must not defeat the backoff. Consequence for AP-04:
    "Refresh" in WaitingAfterError/NeedsSignIn must call `RetryAccount`, not `TriggerAccount`.
  - **D-5** (removed accounts keep Stopped) agreed, as in case 15. Consequence for AP-04: the UI filters
    `CurrentStatus` against the account list.
  - **D-4:** see S3.

## BLOCK

None.

## SHOULD

1. **A stop that skips waiting releases the lock while loops still run** (`MailboxService.cs:198`, likewise `:293`).
   - **Problem:** if `serviceStop.CancelAsync()` faults, the `foreach` with `WaitForWorkerAsync` is skipped. A callback
     can throw, for example MailKit's DONE write that `doneSource.Cancel` triggers through `ct.Register` (`MailKitIdleWatcher.cs:77`).
     *Ich bin mir unsicher* whether MailKit's `IdleComplete` swallows IO errors. The consequences:
     - the `finally` releases the data-folder lock while loops still run (and may write);
     - the loop tasks stay unobserved;
     - `state` is already `Stopped`, so no second stop helps.
   - **The same in `AccountsChangedAsync`:** the removed workers are taken out of the map but neither awaited nor
     disposed.
   - **Proposal:** wrap `CancelAsync` in `try/catch` and add the exception to `failures`, then wait for all workers
     anyway. The existing `AggregateException` reports it.
2. **The watcher stops permanently on transient auth failures** (`MailboxService.cs:612-615`, `:570-573`).
   - **Cause:** every `AuthenticationFailedException` stops the watcher until `RetryAccount`, including
     `AuthFailureReason.Network`/`Unknown`. `ConnectClientAsync` fetches the token **before** connecting
     (`MailKitTransportFactory.cs:65`).
   - **Typical case:** after sleep or resume with an expired access token and no network yet, the token refresh fails
     with `Network`, so the watcher waits for a retry. The cycle loop recovers on its own through the backoff and
     reaches Idle. The user sees no error and never presses "retry", so IDLE stays dead for the rest of the session.
   - **Why only SHOULD:** this follows the wording of the contract (D4.5), but it is inconsistent with D2. There only
     `InteractionRequired` waits for a retry and every other auth failure uses the backoff.
   - **Proposal (planner decision):**
     - (a) only `InteractionRequired` stops the watcher; other reasons use the reconnect backoff; or
     - (b) a successful cycle also sets `WatcherRetry`.
     (a) is the more consistent option.
3. **Subscriber exceptions are swallowed silently** (`MailboxService.cs:680-683`, decision D-4).
   - **Agreed:** a subscriber must not stop the loop. Letting the exception through would also be wrong: through
     `CycleProgress.Report` it would be counted as a sync failure (wrong `LastErrorType`), and at `:418` it would kill
     the loop.
   - **But:** completely invisible contradicts "fail fast, no silent fallbacks".
   - **Proposal:** collect the first exception per subscriber (or all) and throw it in `StopAsync` together with the
     loop failures in the existing `AggregateException`. AP-04 logs it as soon as there is file logging. At least
     record this as an AP-04 requirement in `TODO.md`.
4. **New third copy of the disconnect logic** (`MailKitIdleWatcher.cs:112-127`).
   - **Duplicates:** it duplicates `MailKitTransport.cs:352ff` and `MailKitCommandTransport.cs:204ff`, including the
     constant `DisconnectTimeoutMilliseconds`. The new copy makes it three.
   - **Proposal:** add an `internal static Task DisconnectQuietlyAsync(ImapClient)` helper in `MailKitTransport`
     (next to `RunAsync`), used by all three. `MailKitTransport` keeps the logic.

## NIT

1. `MailboxService.cs:502-506`: `ObserveFault` runs only for an unfinished timer. A timer that faulted at the same time
   as a winning trigger stays unobserved (`UnobservedTaskException`). Practically impossible with
   `TimeProviderServiceDelay`. Simpler: always `ObserveFault(timerTask)` when it was not awaited.
2. `MailboxService.cs:511` (debounce) and `:576` (watcher backoff) wait only through the token, not additionally on
   `worker.Stopped`. The impl notes ("Stop never depends on the fake honouring the token in a wait") claim this for
   every wait. Fine with the real delay, but the note is too broad, or the two places should get the same race.
3. `MailboxService.cs:146`: the message "in use by another MailWarden **process**" also appears for a second instance
   in the same process (the contract says "process or instance"). Suggest "in use by another MailWarden instance".
4. `DataFolderLock.cs:254`: during the short delete-pending window after the holder's `Dispose`, a concurrent open can
   fail with `UnauthorizedAccessException` (ERROR_ACCESS_DENIED) instead of null. Rare and fail fast, so acceptable.
   At most worth a sentence in the XML doc.
5. **Stop duration:** with a dead connection, a watcher stop can take up to about 15 s per account (10 s DONE grace
   plus 5 s LOGOUT). The accounts run in parallel because all are cancelled together. Fine, but note it for the AP-04
   shutdown (closing the window).
6. **Expected extra cycle:** every cycle that executed commands (Move/Delete/Flag) causes EXPUNGE/FETCH on the IDLE
   connection and therefore exactly one more cycle. It terminates (the sync is read-only), but expect it in case 19 and
   in the README.
7. `Backoff` exists in a similar form in `CommandExecutor.cs:375` (with a different cap, 60 min). This is an old
   duplicate in a looser form. A shared helper with the cap as a parameter is optional.

## O1 – SQLite composition (recommendation for AP-04)

- **The probe's approach is correct.** It uses one `SqliteDatabase` (one connection) per independent flow:
  - account cycle (SyncEngine + CommandExecutor + queue/stores, sequential within the account loop);
  - account watcher (`AccountService` → account store);
  - service lifecycle (`StartAsync`/`AccountsChangedAsync`, serialised by `lifecycleGate`).

  No connection is used by two threads at the same time. WAL is active (`SqliteDatabase.cs:218`) and `Pooling=false`.
- **AP-04 composition root must:**
  1. **Connection per flow** as in the probe. The UI (reads, enqueue, account management) gets its own connection(s),
     used only from the UI thread or behind its own gate. Do **not** pass one `SyncEngine`/`CommandExecutor` on one
     `SqliteDatabase` into the service. A serialising wrapper is the worse alternative: it holds a lock across awaits
     and serialises all accounts.
  2. **Move the probe's `AccountPipelines` into a production class in Infrastructure** (for example a
     per-account-pipeline factory or the optional `MailboxService.Create`). Otherwise the app and the probe each keep
     their own copy.
  3. **Concurrent writers:** SQLite allows only one writer. Microsoft.Data.Sqlite retries `SQLITE_BUSY` up to
     `CommandTimeout` (30 s). *Ich bin mir unsicher* whether all `BeginTransactionAsync` calls start as `BEGIN IMMEDIATE`
     (the default since Microsoft.Data.Sqlite 5 is non-deferred). A deferred read→write upgrade gives
     `SQLITE_BUSY_SNAPSHOT` without a retry. AP-04 should verify this with a test: two accounts sync in parallel into
     one file.
  4. **O2: take the lock before `InitializeAsync`.** Today a second process opens and migrates the database before
     `StartAsync` fails. With a newer build that would be a migration under a running instance. Proposal: the
     composition root acquires `DataFolderLock` first and hands it to the service (contract change: an overload or
     constructor parameter), or the service gets an `IAccountStore` that initialises lazily.
  5. **Shared token providers/credential store:**
     - `DpapiCredentialStore` serialises through a `SemaphoreSlim`; `GoogleTokenProvider` is stateless.
     - The watcher and the cycle of the same account can refresh at the same time. *Ich bin mir unsicher* whether the
       MSAL cache serialisation (`BeforeAccess`/`AfterAccess` through the credential store) avoids lost updates when
       Microsoft rotates the refresh token. Check this in AP-04 or the security review there.

## Security trigger

**No security review for AP-03d.** None of the project triggers is substantively affected:
- **Sign-in/token:** `ConnectClientAsync` was only made `internal static` with an explicit `AccountService`. The token
  path, XOAUTH2 and the "token never in messages" rule are unchanged. The watcher factory uses exactly this path.
- **Deleting actions:** the service runs only the existing, already reviewed `CommandExecutor` (AP-03c) and adds no
  delete logic. The service and the watcher send no write commands themselves (checked above). Running unattended
  after an enqueue is the intended behaviour. The gate is the enqueue in the AP-04 UI.
- **Lock file:** a fixed name below the data root, no paths from foreign data, `FileShare.None`, no content.
- **Status:** contains no message texts, names or addresses (`DescribeError`). The probe output contains no names.

**Recommendation:** AP-04 needs a security review, because there the composition (O1.5, shared token caches across
threads), the logging and the UI enqueue come together.