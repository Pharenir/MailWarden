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
