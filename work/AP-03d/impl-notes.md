# AP-03d – implementation notes (builder, implement.1)

Branch `feat/ap-03d-mailbox-service`, base `main @ f69779c` (requirement commit). Build:
`dotnet build MailWarden.sln -warnaserror` → 0 warnings / 0 errors (all projects incl. tests). No tests were written,
run or edited. A builder self-check with fakes (`_scratch\service-selfcheck`, git-ignored, not a test) ran 3× green;
see "Self-check" below.

## Contract items

### D1 – data-folder lock
- `src\MailWarden.Infrastructure\Storage\DataFolderLock.cs:32` `TryAcquire` – creates the root, opens
  `<root>\mailwarden.lock` with `FileMode.OpenOrCreate`, `FileShare.None`, `FileOptions.DeleteOnClose`. Returns null
  only for Win32 error 32/33 (sharing/lock violation, `:61`); every other IO failure propagates (fail fast).
- `:55` `Dispose` closes the stream → the OS deletes the file; a crashed process releases the lock automatically.
- Extra public const `LockFileName = "mailwarden.lock"` (not in the contract, harmless).
- `MailboxService.StartAsync` acquires it **before** reading the accounts; failure →
  `InvalidOperationException("The data folder is in use by another MailWarden process.")` (contains "data folder is in
  use"). Released in `StopAsync` (also when a loop failed).

### D2 – cycle and status (`src\MailWarden.Infrastructure\Background\`)
- `AccountSyncState.cs`, `AccountSyncStatus.cs` – exactly as in the contract.
- `MailboxService.cs:415` `RunCycleAsync`: status Running (Progress null, NextRunAt null) → `executeCommands` →
  `syncAccount(progress)`.
  - Success: Idle, `LastSuccessAt`=now, `LastErrorType` null, `NextRunAt`=now+Poll, Progress null; failure counter 0.
  - `AuthenticationFailedException{InteractionRequired}` → NeedsSignIn, NextRunAt null, counter unchanged.
  - Every other exception (except OOM, and except OCE while the account token is cancelled) → WaitingAfterError,
    counter+1, NextRunAt = now + `Backoff` (`:316`: 1, 2, 4, 8, 16, 30, 30 … min).
  - OCE with the account token cancelled → propagates → the loop ends with Stopped (`:386`, `finally`).
- `:323` `DescribeError`: `GetType().Name`, for `MailTransportException` `"MailTransportException(<Failure>)"`. Never a
  message.
- Progress: `CycleProgress` (`:691`) is a direct `IProgress<T>` (not `Progress<T>`, which would post asynchronously and
  reorder events). Each report → status Running with Progress. `Close()` in `finally` drops late reports, so "Progress
  null after" holds.

### D3 – service
- `MailboxServiceOptions.cs` – positional record with validating `init` properties (pattern of `CommandOptions`);
  `Default` `:23`; ranges: Poll ≥ 1 min, IdleRenew 1..28 min, Debounce 0..60 s (bounds inclusive).
- `IServiceDelay.cs` as in the contract. `TimeProviderServiceDelay.cs:13` – production implementation
  (`Task.Delay(delay, timeProvider, ct)`), not in the contract, needed by the composition.
- `MailboxService.cs`: ctor `:71` (signature exactly as contract), `StatusChanged :95`, `CurrentStatus :115`,
  `StartAsync :134`, `StopAsync :177`, `TriggerAccount :230`, `RetryAccount :241`, `AccountsChangedAsync :256`,
  `DisposeAsync :310` (= StopAsync).
- No `MailboxService.Create` helper (optional; see open point O1 why the composition is not trivial).

### D4 – IDLE watcher
- Core: `src\MailWarden.Core\Sync\IMailIdleWatcher.cs:19`, `IMailIdleWatcherFactory.cs:19` – signatures exactly as in
  the contract; XML doc adds `NotSupportedException` on the factory (generic IMAP account) and
  `OperationCanceledException`.
- `src\MailWarden.Infrastructure\Sync\MailKitIdleWatcherFactory.cs:26` (public, ctor `(AccountService)`): shared
  token/connect/XOAUTH2 via `MailKitTransportFactory.ConnectClientAsync` (`MailKitTransportFactory.cs:52`, made
  `internal static` with the `AccountService` as parameter – the only change to existing code), then `OpenInboxAsync
  :46`: personal folder list → first folder whose `FolderRoleResolver.Resolve(...)` role is Inbox → `OpenAsync(ReadOnly)`
  (EXAMINE). No inbox → `MailTransportException(Protocol)`. MailKit failures mapped by `MailKitTransport.RunAsync`
  (same wording/classification as the sync).
- `src\MailWarden.Infrastructure\Sync\MailKitIdleWatcher.cs` (internal):
  - `WaitForChangeAsync :51` – `maxWait` ≤ 0 → `ArgumentOutOfRangeException`; no `IDLE` capability →
    `NotSupportedException` (`:56`); done token = `new CancellationTokenSource(maxWait)`; `client.IdleAsync(done,
    abort)` `:84` inside `RunAsync` (→ `MailTransportException` Network/Protocol). Returns true when an inbox event
    fired, false on timeout.
  - Events `CountChanged`, `MessageExpunged`, `MessageFlagsChanged` subscribed after EXAMINE (events of the open itself
    do not count); handler `:131` sets `changePending` and cancels the current done token.
  - A change that arrives after DONE but before the tagged OK is kept in `changePending` and returned by the next call
    without a new IDLE (no lost change).
  - Bounded DONE: once the done token fires (change, maxWait or stop), `abort` is cancelled after 10 s
    (`DoneGracePeriod :21`, registration `:75`) → a dead connection ends as `MailTransportException(Network)` instead of
    hanging (see IDLE evidence: no read timeout while idling). Stop (`ct`) cancels the done token first, then the abort
    follows after the grace period (MailKit: cancel done before cancellation token); afterwards
    `ct.ThrowIfCancellationRequested()` → OCE.
  - Read-only: the only commands on this connection are LOGIN (in connect), LIST, EXAMINE, IDLE/DONE, LOGOUT.
  - `DisposeAsync :101` – unsubscribes, LOGOUT best effort (5 s), disposes the client; idempotent.

### D5 – ServiceProbe
- `_scratch\service-probe\` **in the worktree** (`.worktrees\feat-ap-03d\_scratch\service-probe`, git-ignored, like
  the AP-03c CommandProbe), because the project references point to `..\..\src` and only this branch has the service.
  csproj copied from `_scratch\sync-probe` (original untouched). Not run.
- `ServiceProbe <dataRoot> [minutes]` (default 10). Exit codes: 0 ok, 1 usage, 2 data folder in use, 3 refused (guard),
  4 StopAsync reported loop failures.
- Output: one line per status change (`#<index> <Provider> <State> error=<type> last ok=<hh:mm:ss> next=<hh:mm:ss>
  folder n/m <Role> processed/candidates`); progress-only changes at most 1/s per account (plus folder change/end). In
  addition every IDLE event: `idle: connected` / `wait N started (max 9 min)` / `wait N change -> trigger` /
  `wait N timeout -> renew` / `wait N failed <type>` / `disposed` – this is the evidence for case 19 ("second wait after
  9 min", "test mail → change → Running within seconds"). No names, addresses, subjects, folder names, messages.
- **Guard (own decision):** refuses to start when any account has a Pending command, because every cycle executes the
  queue (server changes). The probe enqueues nothing.
- **SQLite composition (own decision, see O1):** one `SqliteDatabase` for the service's account list, one per account
  for its cycles (SyncEngine + CommandExecutor + queue), one per account for its IDLE watcher (`AccountService` reads the
  account store on every connect). Token providers and the credential store are shared.
- Ctrl+C → `StopAsync`, prints the stop duration and the final status per account.
- Second instance: opens/initialises the database (no migration when current), then `StartAsync` fails →
  "Data folder is in use …", exit 2.

## Concurrency design (MailboxService)

- **Two loops per account** (`AccountWorker :776`), both started with `Task.Run` (thread pool, `StartAsync` returns
  immediately): the account loop (`RunAccountLoopAsync :386`) and the watcher loop (`RunWatcherLoopAsync :554`). Each
  worker has its own `CancellationTokenSource` linked to the service stop source; `AccountsChangedAsync` cancels a single
  worker, `StopAsync` the service source.
- **Triggers without lost wake-ups** (`WakeSignal :716`): a level-triggered flag plus a `TaskCompletionSource`
  (`RunContinuationsAsynchronously`) under a private lock. `Set` = flag + complete; `WhenSet` returns a completed task
  when the flag is set, otherwise the current (or a fresh) waiter; `Reset` clears the flag. Because the flag is checked
  before every wait and `WhenSet` re-checks it under the same lock, a trigger between check and wait completes the wait
  at once.
- **Coalescing / exactly one extra cycle:** the account loop calls `CycleTrigger.Reset()` and `Retry.Reset()` right
  before each cycle. Any number of triggers during that cycle (or during the debounce) leaves one set flag → one more
  cycle, which resets it again.
- **Wait phase** (`WaitForNextCycleAsync :488`, `WaitForTimerOrSignalAsync :517`): `Task.WhenAny(timer, retry,
  trigger, stopped)`. The timer is `IServiceDelay.DelayAsync(waitDuration, linkedToken)` with the duration computed in
  the cycle (Poll or backoff); NeedsSignIn has no timer. In Idle a trigger or retry ends the wait; in WaitingAfterError
  and NeedsSignIn only `RetryAccount` does (a trigger stays set and is served by the next cycle). Then the debounce
  (`IServiceDelay`, skipped when 0). An abandoned timer is cancelled and observed (`ObserveFault :331`), so a faulting
  delay never becomes an unobserved task exception.
- **Stop never depends on the fake honouring the token in a wait:** `worker.Stopped` (a TCS completed by a token
  registration) is part of every `WhenAny`. Running cycles must honour the token (contract: "running cycles see
  cancellation").
- **No deadlock on stop:** `StopAsync` takes `lifecycleGate` (a `SemaphoreSlim`; also serialises `StartAsync` and
  `AccountsChangedAsync`), snapshots and clears the worker map under `stateGate`, releases it, `CancelAsync()`s the
  service source (callbacks off the caller's thread), then awaits every loop task. Loops never take `lifecycleGate`,
  and no lock is held while awaiting. `stateGate` (a `System.Threading.Lock`) only guards the worker map and the status
  map and is never held while awaiting or raising events.
- **StatusChanged never under a lock:** `UpdateStatus :649` computes and stores the new status under `stateGate`, then
  raises after leaving it (`RaiseStatusChanged :666`). All raises come from the account loop of that account (cycle
  start/end, synchronous progress reports, Stopped in `finally`), so one account's events are ordered and arrive on a
  thread-pool thread. A subscriber exception is caught per subscriber and ignored (decision D-4).
- **Unobserved exceptions:** the loops catch everything except OOM; loop tasks are always awaited in `StopAsync` /
  `AccountsChangedAsync` (`WaitForWorkerAsync :341`); an unexpected loop fault (programming error, e.g. a delay fake
  that throws) is collected and rethrown as `AggregateException` after the lock is released (fail fast, but the stop
  still completes).
- **Watcher loop:** connect → `WaitForChangeAsync(IdleRenewInterval)` repeatedly; true → `CycleTrigger.Set()` (same as
  `TriggerAccount`); false → re-IDLE. The watcher is disposed in `finally` **before** any backoff wait. End reasons
  (`WatchInboxAsync :588`): `NotSupportedException` → loop ends (polling stays); `AuthenticationFailedException` → wait
  for `WatcherRetry` (set by `RetryAccount`) or stop; anything else (`MailTransportException` and unexpected) → own
  backoff counter `WatcherFailures` (1, 2, 4 … 30 min via `IServiceDelay`) then reconnect. The watcher counter resets
  after any successful `WaitForChangeAsync` (true or false), not after a mere connect.

## MailKit 4.18.2 IDLE evidence

Checked offline in the package (`lib\net8.0\MailKit.xml`) and in the IL of `MailKit.dll` 4.18.0.0 with a small
reflection tool (`_scratch\il-probe`, git-ignored; lists the members a method references):
- `ImapClient.IdleAsync(CancellationToken doneToken, CancellationToken cancellationToken)` – XML doc: a folder must be
  selected; no other command while idle; `doneToken` must be cancellable; "cancel the doneToken before the
  cancellationToken when using SSL"; throws `NotSupportedException` when the server lacks IDLE,
  `ServiceNotConnectedException`, `IOException`, `ImapCommandException`, `ImapProtocolException`.
- `ImapIdleContext.ContinuationHandler` registers `IdleComplete` on the done token after the server's `+` continuation;
  `IdleComplete` writes `DONE\r\n` synchronously (`ImapStream.Write` + `Flush`) if the engine is still idle and sets
  the state back to Selected. So cancelling the done token from any thread (timer or folder event) ends the IDLE, and a
  done token that is already cancelled when the `+` arrives sends DONE immediately.
- `ImapCommand.StepAsync`: while `Engine.State == Idle` and the stream can time out, it saves `ReadTimeout`, sets it to
  **-1** (infinite) for the read and restores it afterwards. `MailKit.Net.NetworkStream.ReadAsync` otherwise enforces
  `ReadTimeout` through a CTS and throws `IOException(TimeoutException)`. Consequence: the 60 s socket timeout of
  `ImapSession` does **not** kill a long IDLE, but a dead connection would hang the read after DONE → hence the 10 s
  abort token (D4).
- Events: `ImapFolder.OnExists` sets `Count` and calls `OnCountChanged` (EXISTS → `CountChanged`); `OnExpunge`
  decrements `Count` and raises `MessageExpunged` (`CountChanged` deferred via a `countChanged` field);
  `OnFetchAsyncCompleted` raises `MessageFlagsChanged` for FETCH FLAGS. They are raised synchronously while MailKit
  processes the untagged response, i.e. on the flow running `IdleAsync`. `MailFolder.OnCountChanged` invokes
  `CountChanged` with `EventArgs.Empty`.
- Cancelling the done token inside the event handler is the pattern of MailKit's `ImapIdleExample` (referenced by the
  `CountChanged` XML doc). Ich bin mir unsicher: I did not have the example source, only the reference; the IL above
  shows it is safe (DONE is written synchronously, the read loop then receives the tagged OK).
- Not verified offline (case 19): that Outlook.com and Gmail actually send untagged EXISTS during IDLE on an EXAMINEd
  inbox within seconds, and how they react to a 9-minute IDLE renewal.

## Decisions (own)

- **D-1** `TriggerAccount` in WaitingAfterError/NeedsSignIn does not end the wait; it is remembered (contract: only
  `RetryAccount` "leaves" these states; an IDLE storm during an outage must not defeat the backoff).
- **D-2** NeedsSignIn does not change the failure counter; `RetryAccount` does not reset it either (consecutive failures;
  only success resets).
- **D-3** `TriggerAccount`/`RetryAccount` for unknown accounts or a service that is not running are no-ops (UI races
  with account removal). `AccountsChangedAsync` when not running → `InvalidOperationException`. `StartAsync` twice or
  after stop → `InvalidOperationException` (no restart; new instance). `StopAsync` before start → no-op.
- **D-4** A throwing `StatusChanged` subscriber is ignored (per subscriber) – a UI bug must not stop the account loop.
  This is a deliberate exception from "fail fast"; reviewer may object.
- **D-5** Initial status per account is set at start without an event: `(Idle, null, null, null, null)`; the first
  event is Running. Removed accounts keep their last status (Stopped) in `CurrentStatus`; re-added accounts get a fresh
  status at the same position.
- **D-6** Running keeps `LastSuccessAt` and `LastErrorType` of the previous cycle; NextRunAt null while Running,
  NeedsSignIn and Stopped.
- **D-7** Debounce 0 → no `DelayAsync` call for the debounce.
- **D-8** Watcher: unexpected exceptions (not `MailTransportException`) also use the reconnect backoff (contract only
  names `MailTransportException`); failing `DisposeAsync` of a watcher is ignored.
- **D-9** `MailKitIdleWatcher` checks the IDLE capability itself (before MailKit would) and throws
  `NotSupportedException` with own wording; generic IMAP accounts get `NotSupportedException` from the factory
  (existing connect helper) → the service stops the watcher for them.

## For the tester

- Fake delays: the service calls `IServiceDelay.DelayAsync` for poll (PollInterval), cycle backoff (1, 2, 4 … min),
  debounce (TriggerDebounce, not when 0) and watcher backoff (1, 2, 4 … min). Cycle backoff and watcher backoff of the
  same step have equal durations – distinguish them by call order or token, not by value.
- The wait timer's duration is the value computed in the cycle (exactly PollInterval / backoff), independent of whether
  the fake `TimeProvider` advances.
- Status order per account: Running → (Running+Progress)* → Idle|WaitingAfterError|NeedsSignIn …, Stopped last.
- `RetryAccount` with debounce > 0 → one debounce delay before the cycle.
- The lock file is `<root>\mailwarden.lock`; the root is created by `TryAcquire`.

## Open points

- **O1 (important for AP-04 / planner):** `SqliteDatabase` owns one connection and is "not thread-safe". The contract
  runs accounts in parallel, and the IDLE watchers (`AccountService.GetAccessTokenAsync` → `IAccountStore.GetAsync`)
  run concurrently with the cycles. A composition root that passes one shared `SyncEngine`/`CommandExecutor` on one
  `SqliteDatabase` (as the D3 text suggests) would use one SQLite connection from several threads. The probe avoids it
  with one connection per account and flow (WAL). AP-04 needs a decision: connection per account/flow, or a serialising
  wrapper. Not solved inside `MailboxService` because the contract hands it plain delegates.
- **O2:** The lock is taken in `StartAsync`, but a composition root has to open/initialise the database earlier (the
  service needs an `IAccountStore`). A second process therefore touches the database before it fails on the lock
  (schema migration would also run there if pending). For AP-04: acquire before `InitializeAsync`, e.g. by letting the
  app hold the lock and the service accept it, or by a lazily initialised account store.
- **O3:** `docs\`/README not touched (documenter).
