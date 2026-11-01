
# AP-04b - review.1 (2026-11-01)

**Verdict: BLOCK.** B1: a key acts on the selection at execution, not at the keystroke, so a permanent delete can hit another mail → U1. S1 target invisible if the tooltip fails → U5. S2 → U6, S3 → U7, S4 → U8. DPI and focus → U9. NITs → `TODO.md`.

# review.3

Delta `f8bb513..7eb7a4a` (`src`, `work/AP-04b/impl-notes.md`), 2026-11-01. Line numbers as of `7eb7a4a`.

**Verdict: APPROVE.** No MUST. V1 to V4 do what they are meant to do, and the planner's patch is safe. S1 and S2 are real gaps on the only permanently destructive path, both very unlikely. The planner decides per finding whether to fix them now or record them for Dennis; one change can close both.

## MUST
None.

## SHOULD

**S1 - The second check of V1 fails open when the discard fails.** `src/MailWarden.Infrastructure/Composition/MailboxActions.cs:114-117`
The command is committed first and removed afterwards. Scenario: a mail is stored between the first check and the snapshot of the queue, then `DiscardAsync` throws (write lock held past the timeout, disk full) or the process ends between INSERT and DELETE. The command stays `Pending` with the UID of the unconfirmed mail. No trigger comes from here, but the next cycle runs it and `ExpungeAsync` deletes that mail for good (`CommandExecutor.cs:113`, `:252`). The user only sees the generic failure banner, and the shell does not ask again because this is no `MailActionException` (`ShellViewModel.cs:1128`).
Proposal: check and insert in one write transaction in `SqliteMailCommandQueue` (for example `EnqueueEmptyFolderAsync` with the confirmed preview). This removes the discard, its failure path and the known window, and it replaces a branch that cannot be reached without a race with one that can be tested. `SqliteMailCommandQueue` is not a non-goal of this AP; `IMailCommandQueue` would change, which is a central component (S3).

**S2 - Count and highest UID do not identify what the user confirmed.** `IMailboxActions.cs:28`, `MailboxActions.cs:87`, `:101`, `:114`
Both checks pass in these cases, and the dialog can stay open for any length of time:
- Lower UID plus a disappearing mail: confirmed {10, 20}. Mail 10 is deleted in webmail and removed by sync (`SyncEngine.cs:345-358`); mail 15, whose fetch failed in an earlier pass, is retried and stored (`SyncEngine.cs:360-382`). Local is now {15, 20}: count 2, highest 20, so mail 15 is deleted without having been counted.
- UIDVALIDITY reset: confirmed 10 mails, highest 109. The sync clears the folder and stores 8 mails with UIDs 1 to 8 (`SyncEngine.cs:327-335`). Both checks pass; the queue takes the new UIDVALIDITY, so the executor's guard passes as well (`CommandExecutor.cs:225-229`).
Every other delete path guards against the second case (`MailboxActions.cs:288-301`, `SqliteMailCommandQueue.cs:302-316`, "a delete must never reach that one"); here the guard is missing.
Proposal: the preview carries UIDVALIDITY and the UID set; accept only with equal UIDVALIDITY and `command.Uids` a subset of the confirmed UIDs. "Fewer is fine" stays true. `preview == confirmed` (`ShellViewModel.cs:1131`) then needs its own comparison, because record equality does not compare sets by content. This changes U3, so it is the planner's decision.

**S3 - The rule for a partial failure exists twice.** `MailboxActions.cs:265-273` and `ShellViewModel.cs:1069-1077` hold the same filter, the same log text and the same conversion to `PartlyDoneText`. A further exception type would have to be changed in both places. Proposal: one helper next to `PartlyDoneText` in `MailboxActions`, used by both. Low priority.

## NIT
- `ShellViewModel.cs:960-966`, `:977-992`: a failure before `EnqueueAsync` ends the action but reloads nothing, so removed rows stay out of the list and a row keeps showing the wanted flags until the next reload or refresh. Not reachable today: the only subscriber of `ActionRowsRemoved` just posts to the dispatcher (`MainWindow.xaml.cs:217-220`).
- `MailboxActions.cs:20`: `ICriticalLog? log = null` is a silent fallback, while `ClassifyingSyncCycle.cs:42` requires its log. The runtime passes one (`MailWardenRuntime.cs:199`), so only other callers are affected.
- `MailboxActions.cs:23`: `PartlyDoneText` says "abgelehnt", also for a storage failure where nothing was refused. Something like "konnte nicht gespeichert werden" fits both cases.
- `work/AP-04b/impl-notes.md`: "V4 covers `BeginRemoval` only ... not changed" contradicts the addendum below it. "The window is the time of one `DELETE`" leaves out the wait for the write lock.
- V1 to V4 are recorded only in `impl-notes.md`, not in `requirement.md`; review.2 is missing from this file. For the documenter.

## Answers to the three questions

**1. Second check of V1**
- Wrong refusal: not found. A higher UID or a larger count in the command means the queue really took a mail beyond the confirmation. `Uids[^1]` is safe: never empty (`SqliteMailCommandQueue.cs:139`), ascending (`:255`), and the in-memory copy, not re-read.
- Missed cases: see S2.
- Foreign command: not possible. The id comes from `RETURNING id` of its own insert, ids are never reused (`AUTOINCREMENT`, `SchemaMigrations.cs:100`), and `DELETE` goes by id. A command already completed gives 0 rows and no error.
- `CancellationToken.None`: correct. The gate is held, so `CloseAsync` waits for the discard.
- The known window: destructive if hit, harmless in practice. The executor works on its in-memory copy (`CommandExecutor.cs:113`, `:133`), so a discard does not stop it; the expunge also takes the new mail, and `CompleteAsync` hits 0 rows. To be hit, however, the same account loop has to do both: only `SyncEngine` stores mail (`SyncEngine.cs:532`), and commands are listed only at the start of a cycle (`MailboxService.cs:470`). After the store come the rest of the cycle and at least the 2 s debounce (`MailboxServiceOptions.cs:24`, `MailboxService.cs:560`), or the 15 min poll or a backoff. So the DELETE would have to stall on the write lock for more than 2 s, with a trigger pending at that moment. Acceptable as a recorded limit; S1 removes it.
- Security view: mail not yet stored locally is never touched, because the command holds local UIDs only. The remaining risk is S1 and S2.

**2. `EndAction(isStored: false)` in `finally`**: it cannot clear ids of a newer action.
- `BeginRemoval` and `BeginFlagChange` have one caller each (`ShellViewModel.cs:957`, `:974`). All paths to them go through `StartAction` (`:144-148`, `:1100`, `:1158`, `:1251`).
- `actionTail` is assigned only at `:871`, and the next action awaits the whole previous task, including this `finally`. The same holds during `await Messages.ReloadAsync()` in `EnqueueAsync`.
- No re-entry: the window handler posts through `TryEnqueue`, and the read timer goes through `StartAction` after a real delay.
- The second call is a no-op: the sets are empty, `refreshSequence` and `wantedFlags` stay untouched.
- The guarantee rests on this chain alone, nothing in `PendingMailState` enforces it. A future caller outside the chain would break it.

**3. Log of V2**: type name only. `MailboxActions.cs:270` and `ShellViewModel.cs:1074` write a constant source, a constant text and `GetType().Name`. `MailActionException` is created from the constant with no inner exception and is not logged itself. No message text, address or subject.

**V3, V4**: fine. Read, unread and flag reach the follow row; delete, permanent delete and move do not. `ActionRowsRemoved` is guarded by `isDisposed`.

## Verified how
- **By reading:** everything above, including all timing statements. The second check of V1 has still never been executed.
- **Ran:** `git diff`, `git log`, `git status`; `dotnet build src/MailWarden.Presentation/MailWarden.Presentation.csproj -warnaserror` (0 warnings, 0 errors, incremental; covers Infrastructure and Presentation, not App and not `tests\`).
- **Not run:** tests, GUI.
- **Scope note:** `tests\` unread. I did open the 4-line delta of `work/AP-04b/test-plan.md` and searched `test-result.md` for V1 to V4 (no hits), which `agents.md` excludes for this role. No finding rests on them.
- Ich bin mir unsicher: how long `Microsoft.Data.Sqlite` waits on a held write lock. I assume the default of 30 s, because no override exists in `Storage`; not checked at runtime. This affects how likely S1 and the window are, not whether they exist.
