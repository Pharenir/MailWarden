
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

# review.5

Delta `3f1d0f0`, `cbcb564` and merge `b768566` as they sit in `1b89d68`, 2026-11-01. Line numbers as of `1b89d68` (`src` unchanged since the merge).

**Verdict: APPROVE.** No MUST: I found no scenario with a wrong folder, a wrong final number or a false log line. S1 is a one-place speed-up that X3 "the moment" needs on a big mailbox. S2 and S3 need no builder round, but they must be done before the merge to `main`.

## MUST
None.

## SHOULD

**S1 - The recount is slow because of one query plan, not because of the id list.** `src/MailWarden.Infrastructure/Storage/SqliteMessageStore.cs:434-438`, hot since `NavigationViewModel.cs:310-324` and `ShellViewModel.cs:840-846`
Scenario: mailbox of 180,000 mails, one unread mail, Del. The row goes at once, the number about 1.4 s later, because `ApplyTree` applies only at the end of the pass. X3 says "the moment". Every further Del in that time restarts the pass, so while tapping the number does not move at all. Del on a read mail costs the same pass for an unchanged number (X-k), and so does each cycle that finished a removal (a second pass via `ReloadAsync`, `PendingMailState.cs:124`).
Cause: a role view of one account filters `m.account_id`, and the planner then scans all of `messages` (`SCAN m`), even for an empty Outbox: about 65 ms each, 18 such entries, about 85 % of the pass.
Proposal: in the role branch filter `f.account_id = $account_id` instead. The composite foreign key of `messages` makes it the same set. Measured below: 1,320 ms to 167 ms without hidden ids, 1,645 ms to 291 ms with 60,000, same counts. Ich bin mir unsicher: that `e_sqlite3` picks the same plans as the Python build; `EXPLAIN QUERY PLAN` on the real driver settles it. The planner may also postpone this until Dennis has seen the delay on his copy; then it belongs in `TODO.md`.

**S2 - X4 rests on a manual test that was run with the bar on.** `src/MailWarden.App/MailWebViewHost.cs:177-178`, `work/manual-tests.md:246`
Case 32 saw the hint with `IsStatusBarEnabled = true`. Whether `StatusBarTextChanged` still fires with `false` is not proven: the SDK doc (1.0.3179.45) says only "The developer must create the status bar and set the text", and `impl-notes.md:78` carries the same doubt. If it does not fire, a link target is invisible, which is the risk of review.1 S1. `manual-tests.md:246` still asks for tooltip **and** status bar and nothing asks for the bar off. Proposal: a new step for Dennis, link with a differing text, bar off, hint shows the true target. Add X-h and X-i to X-m to the same list; they are not in it.

**S3 - Documents contradict the code (for the documenter).**
- `README.md:77` tells the user the status bar stays on. X4 turned it off.
- `PLAN.md:251` ("bleibt vorerst an") and `PLAN.md:255` ("**und** in der Statusleiste") contradict X4.
- The AP-04b table of `PLAN.md` has no line for X1 to X4; its status still says S3 2348/2348, review.4.
- `TODO.md:95` and `TODO.md:115` are stale, and `README.md:42,53` do not mention the right click in the tree or the number.

## NIT
- `src/MailWarden.Core/Storage/IMessageStore.cs:48-49` still lists "Role/AccountId/FolderId"; `ExcludedMessageIds` counts too.
- `src/MailWarden.App/MainWindow.xaml.cs:502-503`: the comment expects a parent `TreeViewItem` above the entry. As far as I remember the control holds its items flat (`impl-notes.md` assumes this as well), then nothing bubbles to a parent. `Handled = true` is harmless either way. Ich bin mir unsicher: not checked at runtime.

## Answers

**1. Right click: no path empties another folder than the one clicked.**
- `view` is read once from the entry and captured by the menu (`MainWindow.xaml.cs:508`, `:514`). A refresh that replaces the items does not touch the record, and `CanEmptyFolder` is not read again.
- Ids cannot come back: `folders.id` and `messages.id` are `AUTOINCREMENT`.
- Stale true: `EmptyFolderAsync` reads the folders again (`ShellViewModel.cs:1126`). Junk group after a second account got a Junk: null, nothing happens. Role changed: null, nothing happens. Folder gone: same. Stale false: no menu until the next refresh, which is the safe side.
- `ItemFromContainer`: the event and `ApplyTree` both run on the UI thread, so they cannot interleave. A wrong answer would also have to pass the question, which names folder, account and count (`UiText.cs:220-225`) and defaults to cancel. Null means no menu. Ich bin mir unsicher: that it answers for child entries and for a `TreeViewItem` that is the root of the template. Only X-h shows that, and the failure is visible and safe.
- Handled and menu: no other `ContextFlyout` above the tree (`MainWindow.xaml:163` is the list). Right click and context key are both covered; `TryGetPosition` false shows the menu at the entry.
- Same path as the toolbar: one rule (`MailView.cs:53-60`), no second function for the task.

**2. `ActionRowsRemoved`: nothing breaks.**
- Delete, permanent delete and move return before `RemoveRowsAsync` when no row is left (`ShellViewModel.cs:1049`, `:1094`, `:1199`). Only the empty path can pass no rows.
- Toolbar empty with rows: as before. Toolbar empty with none loaded: the focus stays on the button, which stays visible. Right click on the folder in the list: rows go, the focus moves to the list, as with the toolbar. Search box: `FocusMessageList` keeps out.
- `MainWindow.xaml.cs:90` is the only subscriber in `src`; the doc at `ShellViewModel.cs:325` is now exact.
- `Messages.ReloadAsync` at `:1163` with `HasMore` on an unrelated list merges in place and keeps the selection by id (`MessageListViewModel.cs:255-260`), so X-e holds.

**3. X3.**
- *SQL:* `m` is the alias in both shapes (`:172`, `:429`); folder, role and role plus account work. `m.id` is the primary key, and the JSON holds only formatted `long` values, so no NULL can turn `NOT IN` into nothing. Empty and null skip the clause. Parameterised.
- *Search:* `SearchAsync` has no `MessageQuery`, so it is not affected. `PermanentDeleteWatcher.cs:94` and `MailView.ToQuery` pass nothing.
- *`ListAsync`:* it shares the filter and honours the member, but nobody passes it there. Fine, and simpler than a second filter. Anyone who does must know that the offset logic of `MessageListViewModel` (`hiddenRowsRead`) assumes hidden rows are read.
- *Double subtraction:* none, as nothing is subtracted. The executor deletes the rows (`CommandExecutor.cs:351`) before `CompleteAsync` (`:184`), so a command that has left the queue has no row left to be counted again. If the local delete fails (swallowed on purpose), number and row come back together until the next sync.
- *Order, Del:* `BeginRemoval` starts pass A, `EndAction(true)` raises nothing and the set stays equal, so A stays valid. An older queue read is dropped by the sequence.
- *Order, refusal:* `EndAction(false)` starts pass B, which replaces A. The number returns (X-j).
- *Order, empty:* pass, store, `RefreshAsync`, event, newer pass (X-l). `BeginRemoval` with no rows raises nothing, correct.
- *Order, cycle:* pass with the old set (right, rows gone), then `ReloadAsync` reads the queue, event, second pass (same numbers; a failed command brings its number back).
- *Partly stored:* `EndAction(false)` clears the stored ids too, until the queue read of `ReloadAsync` brings them back. The number can rise for that moment and ends right, as the rows do.
- *No missed event:* every change of the union passes an event (`PendingMailState.cs:124`, `:166`, `:214`); the reverse only costs a spare pass.
- *Concurrent passes:* newest wins by generation (`NavigationViewModel.cs:146-147`, `:168`, `:184`). A replaced pass neither applies nor logs.
- *After `Dispose`:* `ShellViewModel.cs:842` and `NavigationViewModel.cs:141` keep out; `Close` bumps the generation, so a late `ObjectDisposedException` is not logged (`NavigationViewModel.cs:190`). `RunObservedAsync` catches the rest, except `OutOfMemoryException` as everywhere.
- *Startup:* the guard `Items.Count > 0` loses nothing. The queue is read before the first pass (`ShellViewModel.cs:349-350`), `ApplyStatus` ignores unknown accounts until the tree exists, and the first load starts from `ApplyTree`, when `Items` is filled.
- *Cost, freeze:* no freeze. Counts and JSON run on the thread pool (`MailboxReadModel.cs:110`). On the UI thread: `HiddenMessageIds`, about 2 to 5 ms at 60,000 ids per pass (estimate), the hops, and `ApplyTree`, which raises only for changed values.
- *Cost, queue:* no build-up. A pass holds at most one statement in the gate; the next one is cancelled in `WaitAsync` or `Task.Run`. Writes run on the own connection of `MailboxActions`; WAL keeps them from waiting for reads.
- *Holding Del is not a load case:* `MainWindow.xaml.cs:278` ignores repeats (`WasKeyDown`, `PLAN.md` row 12), so holding acts once. The real case is tapping or the toolbar.
- *Tapping:* `DeleteAsync` reads the roles through the same gate before `BeginRemoval` (`ShellViewModel.cs:1054`, `:988`), so the next row goes up to one count later: at most about 90 ms at 180,000 mails as built, far less with S1.
- *Id list:* 60,000 ids add about 8 ms per count that reaches a row, plus about 4 ms of serialising and about 1.2 MB in the large object heap per count (estimate, about 50 MB per finished pass). Noticeable, not a defect. The size is reached only while a very big empty is pending.

| 180,000 synthetic mails, 40 entries | One pass |
|---|---|
| As built, no hidden ids | 1,320-1,360 ms (worst count 81-87 ms) |
| As built, 60,000 ids | 1,645 ms (worst count 89-91 ms), plus the serialising |
| S1, no hidden ids | 167 ms |
| S1, 60,000 ids | 291 ms, plus the serialising |

**4. X4: nothing else relies on the bar.** `MailWebViewHost.cs:190-196` to `MainWindow.xaml.cs:333-336` is the only use; only the manual proof is missing (S2).

**5. Merge `b768566`: consistent.**
- Conflicts only in `PLAN.md` and `README.md`, no markers left, and both AP sections are complete. Both sides changed `MailWardenRuntime.cs`, which merged cleanly without conflict hunks. No AP-06 file in `Presentation` or `App`; `SqliteMessageStore` and the schema have no overlap.
- review.4 said the classifier rescue becomes reachable from AP-06 on. It is still closed: `Act` is off (`PLAN.md`, AP-06), and `TODO.md:280` keeps the decision for Dennis.
- **Condition:** no S3 has seen this combination. `TODO.md:80` counts the S3 of AP-06, `PLAN.md` the 2348 of verify.4. Merging to `main` needs a green S3 on the merged head.
- `tests/test-map.json` changed on both sides; I did not open it (`tests\`), the tester should check it.

## Verified how
- **By reading:** everything above, including orders and races. No race was run.
- **Ran:** `git show`, `git diff`, `git log`, `git status`; `dotnet build MailWarden.sln -warnaserror` (0 warnings, 0 errors; wrote only `bin`/`obj`); a lookup in the WebView2 XML doc.
- **Ran, measurement:** a Python script in the session scratchpad. SQLite 3.50.4 from Python, not `e_sqlite3` and not C#. File database with the tables and message indexes of `SchemaMigrations` (no FTS, no foreign keys), 125 MB, WAL, default cache, warm OS cache, artificial data, 3 accounts with 12 folders each. Cold cache and a real mailbox are not measured; both will differ. The row "S1" compared the counts of all 40 entries: equal.
- **Not run:** tests, GUI, the app, S1-S3, a real database.
- **Estimated, not measured:** the UI-thread share, serialising in C#, the heap figures.
- **Scope note:** `tests\` unread. `git show --stat` output listed test file names, no content.
- Ich bin mir unsicher: how large the real database is. It decides whether S1 is noticeable; time grows about linearly with the mails.
