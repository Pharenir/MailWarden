---
id: AP-07d
slug: classifier-wiring
title: Classification pass in the running app - cycle wiring, brake, visible problems, default record only
test-scope: S3
test-filter: "Category=feature:classifier|Category=feature:ui|Category=feature:sync|Category=smoke"
base: chore/ap-07d-requirement (builder and tester)
status: ready (2026-11-01)
---

# AP-07d - classification pass in the running app

## Goal
After each sync pass the running app classifies new mail with `ClassificationPass` (AP-07c, D8 there). By default it only records
(`classification-policy.json` missing → `RecordOnly`), so nothing moves on a real account until Dennis switches it on, and `Act` still
rests while the address book is empty (AP-06).

## Context
- `src\MailWarden.Infrastructure\Composition\AccountPipelines.cs`: one connection per account cycle; `SyncAccountAsync` is the `syncAccount`
  function of `MailboxService`. `MailWardenRuntime.OpenAsync` composes it.
- `ClassificationPass`, `MailKitFolderProvisioner`, `ClassificationPolicyFile`, `ClassifierSettingsFile`, `SqliteClassificationLog`,
  `SqliteContactStore`. `ClassificationPassReport` counts mails.
- Findings of AP-07c to settle here: J4 (brake), visibility of `EvaluationFailed`, PLAN AP-07 #4b "error in the log".
- **Project rule since 2026-11-01:** no account names or addresses of Dennis in the repo, not in notes or messages either: only `Konto 1`,
  `Konto 2`, `Konto 3`.

## Non-goals
- No settings UI, no "Warum?" view, no feedback, no old-stock check, no mapping editor (AP-08). No contact collection (AP-06).
- Images loading automatically for trusted mail: later, `TODO.md`.
- No new package, no schema change except what D2 needs (none expected).

## Contract (binding)

### D1 - one cycle (`MailWarden.Infrastructure.Composition`, testable without a server)
```csharp
/// Result of one pass for the UI; counts only, never message data.
public sealed record ClassificationPassSummary(Guid AccountId, int EvaluationFailed, int ReadFailed, int BrakeHeld, bool ActSuspendedNoContacts);

public sealed class ClassifyingSyncCycle
{
    public ClassifyingSyncCycle(
        Func<Guid, IProgress<SyncProgress>?, CancellationToken, Task<SyncReport>> sync,
        Func<Guid, CancellationToken, Task<ClassificationPassReport>> classify,
        Action<Guid> triggerAccount,
        Action<ClassificationPassSummary> reportSummary,
        ICriticalLog log);
    public Task<SyncReport> RunAsync(Guid accountId, IProgress<SyncProgress>? progress, CancellationToken ct);
}
```
1. `sync`. An exception ends the cycle as before; `classify` does not run.
2. `classify`. An exception (other than cancellation) writes one critical log line with the exception's type name and fixed text, and
   propagates, so `MailboxService` shows `WaitingAfterError` with its backoff. This fulfils PLAN AP-07 #4b. Cancellation propagates
   without a log line.
3. When `MovesEnqueued + RetentionDeletesEnqueued > 0`: `triggerAccount(accountId)`, so the commands run soon instead of after the poll
   interval. `RetentionDeletesRejected` or a `BrakeHeld` never triggers.
4. `reportSummary` once per successful pass. An exception from it writes a critical log line and does not fail the cycle.
5. `EvaluationFailed > 0` or `BrakeHeld > 0` writes one critical log line with the counts only.
6. Returns the report of `sync`.

### D2 - brake (J4 of AP-07c)
- `ClassificationPolicy` gets a third member `int MaxMovesPerPass = 100`, range 1..1000, otherwise `ArgumentException` from `Validate`.
  `classification-policy.json` gets the optional key `maxMovesPerPass` with the existing strictness. Default values stay safe.
- New `ClassificationOutcome.BrakeHeld = 8` and a last report member `int BrakeHeld` (mails).
- In `ClassificationPass`, per folder in effective `Act`: when the moves planned for one folder in one pass exceed the limit, **none** of them
  is enqueued. These entries get outcome `BrakeHeld`, the planned action stays visible in the log, and the marker advances as usual. They
  are not decided again (AP-08 can offer that later). Exactly the limit still moves. `RecordOnly` is unaffected. Retention deletes are not
  part of the brake (they have their own days setting).

### D3 - runtime
- `MailWardenRuntime.OpenAsync(DataPaths paths, CancellationToken ct = default, ICriticalLog? log = null)` loads
  `classification-policy.json` and `classifier-settings.json` from the data root once at start, before the lock's work continues. An invalid
  file fails `OpenAsync` with its `InvalidDataException` (lock released, as for other failures); the app shows its start error window with a
  fixed text and the file name. Null `log` means no logging.
- `AccountPipelines` composes the pass per account cycle on that cycle's connection, with `MailKitFolderProvisioner` and
  `SqliteClassificationLog`, `SqliteContactStore`. `MailboxService` gets the cycle's `RunAsync` as its `syncAccount`.
- `triggerAccount` is `MailboxService.TriggerAccount`.
- `public event EventHandler<ClassificationPassSummary>? ClassificationPassCompleted` on the runtime, raised on the cycle's thread. It is not
  raised after dispose.

### D4 - visible problems (`MailWarden.Presentation`)
- `IMailboxController` gets `event EventHandler<ClassificationPassSummary>? ClassificationPassCompleted`, with the same marshalling as
  `StatusChanged` and the same handling of handler exceptions.
- `ShellViewModel` keeps the last summary per known account and sets a new banner source `Classification` with one fixed German text from
  `UiText` when any account has `EvaluationFailed > 0` or `BrakeHeld > 0`. It names the counts added over the accounts' last summaries and
  nothing else. It clears when no account has any left. Unknown accounts are ignored. `ActSuspendedNoContacts` alone shows **no** banner while
  the mode is not `Act` (it is only true in `Act`), and in `Act` a separate fixed text says the filter waits for the address book.
- `ReadFailed` shows no banner, only its count in the log line of the pass when above 0.

## Expected behaviour
Test cases with fakes for the functions; real SQLite and queue for D2.

| # | Case | Expected |
|---|---|---|
| 1 | Normal cycle: order | sync, then classify, then summary; returns the sync report |
| 2 | Moves 2; retention deletes 1; both 0; only rejected 3; only held 5 | trigger once; once; never; never; never |
| 3 | Sync throws | classify not called, summary not called, exception propagates, no log line from the cycle |
| 4 | Classify throws (marker text); cancellation | one log line with the type name, marker nowhere, propagates; no log line |
| 5 | Summary handler throws | one log line, cycle result normal |
| 6 | `EvaluationFailed` 2, `BrakeHeld` 3 | one log line with the counts, no message data |
| 7 | Policy file: `maxMovesPerPass` valid, 0, 1001, wrong type; missing | per D2; default 100 |
| 8 | Pass, `Act`, limit 3: 3 planned moves in the Inbox; 4 planned moves | moved; none moved, 4 × `BrakeHeld`, report 4, queue empty, marker advanced |
| 9 | Positive control and separation: 2 in the Inbox, 4 in Junk with limit 3 | Inbox moves, Junk held |
| 10 | `RecordOnly` with 200 mails | no `BrakeHeld`, no command |
| 11 | Next pass after a hold: same mail again | `SkippedKnown` |
| 12 | Runtime: invalid `classification-policy.json`; invalid `classifier-settings.json`; missing files | `InvalidDataException`, lock released and a second open works; opens with defaults |
| 13 | Runtime with fakes in the data root and no accounts: `ClassificationPassCompleted` wiring by the composition | see builder's seam; no server |
| 14 | Banner: one account with 2 failed; second with 1 held; clean pass of the first; clean pass of the second; unknown account | text with the counts; updated; updated; cleared; ignored |
| 15 | Summary with `ActSuspendedNoContacts` true | fixed waiting text, no count |
| 16 | Summary event marshalled through the dispatcher; handler exception | on the dispatcher; log line, no propagation |
| 17 | Manual, Dennis: app on a copy with new mail | log entries appear, nothing moves, no banner; no slowdown of the sync |
| 18 | Manual, planner, copy, counts only: `classification-policy.json` with `act`, a test contact, test mails between `Konto 2` and `Konto 3` only after Dennis's approval | mails move through the running service, soon after arrival |

## Done when
- Build `-warnaserror` green; S3 green (interfaces, policy).
- Review APPROVE; security review without Critical or High (trigger: classifier actions that can delete, now in the running app).
- Documenter: `README.md`, `docs\classifier.md`, PLAN rows, TODO; no account names.
