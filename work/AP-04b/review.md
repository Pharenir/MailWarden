
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

# review.4

Delta `d565e24..6e6428d` (`src`, `work/AP-04b/impl-notes.md`), 2026-11-01. Line numbers as of `6e6428d`. Spec: W1 to W6 of `requirement.md`.

**Verdict: APPROVE.** No MUST. W1 to W6 do what they are meant to do. "Ordner leeren" can no longer expunge a mail outside the confirmed set; S1 to S3 of review.3 are closed. One SHOULD, which is a record for Dennis and no code change. S3 is still required, because `IMailCommandQueue` changed.

## MUST
None.

## SHOULD

**S1 - Record the remaining gap of U2 completely, it is larger than `impl-notes.md` says.** `work/AP-04b/impl-notes.md`, "Remaining limits"; `MailboxActions.cs:102`
The note only names a classifier move stored between the check and the transaction. The larger window is a move stored after the delete (order C in answer 4), which no check in `EnqueueEmptyFolderAsync` can catch. Proposal: a `TODO.md` entry for Dennis and AP-06 with orders B and C. No code change in this round.

## NIT
- `MailboxActions.cs:31`: `MailArrivedText` says more than is known, see answer 5. One-line change if another edit happens anyway; no reason for a round of its own.
- `ShellViewModel.cs:1098-1099`: the comment names only "a mail has arrived"; the refusal now also covers another UID in the same count and a changed UIDVALIDITY.
- `MailboxActions.cs:23`: the summary of `PartlyDoneText` still says "of a rejection", while W6 made the text cover storage failures. `src` now uses the constant only in this class; it can become private unless tests need it (`tests\` unread).
- `MailboxActions.cs:173`: a method named `TryGet...` that also writes to the log, called from an exception filter. It is documented, and the old behaviour is kept. A log that throws inside the filter would make the original failure escape instead of the partly-done text; `CriticalFileLog.Write` swallows everything except `OutOfMemoryException`, so this has no effect today.
- `work/AP-04b/impl-notes.md`, fix.4: no statement on a build with `-warnaserror` of this delta, unlike fix.3. Nor did this review build. The S3 run has to cover it.

## Answers to the six questions

**1. Transaction: correct.**
- `SqliteMailCommandQueue.cs:140` takes the write lock before the first read (`:142`, `:153`). `folderStore` and `messageStore` hold the same `SqliteDatabase` (`:37-38`).
- The three helpers open no connection and no transaction: `SqliteFolderStore.GetAsync` (`:36-50`), `SqliteMessageStore.GetUidFlagsAsync` (`:295-318`), `InsertAsync` (`:378-432`). All use `Connection.CreateCommand()`, which takes over the open transaction (checked in the library, see "Verified how").
- A sync on its own connection waits: it can only write after the commit. A reader there sees nothing of it before the commit, which also closes the V1 window of review.3.
- The connection is used by nothing else: `MailWardenRuntime.cs:175`, `:199`, and the gate covers `MailboxActions.cs:99-120` as a whole. No second transaction can be open.
- Failure paths, each ending in the rollback of `await using` before any catch runs: wrong role or never synced (`:142-146`), changed UIDVALIDITY (`:150`), UID not confirmed (`:163`), UID 0 (`:167`), insert failure (`:168`), commit failure or cancellation (`:170`), cancellation during a read. The null path (`:158`) rolls back an empty transaction. `BeginTransaction` itself failing after the wait for the lock ends as `StorageException` with nothing begun. A crash before the commit leaves nothing.

**2. Subset check: covers S1 and S2 in full.**
- S1 is gone by construction: no insert before the check, no discard, no failure path after a commit.
- S2, first case: confirmed {10, 20}, local {15, 20}: `:161` refuses. New preview differs, the dialog asks again.
- S2, second case: reset, confirmed under the old value: `:148` refuses, also when the new UIDs happen to be a subset.
- Reset order (`SyncEngine.cs:327-342`): the rows go in one transaction (`SqliteMessageStore.cs:266`), then the sync state, then UIDVALIDITY, then new mail. So the states are (old, all old), (old, empty), (new, empty), (new, growing), with no mixed state.
- A preview reads the value first (`MailboxActions.cs:88`) and the UIDs later (`:89`), so all pairs it can produce are:
  - (old, all old): a correct pair. Reset finished before the insert: refused. Not yet started: stored with the old value, and the executor refuses before anything is sent (`CommandExecutor.cs:226-229`), the server already has the new value. Failed entry, no mail touched.
  - (old, empty): no dialog (`ShellViewModel.cs:1109`).
  - (old, new UIDs): the dialog shows the new count, the queue refuses by value, the dialog asks again.
  - (new, new UIDs): a correct pair.
- (new, old UIDs) is impossible, as the comment at `MailboxActions.cs:86-87` says. A preview during a reset cannot pass wrongly.
- Queue during the state (old, empty): null, no command. Changed value with nothing local: refusal, new preview with count 0, the loop ends without a banner. Fine for W-d in the shell.

**3. Trigger and what the old checks did: nothing lost.**
- Stored means triggered: `MailboxActions.cs:113-116`. After the commit nothing checks the token any more; a commit that is cancelled does not happen. `TriggerAccount` only sets a flag (`MailboxService.cs:259-262`). A service that is stopped or waiting keeps the command for the next cycle.
- Gate and `isClosed`: unchanged, `RunSerialisedAsync` wraps it all.
- `EmptyFolderChangedException` derives from `Exception`, so `CallQueueAsync` (`:228-242`) leaves it alone and `:108-111` turns it into the refusal. `StorageException` passes through as well and goes to the failure path of the shell, with rows reloaded and no second question. `ArgumentException` becomes `RejectedText`.
- An `InvalidOperationException` from the library (nested transaction) would read "kein Papierkorb". Not reachable under the gate; the wrong text is already an item for `TODO.md` from fix.2.

**4. `RequireEmptiableFolderAsync` outside the transaction: this does NOT have to move into it.**
- User moves cannot interleave: same gate, one `MailboxActions` instance (`MailWardenRuntime.cs:199`).
- The classifier can only do so for Junk, with one action: `TrustedContact` to Inbox (`ClassificationRouting.cs:33`). Trash is never a source (`ClassificationPass.cs:146`).
- Not reachable today: the default policy is RecordOnly (`ClassificationPolicy.cs:24`), and Act rests while there are no contacts (`ClassificationPass.cs:131`). No code in `src` adds contacts yet. From AP-06 on it is reachable.

Let X be the rescued mail:

| Order | Queue | Executor | X |
|---|---|---|---|
| B: move stored between `MailboxActions.cs:331` and `SqliteMailCommandQueue.cs:140` | Move n, delete n+1 | Move succeeds (`CommandExecutor.cs:248`), the expunge does not find X (`:252`) | safe in the Inbox |
| B, move refused by the server | same | Backoff, and the run goes on (`:167-170`), so the delete expunges X in the same run | **lost** |
| B, network failure | same | The run ends (`:162-164`); the move waits 1 min. A retry before that runs the delete alone (`SqliteMailCommandQueue.cs:223`) | **lost** if so |
| C: move stored after the delete, window `ClassificationPass.cs:237` to `:456` | Delete n, move n+1 | The delete runs first; the move finds nothing. `RequireSourceAsync` lets the move pass, X is still local | **lost** |

- All of these lose only a mail in the confirmed set, because `:161` refuses anything else. So W1 holds: nothing the user did not count goes. What is broken is U2 ("on its way out"), for a classifier rescue only.
- Window B is the time between two statements, longer only while waiting for the lock. Window C is the sync and classification between storing X and enqueuing its move: seconds with many new mails, enough for a dialog. Moving the check closes B and leaves C.
- A real fix needs a rule at execution or in `EnqueueMoveAsync`, for instance: a delete leaves out UIDs with a pending move. That is a product decision: does a rescue beat a confirmed delete? For Dennis, see S1.

**5. `MailArrivedText` after a UIDVALIDITY change: worth one line, not a round.**
- The user rarely sees it: a changed value gives a different preview, so the dialog asks again (`ShellViewModel.cs:1123-1129`). The text reaches the banner on the third refusal, or if the state has flipped back to the same preview.
- It is also imprecise for the older UID of S2, which is no new mail on the server. Harmless, because the next attempt works.
- Proposal if edited anyway: "Der Ordner wurde nicht geleert, weil sich sein Inhalt seit der Bestätigung geändert hat." True in all cases. Tests may hold the old text (`tests\` unread).

**6. `TryGetPartlyDone` and the comparison: correct, duplication gone.**
- Exclusion list only at `MailboxActions.cs:181`, log text only at `:188`. Both callers (`:288-292`, `ShellViewModel.cs:1069-1073`) keep their own condition and log source. `finally` in `EnqueuePerFolderAsync` still triggers the stored accounts.
- `HasSameContent` compares value and content (`IMailboxActions.cs:38-42`). Compare `==` is gone from `src`; the record comment warns about it. `Count` comes from the set, so the two cannot disagree. Same preview: stop with the error. Different: ask again, at most 3 questions. As W4 says.
- W6: the new text fits both a refusal and a storage failure.

## Verified how
- **By reading:** everything above, including all orders and timing. No race was run.
- **Ran:** `git diff`, `git log`, `git status`. PowerShell reflection on `Microsoft.Data.Sqlite.dll` 9.0.22 (IL only, no database opened): `CreateCommand` calls `set_Transaction` with `get_Transaction`; a second `BeginTransaction` throws; the constructor holds `BEGIN IMMEDIATE`.
- **Not run:** tests, GUI, build (as asked). The runtime probes in `impl-notes.md` are the builder's own statement; I could not repeat them.
- **Scope note:** `tests\` unread. A repo-wide search for `HighestUid` also returned lines of `work/AP-04b/test-plan.md` and `work/AP-07c/test-plan.md`, which `agents.md` excludes for this role. No finding rests on them.
- Ich bin mir unsicher: whether every IMAP server ignores a missing UID in `UID MOVE` and `UID EXPUNGE`. RFC 3501 says so for UID sets, but it is not checked against a server. It decides whether the safe row of order B completes or records a failure; X is safe either way, the other mails of that command would be retried.
- Ich bin mir unsicher: the branch that picks `BEGIN IMMEDIATE` for `deferred: false`. I saw the strings and the call to `IsolationLevel`, not the branch itself. It matches `BeginTransaction(bool)` as documented and the builder's two-connection probe.
- Ich bin mir unsicher: how long the wait for the lock lasts (not measured). It affects how long a user waits and window B, not the result.
