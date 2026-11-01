STEP-RESULT: done (implement.1)

# AP-07d – Implementation notes (Builder, implement.1)

Branch `feat/ap-07d-classifier-wiring`. No package added, no schema change. Nothing under `tests\` read or changed.
`requirement.md` and `PLAN.md` not changed: `PLAN.md` has no row table for AP-07d yet, the documenter fills it. The
coordinator's precisions of 2026-11-01 are applied (see below).

## Files
| File | Change |
|---|---|
| `src\MailWarden.Core\Classification\ClassificationPolicy.cs` | third member `MaxMovesPerPass = 100`, range 1..1000 in `Validate`; consts `MaxMovesPerPassLowerLimit`, `MaxMovesPerPassUpperLimit` (D2) |
| `src\MailWarden.Core\Classification\ClassificationOutcome.cs` | `BrakeHeld = 8` (D2) |
| `src\MailWarden.Infrastructure\Classification\ClassificationPass.cs` | brake per source folder, in `HandleFolderAsync` and `HoldMoves` (D2) |
| `src\MailWarden.Infrastructure\Classification\ClassificationPassReport.cs` | last member `BrakeHeld` (D2) |
| `src\MailWarden.Infrastructure\Classification\ClassificationPolicyFile.cs` | key `maxMovesPerPass`; `ReadRetentionDays` became the shared `ReadWholeNumber` (D2) |
| `src\MailWarden.Infrastructure\Composition\ClassificationPassSummary.cs` | new (D1) |
| `src\MailWarden.Infrastructure\Composition\ClassifyingSyncCycle.cs` | new (D1) |
| `src\MailWarden.Infrastructure\Composition\AccountCycleSteps.cs` | new, the seam for case 13 |
| `src\MailWarden.Infrastructure\Composition\AccountPipelines.cs` | composes the pass on the cycle's connection, `ClassifyAccountAsync` (D3) |
| `src\MailWarden.Infrastructure\Composition\MailWardenRuntime.cs` | new `OpenAsync` signature, loads both files, event, `OpenWithCycleStepsAsync` (D3) |
| `src\MailWarden.Presentation\IMailboxController.cs` | event `ClassificationPassCompleted`; adapter now takes the runtime (D4) |
| `src\MailWarden.Presentation\ShellViewModel.cs`, `ShellBanner.cs`, `UiText.cs` | banner (D4), start error text (D3) |
| `src\MailWarden.App\App.xaml.cs` | passes the log, new adapter constructor, start error with the file name (D3) |

## Seam for case 13
```csharp
public sealed record AccountCycleSteps(
    Func<Guid, IProgress<SyncProgress>?, CancellationToken, Task<SyncReport>> Sync,
    IMailIdleWatcherFactory IdleWatchers,
    Func<Guid, CancellationToken, Task<ClassificationPassReport>>? Classify = null,
    Func<Guid, CancellationToken, Task<CommandRunReport>>? ExecuteCommands = null);

public static Task<MailWardenRuntime> MailWardenRuntime.OpenWithCycleStepsAsync(
    DataPaths paths, AccountCycleSteps cycleSteps, CancellationToken ct = default, ICriticalLog? log = null);
```
Same composition as `OpenAsync`, only the server steps are replaced; a null `Classify` keeps the real pass on the real
database with the files of the data root; a null `ExecuteCommands` keeps the real run, which connects only when a command
is due. Public because the project has no `InternalsVisibleTo`; the app does not call it.

How to drive it, without a server:
1. Open with a fake `Sync` and an `IdleWatchers` whose `ConnectAsync` throws `NotSupportedException` (the watcher loop then
   ends).
2. **One artificial account is needed**, deviating from "no accounts" in case 13, because no account means no cycle:
   `new SqliteAccountStore(await runtime.OpenDatabaseAsync()).UpsertAsync(...)` before `Service.StartAsync()`.
3. Subscribe to `ClassificationPassCompleted`, then `Service.StartAsync()`.

What this can show: event from the real service loop; a fake `Classify` with `MovesEnqueued > 0` causes a second cycle after
the 2 s debounce (`TriggerAccount` wiring); a fake `Classify` that waits until `DisposeAsync` has begun raises nothing; a log
passed to the runtime gets the count line.

## Interface deviations and additions
- `MailboxServiceController` constructor: `(MailboxService)` became `(MailWardenRuntime)`, because the event lives on the
  runtime. Existing tests that build this adapter must change.
- `ClassificationPassReport` has no default for `BrakeHeld`, like its other members.
- Additions not in the contract: `MailWardenRuntime.InvalidFileNameDataKey` (the exception stays an exact
  `InvalidDataException`, the app reads the file name from `Exception.Data` instead of the message, which can quote a key
  from the file); `UiText.StartErrorInvalidClassifierFile(fileName)`, `UiText.BannerClassificationProblems(evaluationFailed,
  brakeHeld)`, `UiText.BannerClassificationWaiting`.
- `ShellBanner` got two members, `Classification` and `ClassificationWaiting`, not one: a pass can have problems and wait at
  the same time, and each source owns its text. Both texts then show.

## Readings where the contract is open
- Coordinator's precisions, as built: `ReadFailed > 0` alone writes a line; moves of a source folder count over all targets;
  a held mail counts in `Classified`, `TierCounts` and `BrakeHeld`, its entry keeps mode `Act` and tier; a suspended `Act`
  plans no moves, so `Recorded` and no `BrakeHeld`; banner counts are decimal digits, the waiting text has none.
- Order within a cycle: sync, classify, trigger, summary, count line.
- Count line: `Classification pass counts: evaluation failed N, brake held M`, plus `, read failed K` when above 0.
- Subscribers of the runtime event are called one by one, each failure gets its own log line, as `MailboxService` does for
  `StatusChanged`.
- A mail without a usable sender can be held as well; it has no tier and so counts only in `BrakeHeld`.

## Worth a look by the reviewer
- `ClassifyingSyncCycle.cs:76`: every `OperationCanceledException` counts as cancellation, without checking `ct`, as in
  `ClassificationPass`. An OCE from a timeout inside the pass therefore writes no log line. `MailboxService` still shows
  `WaitingAfterError` for it.
- The banner follows the last pass (case 14): a held mass move leaves the banner at the next clean pass of that account, at
  the latest after the poll interval, while the mail stays. The count line in the log and the `BrakeHeld` entries stay.
- `ShellViewModel.cs:385`: a summary of an account that left the navigation stops counting at the next summary of any
  account, not at the moment of removal. The app has no way to remove an account yet (AP-04b).
- While the pass runs the account stays `Running` with the last sync progress: no status of its own. Cosmetic.

## Tested / untested
Tested by me (no proof, no test run of `tests\`):
- `dotnet build src\MailWarden.App\MailWarden.App.csproj -warnaserror` and `src\MailWarden.Presentation\...`: 0 errors, 0
  warnings. First commit alone, from `git archive`, `MailWarden.Infrastructure.csproj`: 0 errors, 0 warnings.
  CA1068 did not fire on the binding parameter order `OpenAsync(paths, ct, log)`.
- Probe `_scratch\ap07d-probe` (not in git, artificial data, no server): invalid policy file with the data key and a second
  open; two cycles through the trigger with the event and the count line; real pass through the seam; no event during
  dispose.

Untested:
- All of `tests\`; they do not compile until adapted (`ClassificationPassReport`, `IMailboxController`, adapter constructor).
- The brake in the real pass and the banner: built, never run.
- `App.xaml.cs`: the start error window (GUI, not started).
- Cases 17 and 18 (manual).

## Questions
None open.
