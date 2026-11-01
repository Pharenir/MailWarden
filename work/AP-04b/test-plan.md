# AP-04b - test plan (author.0, written from `requirement.md` only; nothing was compiled or run)

Tags: all tests `feature:ui`; D1 tests also `feature:sync` and `feature:storage`; smoke: one per file group (marked below).
Async tests use `[Fact(Timeout = TestTimeouts.PerTestMilliseconds)]` (tripled/doubled only where 1,000-2,500 rows are seeded).

## Case -> test

| Case | File (tests project) | Tests |
|---|---|---|
| 1 | Infrastructure `Composition\MailboxActionsTests.cs` | SetSeen (smoke, +add/remove theory), SetFlagged theory, MoveToTrash, DeletePermanently (also from Trash), Move, several folders, several accounts, empty list |
| 2 | same | 2,500 mails (3 commands), boundary theory 1,000 / 1,001 |
| 3 | same | MoveToTrash from Trash, unknown target, other account's target, stale message of unknown folder, mixed Trash+Inbox selection |
| 4 | same | CountForEmpty + EmptyFolder theory (Trash, Junk), count follows local state, Inbox rejected, unknown folder, empty folder = no command/no trigger, 2,500 in Junk |
| 5 | same | pending + failed with resolved ids, gone UID skipped, empty / all accounts |
| 6 | same | discard pending (+ unknown id no-op), discard failed |
| D1 extra | same | parallel calls on one instance ("serialised") |
| 7 | Presentation `ShellActionsTests.cs` | delete hides at once + next row selected + pane + reload (smoke), last row, only row, confirmed delete, other views, positive control, rejected delete keeps row |
| 8 | Presentation `FailedActionsTests.cs` | failed delete + discard (smoke), discard pending, banner count follows discards, fixed text per kind, pending not failed |
| Q1 AP-03c | same | Shift+Del reached Trash: banner once, control without copy, control with other Message-ID |
| 9 | `ShellActionsTests.cs` | no, no answer, yes (count in text, no private text), no selection |
| 10 | same | Del in Trash asks then permanent, answered no, Del outside Trash does not ask |
| 11 | same | empty Junk (snapshot count 7 vs 4 stored, role word, marker check), no / no answer theory, Trash, Inbox disabled; marker check (`marker_subject`, `marker_sender`, `example.test`) in every confirmation text |
| 12 | same | mark read on 3 rows, mark unread + failed command -> server truth, flag toggle both ways |
| 13 | Presentation `ReadOnSelectTests.cs` | default off (settings + shell), off: select + wait 10 min each row -> no fake call, no controller call; stored value applied; change saved keeping other values |
| 14 | same | 1.4 s then leave, switch mail restarts clock, 1.6 s once (smoke), already read, multi-selection, switch off during wait, Dispose cancels |
| 15 | `ShellActionsTests.cs` | same account, other account refused before queue, multi-selection across accounts disabled (+ control) |
| 16 | same | SelectedRows 0 / 1 / 2 / 0: pane + CanExecute of five row commands; commands without selection |
| 17 | Presentation `SearchViewModelTests.cs` | result + label + limit 500 (smoke), no hit, blank theory, blank without earlier search, stale, blank discards late result, syntax-character theory, text unchanged, hidden row, no log, select result row, answer after Dispose |
| 18 | Presentation `WindowSettingsTests.cs` | defaults (smoke), missing keys, all keys, single keys, boundaries, invalid theory (key named, no value), null, round trips, saved keys, shell keeps window values, monitor helper (in/out/partial/none) |
| 19 | Presentation `HoverLinkTextTests.cs` | set (smoke), cleared theory, blank/control only, bidi/control removal, 10,000 chars <= 501, 500 kept, PropertyChanged |
| 20 | `ShellActionsTests.cs`, `ReadOnSelectTests.cs`, `SearchViewModelTests.cs` | action blocked in flight + Dispose, commands after Dispose, confirmation answered after Dispose, read-on-select timer on Dispose, search answering after Dispose |
| 21, 22 | manual (Dennis) | not automated |

Test support added/changed (all under `tests\`): `Presentation.Tests\TestSupport\FakeMailboxActions.cs`, `ActionsTestHelpers.cs`, `Fakes.cs` (read model: `SearchAsync`, `SetFlags`, `MoveMessage`, `Find`, `messageId` on `AddMessage`), `ShellHarness.cs` (actions fake, confirmation answers, `SeedInboxAndTrash`); `Infrastructure.Tests\TestSupport\ActionsHarness.cs`; `tests\test-map.json` (entry for `MailboxActions*`).

## Assumptions made where the contract is silent (reading chosen)

- A1 `UiSettings` is a record; new members are settable via `with`, and the old positional constructor still compiles (existing tests use it).
- A2 `FakeMailboxActions` keeps its own pending list: the shell sees enqueued commands through `ListPendingAsync`. Hidden kinds: MoveToTrash, DeletePermanently, Move, EmptyFolder (as DeletePermanently); Failed commands are not hidden; SetFlags never hides.
- A3 Rows: `MessageRowViewModel.IsUnread` / `IsFlagged` follow the wanted state at once (case 12).
- A4 After a delete the next row (the one below, or the one above for the last row) is selected.
- A5 `ToggleFlagCommand` flags when the (uniform) selection is unflagged, unflags when flagged. Mixed selections not tested.
- A6 Read-on-select is timer based on the injected `TimeProvider` (`CreateTimer` or `Task.Delay(…, provider)`); it counts from showing the mail, tested without leaving.
- A7 `EmptyFolderCommand` acts on the folder of the current view (Folder view), parameter null; enabled only in Trash/Junk.
- A8 A queue rejection (`MailActionException`) is shown as a fixed banner, no private text, no log line.
- A9 The Q1 hint is raised on the first reload after the command is gone from `ListPendingAsync` and a mail with the same `MessageId` sits in the Trash; shown at most once (banner afterwards equal or cleared).
- A10 `HoverLinkText` is a settable string property that cleans the assigned value (SafeDisplayText.Clean + Shorten 500); clearing gives null or empty.
- A11 `PendingActionRow` has `CommandId` and `Text`; `DiscardFailedActionCommand` takes the row as parameter; `Banner` is null when nothing is to report.

## Questions to the planner

- Q1 D1 implementation: class name and constructor. Assumed `new MailboxActions(SqliteDatabase database, Action<Guid> triggerAccount, TimeProvider clock)` in `ActionsHarness` (one place to fix). How is the trigger passed (callback, `MailboxService`)?
- Q2 `ShellViewModel` constructor: assumed `(readModel, controller, actions, dispatcher, settings, log, clock, documentBuilder)` in `ShellHarness` (one place to fix). `MailActionException(string)` constructor assumed (used once in `ShellActionsTests`).
- Q3 Names not in the contract and assumed: `ShellViewModel.Search`, `PendingActionRow.CommandId/Text`, `MessageListViewModel.SelectedRows` element type, `ReadingPaneViewModel.HoverLinkText` setter. The move-target menu list (case 15 "target menu") has no member in the contract, so only `MoveCommand` with a folder id is tested; please name the property if a target list should be tested.
- Q4 Case 18 "pure helper" for a position off every monitor has no name. Assumed `WindowPlacement.IsOnAnyMonitor(int x, int y, double width, double height, IReadOnlyList<(int X, int Y, int Width, int Height)> monitors)` in `MailWarden.Presentation`; rule: the top-left corner must lie on a monitor. If the real helper differs, only `WindowSettingsTests` (last four tests) needs adjusting.
- Q5 Case 13: the requirement says "Queue-Aufrufe" for case 13; no view-model test reaches the real queue (D2-D4 run against fakes). Fake calls and controller calls are checked instead.
- Q6 AP-04a case 41 test `View_models_expose_no_write_style_methods` forbids method names with Delete/Move/Mark/Flag/Empty on the four view models. The new members are commands (properties), so it should still pass, but any public method the builder adds with such a name breaks it (the test was written for read-only AP-04a and may need a deliberate update).
- Q7 `EnqueueEmptyFolderAsync` kind is not specified; the test only checks folder, UIDs and coverage, not `Kind`.

## fix.2 (author.1): cases 23-31

Not compiled here: the test branch has no fix.2 product code yet (`EmptyFolderPreview`, `PreviewEmptyAsync` are missing, expected compile errors until merged with the builder's fix.2).

| Case | Tests |
|---|---|
| 4 (changed) | `MailboxActionsTests`: preview theory (Count, HighestUid), preview follows local state, Inbox/unknown refused, empty without mails, 2,500 mails use `PreviewEmptyAsync` + `EmptyFolderAsync(id, preview)`; `FakeMailboxActions` has the same two-step API and refuses when a mail above `HighestUid` exists |
| 23 | `ActionRaceTests`: theory (MarkRead, MarkUnread, ToggleFlag, Delete) with gate in the fake; move; permanent delete with open confirmation; positive control in each (no call while gated / unanswered) |
| 24 | `ActionRaceTests.Delete_twice...` (gated and ungated) incl. positive control for a later Del |
| 25 | `MailboxActionsTests`: pending Move, pending MoveToTrash, Move in backoff block preview and empty; other pending commands (Move elsewhere, SetFlags here) do not |
| 26 | D1: mail arrives after preview (refused, fresh preview works), mail disappears (theory 1, 3); UI: asks again with new count, no on the second question, disappearing mail goes ahead after one question |
| 27 | D1: "teilweise" in the message exactly when a group was stored; UI: banner shows the wording of the exception |
| 28 | `ActionRaceTests`: 4+1 mails over 2 accounts name "2" and "5", one account does not, empty folder names cleaned label (bidi removed), no sender/subject |
| 29 | `HoverLinkTextTests`: long absolute target host first and bounded (<= 601), user info (`evil` host first), short target with user info keeps the real host; 10,000-char plain text still <= 501 |
| 30 | `ActionRaceTests.CanEmptyFolder_follows_a_search...`: true, false while searching and after, true after blank query, `CanExecuteChanged` raised |
| 31 | `ActionRaceTests`: Dispose while the delete waits in the role lookup (read-model `RoleGate`) stores nothing; control without Dispose stores |
| U8, U9 | not automatable here (key repeat and window placement live in `App`); manual |

New questions: Q8 Case 27 UI: the fake throws `MailActionException` whose text contains "teilweise"; if the view model derives the wording from a flag on the exception instead, tell me the member. Q9 Case 31 assumes the delete awaits `GetFolderRoleAsync` after the keystroke; if not, the test times out waiting for the gate (then the await point differs). Q10 The length bound after "host first" (U5) is not fixed; tests allow 501 + 100.

## fix.2 additions (author.2)
- D1 (`MailboxActionsTests`): late-loaded lower-UID mail refuses the empty and leaves the queue empty (+ positive control: fewer mails allowed); `StorageException` after a stored group reports `PartlyDoneText` (a SQLite trigger on `mail_commands` fails the second insert; + control with a single group). Q11: the log line with the type name is not asserted because the `MailboxActions` constructor used by the harness has no log; tell me how to inject one.
- UI (`ActionRaceTests`): read/flag/unread on the follow row work while the delete is saved; delete/permanent/move skip it (+ positive control afterwards); `ActionRowsRemoved` silent after Dispose (+ control without Dispose).
