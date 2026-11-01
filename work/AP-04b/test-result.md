# AP-04b - test result (verify.1)

Result: PASS. Stage S3 (full suite) via watchdog on `feat/ap-04b-ui-actions`, build `-warnaserror` clean.

| Project | Tests | Passed |
|---|---|---|
| Core.Tests | 511 | 511 |
| Presentation.Tests | 487 | 487 |
| Infrastructure.Tests | 1302 | 1302 |
| Total | 2300 | 2300 |

Duration 25.9 s (no soft limit exceeded). Cases 1-20 automated, 21-22 manual (Dennis).

## Failures in the first runs (all test errors, fixed in tests only, no src change)
- Compile: 6 errors (constructor of `ShellViewModel` in two AP-04a tests, `double` signature of `WindowPlacement.IsOnAnyMonitor`, missing using).
- `Null_window_values_are_treated_as_unknown`: assumption wrong; the loader rejects null (fail fast). Test now expects `UiSettingsException` naming the key.
- `A_confirmation_answered_after_dispose_enqueues_nothing`: Dispose already completes the open answer; `SetResult` -> `TrySetResult`.
- `A_search_answering_after_dispose_changes_nothing`: Dispose clears the list; test now asserts no search result row appears.
- AP-04a `Read_model_interface_has_only_read_members`: `SearchAsync` added to the expected members (contract D3).
- AP-04a `View_models_expose_no_write_style_methods`: now lists offenders; `ShellViewModel.SaveWindowBounds` (D4 window settings, not mail) is allowed.
- Empty-folder tests (3): the command follows the navigation selection, not `Messages.LoadAsync`; tests select the navigation node (Junk via `GroupForAccount`).
- `EmptyFolder_with_2500_mails...`: AP-03c has no 1,000 UID limit for EmptyFolder; test asserts coverage only.

No product defect found. Open: nothing automated for cases 21 and 22.

# verify.2 (fix.2, cases 23-31)

Result: PASS. S3 via watchdog on `feat/ap-04b-ui-actions` (fix.2 merged), build `-warnaserror` clean, 27.2 s.

| Project | Tests | Passed |
|---|---|---|
| Core.Tests | 511 | 511 |
| Presentation.Tests | 507 | 507 |
| Infrastructure.Tests | 1310 | 1310 |
| Total | 2328 | 2328 |

Only test-side fixes needed before the run: xUnit1031 (blocking `GetResult` replaced by `await`), case 24 reduced to the gated variant (an ungated double Del is correct behaviour), case 27 uses `MailboxActions.PartlyDoneText`, case 29 sharpened to the `host | text` format with at most 501 characters. No product defect. Not automated: U8 (key repeat), U9 (window placement, focus), cases 21, 22, 32.

# verify.3 (feat 7eb7a4a merged into the test branch)

Result: PASS. Build `-warnaserror` clean; S3 via watchdog 27.1 s, no soft limit exceeded.

| Project | Tests | Passed |
|---|---|---|
| Core.Tests | 511 | 511 |
| Presentation.Tests | 511 | 511 |
| Infrastructure.Tests | 1314 | 1314 |
| Total | 2336 | 2336 |

First run: 1 red test, `ActionRaceTests.Read_and_flag_on_the_follow_row_work_while_the_delete_is_being_saved` (timeout waiting for `SetSeen` while the delete was still blocked in the fake). Class: test assumption, not a proven product defect: the test expected the read call to reach the actions fake while the delete was still held. Observed: it does not arrive before the delete is released. Test adapted to execute read, flag and unread, release the delete, then assert the calls go to the follow row; green afterwards. Open point for the planner: whether read/flag on the follow row should be stored concurrently with the delete being saved (not observed) or may wait behind it (observed or skipped, not distinguishable from the fake).

Covered: V1 (lower UID, count above confirmed refused, fewer passes), V2 (second-group storage failure gives `PartlyDoneText`; log now asserted via `ICriticalLog` parameter: contains `StorageException`, no trigger text), V3 (read to follow row; delete/permanent/move skipped), V4 (no `ActionRowsRemoved` after Dispose, control fires).

Not reachable from the public surface, no tests: step-before-enqueue failure leaving no in-flight state for flag/removal (no injection point for that step), and `DeleteTrashRowsAfterMoveAsync` second-step failure (not public; the mixed Del path would need a fake of the two-step sequence).

# verify.4 (feat 6e6428d merged into the test branch)

Result: PASS. Build `-warnaserror` clean; S3 via watchdog 27.2 s, no soft limit exceeded.

| Project | Tests | Passed |
|---|---|---|
| Core.Tests | 511 | 511 |
| Presentation.Tests | 513 | 513 |
| Infrastructure.Tests | 1324 | 1324 |
| Total | 2348 | 2348 |

Adapted to the new contract (no behaviour softened): old one-argument `EnqueueEmptyFolderAsync` calls go through `EmptyFolderHelpers` (confirm UIDVALIDITY and all local UIDs), `FakeMailboxActions` builds `EmptyFolderPreview(UidValidity, Uids)` and refuses when a local UID is not confirmed, the V1 tests (count/highest UID) were rewritten to UID sets.

New: W-a, W-b (D1 and queue level, queue asserted empty), W-c, W-d, W-e (trigger on insert, error reaches the caller, queue empty, same call works after `DROP TRIGGER`), atomicity (second connection holds `BEGIN IMMEDIATE`, changes a UID, the call waits >= 500 ms and then refuses; not tagged slow, about 0.5 s), W-f and W-g (UI: forced refusal with unchanged preview stops after one question; different preview asks again). Queue level: `EmptyFolderChangedException` on unconfirmed UID and on changed UIDVALIDITY, `null` for nothing local.

One test changed because it was red: `W_e_...` first asserted that the caller's exception text hides the SQLite message. That assertion was my own, not part of W-e; the raw storage error reaches the caller (as W-e says), so it was removed. All other W assertions are unsoftened.

Not tested: `MailboxActions.MailArrivedText` is not public, so the refusal text is checked as fixed, non-empty, different from `PartlyDoneText`, and free of mail data; `TryGetPartlyDone` (semantics of the return value not in the contract given to me); `MaxEmptyConfirmations`.

# verify.5 (feat with X1-X4 and main/AP-06 merged into the test branch)

Result: **FAIL** (2 red tests, no test softened, no production code touched). Build `-warnaserror` clean; S3 via watchdog 28.8 s.

| Project | Tests | Passed |
|---|---|---|
| Core.Tests | 536 | 536 |
| Presentation.Tests | 532 | 530 |
| Infrastructure.Tests | 1575 | 1575 |
| Total | 2643 | 2641 |

New: `EmptyFromTreeTests` (X-a to X-g, X-i to X-m, `CanEmptyFolder` refresh and PropertyChanged, `ActionRowsRemoved` fires only when rows left the list, answer "no"), `SqliteMessageStoreExcludedIdsTests` (real SQLite: folder view, role view, account+role, null, empty, unknown, seen, all, 40,000 ids, duplicates). `FakeMailboxReadModel` honours `ExcludedMessageIds`.

## Red tests
1. `X_d_group_junk_and_trash_with_two_accounts_cannot_be_emptied` (EmptyFromTreeTests.cs line 144). Two accounts that both have Trash and Junk: the Junk group item has `CanEmptyFolder == false` (passes), but `EmptyFolderOfViewCommand.CanExecute(MailView.Group(FolderRole.Trash))` returns **true**. Expected false (X-d: group Trash with two accounts cannot be emptied; X-c: for Trash the rule is tested on the group view). Repro: seed two accounts with Trash and Junk, initialise, query that CanExecute. Class: product or contract (the command's CanExecute does not apply the "exactly one folder behind the view" rule for Trash).
2. `X_b_other_folders_account_nodes_and_parents_cannot_be_emptied`: all tree items report `CanEmptyFolder == false` for Inbox, Sent, Archive, custom, account node and parent (passes), but `EmptyFolderOfViewCommand.CanExecute(MailView.Folder(account, folder))` returns **true** for Inbox, Sent, Archive and the custom folder (4 of 4). The entry flag is right; the command is enabled for views that cannot be emptied. Whether Execute then does nothing is not checked by this test. Class: product (CanExecute too permissive) or an unwritten contract detail; planner to decide. The CanExecute assertion is my reading of X1 ("only when that entry can be emptied").

## Not tested
- X-h and X4 (status bar off) are manual (WinUI code).
- X-e on the real queue: the Presentation tests use the actions fake; the stored UIDs of an empty are covered on the real queue by W-c (`MailboxActionsTests`).
- Mark read/unread and the number: the requirement says they are not part of X3, so nothing is asserted.

## verify.5 update (planner decision on the 2 red tests)

First result: FAIL, 2641 of 2643 (X-d and X-b, both on `EmptyFolderOfViewCommand.CanExecute`).

Decision (planner): contract gap, not a product fault. `CanEmptyFolder` on the tree entry decides the menu; `CanExecute` only says the parameter is a `MailView`; the emptiable check runs at execution; the safety property is that executing on a non-emptiable view does nothing.

Tests corrected (stricter on behaviour): X-d executes the command on `MailView.Group(Junk)` and `Group(Trash)` with two accounts, asserts no question and no call. X-b keeps all `CanEmptyFolder == false` assertions and executes on Inbox, Sent, Archive and the custom folder, each holding a real mail, asserts no question and no call.

Rerun: PASS. Build clean, S3 28.9 s.

| Project | Tests | Passed |
|---|---|---|
| Core.Tests | 536 | 536 |
| Presentation.Tests | 532 | 532 |
| Infrastructure.Tests | 1575 | 1575 |
| Total | 2643 | 2643 |
