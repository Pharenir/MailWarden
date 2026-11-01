STEP-RESULT: done (fix.3: V1-V4 done, see section "fix.3"; fix.2: U1-U9; implement.1 with the coordinator's precisions of 2026-11-01)

# AP-04b - Implementation notes (Builder, implement.1)

Branch `feat/ap-04b-ui-actions`. No package, no schema change. `SyncEngine`, `CommandExecutor`, `Infrastructure\Classification`
untouched. Nothing under `tests\` read or changed. `PLAN.md` has no row table for AP-04b yet, the documenter fills it.
`requirement.md` not changed (the coordinator updates it).

## Files
| File | Change |
|---|---|
| `src\MailWarden.Infrastructure\Composition\IMailboxActions.cs`, `MailActionException.cs`, `MailboxActions.cs` | new (D1) |
| `src\MailWarden.Infrastructure\Composition\MailWardenRuntime.cs` | `Actions`, own connection; closed first on dispose (D1) |
| `src\MailWarden.Infrastructure\Composition\IMailboxReadModel.cs`, `MailboxReadModel.cs` | `SearchAsync` (D3) |
| `src\MailWarden.Infrastructure\Storage\SqliteMessageStore.cs` | `GetUidIdsAsync` on the class only, `IMessageStore` unchanged, so fakes of it stay valid |
| `src\MailWarden.Presentation\ShellViewModel.cs` | commands, confirmation, failed actions, read on select, move targets, window bounds (D2, D4) |
| `src\MailWarden.Presentation\MessageListViewModel.cs` | `SelectedRows`, hiding, wanted flags, search source, rows change flags in place (D2, D3) |
| `src\MailWarden.Presentation\PendingMailState.cs` | new: hidden ids and wanted flags from `ListPendingAsync` (D2) |
| `src\MailWarden.Presentation\PermanentDeleteWatcher.cs` | new: Q1 banner, best effort (D2) |
| `src\MailWarden.Presentation\SearchViewModel.cs` | new (D3) |
| `src\MailWarden.Presentation\WindowPlacement.cs` | new: `IsOnAnyMonitor`, DIP conversion, record `WindowBounds` (D4) |
| `src\MailWarden.Presentation\ParameterRelayCommand.cs` | new: `RelayCommand` drops its parameter |
| `src\MailWarden.Presentation\UiSettings.cs`, `UiSettingsFile.cs` | new settings and keys, same strictness (D4) |
| `src\MailWarden.Presentation\ReadingPaneViewModel.cs`, `MailDisplayText.cs` | `HoverLinkText`, `HasHoverLink` (D6) |
| `src\MailWarden.Presentation\ShellBanner.cs`, `UiText.cs` | sources `ActionsRead`, `Actions`, `ActionRejected`, `DeleteReachedTrash`; texts |
| `src\MailWarden.App\MainWindow.xaml`, `MainWindow.xaml.cs` | toolbar, context menu, keys, multi-selection, dialog, search box, flyouts, link hint (D2-D6) |
| `src\MailWarden.App\WindowPlacementKeeper.cs`, `NativeDisplay.cs` | new: window size and position (D4); P/Invoke for scale and mouse |
| `src\MailWarden.App\MailWebViewHost.cs` | `IsStatusBarEnabled = false`, event `StatusTextChanged` (D6) |
| `src\MailWarden.App\App.xaml.cs` | passes `runtime.Actions`, applies and saves the placement |

## Public surface beyond `requirement.md` (for the tester)
- `ShellViewModel(readModel, controller, actions, dispatcher, settings, log, timeProvider, documentBuilder = null)`.
- `ShellViewModel`: `Search`, `MoveTargets` (`IReadOnlyList<NavigationItem>`, folder id in `View.FolderId`), `CanMove`, `CanEmptyFolder`,
  `HasFailedActions`, `WindowBounds`, `SaveWindowBounds(WindowBounds)`.
- `SearchViewModel.IsActive`: the list shows results. `SearchViewModel.ResultLimit` = 500.
- `PendingActionRow(long CommandId, string Text)`; `DiscardFailedActionCommand` takes the row, `MoveCommand` a boxed `long`.
- `MailboxActions(SqliteDatabase, Action<Guid>, TimeProvider)`, plus `CloseAsync()`; after it every call throws `ObjectDisposedException`.
- `WindowPlacement.IsOnAnyMonitor(...)`: positive overlap; touching an edge is none. `ToScreenPixels`, `ToDeviceIndependent`.
- `UiSettings`: the new members are optional at the end of the positional list, so old calls compile.
- `MessageRowViewModel` is now an `ObservableObject`; flags change in place on the same object, also on a reload.

## How it behaves (what a fake has to do)
- Every `LoadAsync`, `ReloadAsync` and search calls `ListPendingAsync` first; `LoadMoreAsync` does not. A fake must list a command after
  its enqueue for the row to stay hidden across a reload: a reload with an empty pending list brings the row back, as the contract says.
- Wanted flags live in memory, because `PendingAction` has no flags. They hold until the first refresh that does not list a `SetFlags`
  command with that id. After a restart a pending `SetFlags` shows the stored flags until it has run.
- Hidden means status `Pending` with kind `Move`, `MoveToTrash` or `DeletePermanently`; `Failed` hides nothing.
- Actions run one after the other. Up to the first real wait they run synchronously inside `Execute`: flags at once; a delete after the
  role read, a move after the folder read, a confirmation path after the answer.
- A queue rejection stops the call; commands stored before stay, their accounts are triggered once per call in `finally`. The rows of
  the failed part come back through a reload.
- Without a subscriber of `ConfirmationRequested`, with no answer until `Dispose`, or with a cancelled answer: nothing is enqueued.

## Deviations and decisions
1. **`MailActionException` text is German and lies in Infrastructure** (`MailboxActions.cs`, constants). The binding constructor takes only
   a string, so there is no reason code the UI could map. The banner shows `exception.Message` (`ShellViewModel.cs:856`), and all
   producers are fixed strings. `UiText` otherwise holds all German texts; this is the one exception. No inner exception.
2. **Added guard: stale row** (`MailboxActions.cs:249`). The contract reads UIDVALIDITY now, which removes what secure M1 of AP-03c protected: a
   row from before a UIDVALIDITY reset could address another mail with the same UID. The stored id of the UID must equal `MailMessage.Id`,
   otherwise rejected. `Id` 0 is not compared.
3. **"Ordner leeren" asks again when the count has grown** (`ShellViewModel.cs:1013`), at most 3 times, then nothing is enqueued. The contract has
   no way to pass the confirmed count, so a mail arriving between the last count and the snapshot of the queue can still be part of it: a
   window of milliseconds, not closed. N2 of AP-03c is therefore met for the question, not atomically. Count 0 asks nothing.
4. **Group view Junk over several accounts cannot be emptied** (`ShellViewModel.cs:748`): the button is only there when exactly one folder lies
   behind the view, so one account only. Emptying several accounts with one click does not fit "Ordner leeren" and would endanger case 21,
   where `Konto 1` must stay untouched. With one account the group view works.
5. **`MoveTargets` leaves out** the source folder (when the selection has one), Gmail "All Mail" and the local Outbox (`ShellViewModel.cs:394`).
   A move to one of these is refused before the queue as well; rows already in the target are skipped.
6. **Del with a mixed selection** (for instance from a search): one question; rows in the Trash go permanently, the others to the Trash.
7. **Q1** watches only deletes outside Trash and Junk, because only there the executor goes through the Trash. It looks at the first two
   refreshes after the command is gone, through at most 5,000 Trash rows; mails without a Message-ID are not recognised. The banner stays
   until the next action the user starts.
8. **Resize end** has no event in the Windows App SDK: stored 500 ms after the last change, and on close. No window-procedure hook.
9. **The window size is stored in device-independent pixels**, the position in screen pixels. A position counts when an inner band of the title
   bar overlaps a work area (`WindowPlacementKeeper.cs:94`), so a window whose title bar is lost is placed by the system.
10. **D6 tooltip: built as planned, not verified.** A `Border` in a `Canvas` above everything (not `ToolTip`, which needs a pointer owner), placed from
    `GetCursorPos`; when that fails, at the lower left. It stands where the mouse was when the text changed and does not follow inside a
    link. **Ich bin mir unsicher:** that WinUI draws XAML over the WebView2 here and that `StatusBarTextChanged` fires with `IsStatusBarEnabled = false`;
    the SDK docs say "the developer must create the status bar", which points that way. If the hint does not show, the target is invisible: then
    set `IsStatusBarEnabled = true` (`MailWebViewHost.cs:178`), see case 22.

## Questions
- `MailboxActions.cs:69`: `CountForEmptyAsync` on the Inbox is rejected too (case 4 read literally). If the count should work there, only
  `RequireEmptiableFolderAsync` goes.
- `UiText.cs:119`: `BannerNoAccounts` still says "bis AP-04b"; accounts moved to AP-04c. Left as it is, because it is out of this scope. Change?

## Getting a result
**Tested (self-check, no proof; artificial data, `_scratch\ap04bprobe`, in-memory SQLite and fakes):** D1 on the real queue (2,500 mails, rejections, count, empty, gone
UID, discard); cases 7, 8, 9, 12, 14 (real time 0.8 s and 1.8 s), 15, 16, 17 without a stale race, 18, 19; Q1 banner; empty folder text; no critical line after dispose. All passed.
Build: `MailWarden.App` and `MailWarden.Presentation` with `-warnaserror`, including XAML: 0 errors, 0 warnings. `tests\` were not built.

**Untested, only Dennis can see it:** everything in `MailWarden.App` at runtime: multi-selection and its sync with the view model, keys, context menu and selection on right click,
`ContentDialog`, flyouts, search box, navigation without highlight during a search, link hint, window placement after a restart (maximised, other scale, monitor removed), a real
server. Theme resources of the hint were checked against `generic.xaml`. **Vermutlich korrekt:** the rest of the XAML wiring, since it compiles.
**Not run:** S1-S3 (tester).

## fix.2 (U1-U9 of `requirement.md`, section "Decisions fix.2")
Items 3 and 10 of "Deviations and decisions" and the question on `CountForEmptyAsync` above are replaced by this section.

| # | Where | What |
|---|---|---|
| U1 | `ShellViewModel.StartOnSelection` | The only way a command reaches the selection: a copy at the keystroke goes into the action as a parameter; at its turn hidden rows drop out. `DeleteAsync`, `DeletePermanentlyAsync`, `MoveAsync`, `ToggleFlagAsync`, `ChangeSeenAsync` no longer read `SelectedRows`. |
| U2 | `MailboxActions.RequireEmptiableFolderAsync` | Refuses preview and empty while a command with status `Pending` and kind `Move` or `MoveToTrash` has this source folder, also in backoff. A permanently failed one takes nothing away and does not block. |
| U3 | `IMailboxActions`, `ShellViewModel.EmptyFolderAsync` | `PreviewEmptyAsync`, `EmptyFolderPreview`, `EmptyFolderAsync(folderId, confirmed, ct)`. After a refusal the UI reads a new preview: changed means ask again (3 questions at most), unchanged means another reason and goes to the banner. |
| U4 | `MailboxActions.PartlyDoneText`, `UiText.Confirm*` | Rejection after stored commands; also the second step of a mixed Del. Number of accounts when more than one; account label in the empty question. |
| U5 | `MailWebViewHost`, `MailDisplayText.LinkTarget` | Status bar on, hint on top. Beyond 500 characters an http or https target reads `host \| text`, in total at most 501 characters. |
| U6 | `ShellViewModel.ViewBehindEmptyCommand` | Decides by `Search.IsActive`, which changes before the event; `Messages.IsShowingSearch` changed only after it, that was the bug. |
| U7 | `ShellViewModel.EnqueueAsync` | Checks `isDisposed` right before enqueuing, and again before the second step of a mixed Del. |
| U8 | `MainWindow.OnMessageListKeyDown` | `KeyStatus.WasKeyDown`: the key is handled, nothing happens. |
| U9 | `WindowPlacementKeeper`, `MainWindow.FocusMessageList` | `Move`, then `Resize`. Focus back on the selected row after a removal, not out of the search box. |
| TODO | `TODO.md` | The six deferred points, section "Vertagt aus AP-04b (fix.2)". |

### Beyond the decisions, please check
1. **Second Del while the first is being stored (`ShellViewModel.rowSelectedByRunningRemoval`).** U1 only names "before the row is hidden". But the row is hidden before its command is stored, so a fast second
   Del would hit the next row, which the app and not the user selected. Case 24 asks for "the next row untouched", so a keystroke during that time leaves this row out. A row the user selected himself in that time
   is acted on. After the command is stored the next Del acts normally, as in Outlook.
2. **The view of "Ordner leeren" is also taken at the keystroke**, by the same reasoning as U1 (U1 does not list it).
3. **`ActionRowsRemoved`**: new public event on `ShellViewModel`, so that the window knows when to take the focus back (U9).
4. **`ShowWantedFlags`:** the wanted flags now go to the current rows by id, because a row copied at the keystroke can have been replaced by a reload.

### For the tester
- Case 24: two `Execute` calls need something blocked (role read or enqueue). With a read model and actions that complete synchronously the first Del is completely done before the second, and two commands are correct.
- `MailActionException` during `EmptyFolderAsync` with an unchanged `PreviewEmptyAsync` goes to the banner without a second question.
- Banner on a partial rejection: the text of the exception. A fake has to throw its own text; only the real `MailboxActions` produces `PartlyDoneText`, the shell only for the mixed Del.

### Remaining limits
- U3: replaced by V1, see section "fix.3".
- U5: a target above 65,519 characters cannot be parsed by `Uri`, so it has no host in front.
- U5: the host in front is `Uri.IdnHost` (punycode), not `Uri.Host` as the decision says (commit `6bb0491`): a Unicode host cannot pass as another one.

### Getting a result
**Tested (self-check, no proof; `_scratch\ap04bprobe`, artificial data):** U2, U3, U4 on the real queue; cases 23, 24 (both variants), 26, 28, 29, 30, 31 on fakes. All passed.
Build: `MailWarden.Presentation` and `MailWarden.App` with `-warnaserror`: 0 errors, 0 warnings. `tests\` not built: `PreviewEmptyAsync` breaks their fakes until the tester adapts them.
**Untested, only Dennis can see it:** U8 (holding Del), U9 (window on a monitor with another scale, focus after a delete), U5 at runtime (case 32), and case 27 in the banner of the running app. **Not run:** S1-S3.

## fix.3 (V1-V4, coordinator 2026-11-01)
| # | Where | What |
|---|---|---|
| V1 | `MailboxActions.EmptyFolderAsync` | Also refused when the local count is above `confirmed.Count`. After enqueuing, before the trigger: `Uids[^1] > HighestUid` or `Uids.Count > Count` discards the command and refuses with the same text. Fewer stays allowed. |
| V2 | `MailboxActions.EnqueuePerFolderAsync` | Any failure after stored commands except cancellation, `ObjectDisposedException`, `OutOfMemoryException` gives `PartlyDoneText`; the type name goes to the log, not for a `MailActionException`. |
| V3 | `ShellViewModel.StartRemovalOnSelection` | `rowSelectedByRunningRemoval` holds for delete, permanent delete, move; read, unread, flag use `StartOnSelection`. |
| V4 | `ShellViewModel.RemoveRowsAsync` | `try` directly behind `BeginRemoval`, `EndAction` in `finally` (a no-op when `EnqueueAsync` has ended it). `ActionRowsRemoved` does not fire after `Dispose`. |

### Beyond the wording, please check
1. **New optional constructor parameter** `MailboxActions(database, triggerAccount, timeProvider, ICriticalLog? log = null)`: V2 needs a log, and the class had none. Calls with three arguments stay valid; the runtime passes its log. Without a log nothing is written.
2. **V2 also for the mixed Del in the shell** (`DeleteTrashRowsAfterMoveAsync`): its first step is stored there, so the same rule applies.
3. **The discard in V1 runs with `CancellationToken.None`:** a cancelled caller must not leave the command behind.

### Remaining limits
- V1: a cycle already running can list the command between its insert and its discard. No trigger comes from here, and the window is the time of one `DELETE`; closing it needs a transaction in the queue.
- V4 covers `BeginRemoval` only. `ChangeFlagAsync` has the same order around `BeginFlagChange`, not changed: V4 does not name it, and a failure there can only come from a subscriber of a row. In-flight flags would stay until the next action.

### Getting a result
**Tested (self-check, no proof; `_scratch\ap04bprobe`, artificial data):** V1 before enqueuing (a lower UID arrives, count above the confirmed one; fewer goes ahead), V2 with an SQLite trigger that fails the second group (`PartlyDoneText`, type name in the log), V3, V4 (no event after `Dispose`). All passed.
**Untested:** the check of V1 after enqueuing, because it is only reachable in a race with the sync and the queue has no seam for it; read only.
Build: `MailWarden.Presentation` and `MailWarden.App` with `-warnaserror`, see the report. **Not run:** S1-S3.

## fix.3 addendum (planner, 2026-11-01)
`ChangeFlagAsync` gets the same `try`/`finally` around `BeginFlagChange` as `RemoveRowsAsync`: the builder reported the same order and the planner decided to treat both alike. `EndAction` does nothing when the action has already ended.
