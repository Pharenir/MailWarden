# AP-04c test result (tester, step verify.1)

**Verdict: FAIL** (2 of 3175 tests red, one open question to the planner, class c)

```
Stufe:        S3 (test-map: TestSupport changed -> FULL)
Auswahl nach: voll
Ergebnis:     3173 von 3175 grün (Suite gesamt: 3175)
              Core 536/536 · Infrastructure 1844/1844 · Presentation 793/795
Fehlschläge:  AttachmentActionsTests.An_attached_message_is_proposed_and_opened_as_an_eml_file(name: "forwarded_a")
              AttachmentActionsTests.An_attached_message_is_proposed_and_opened_as_an_eml_file(name: "")
Nicht gelaufen: nichts ausgelassen
Laufzeit:     28.5 s (erwartet ca. 15-29 s laut Historie; Grenzen weich/hart 300 s = Mindestwert)
Abbruch:      –
Auffällig:    weiche Grenze nicht überschritten; kein Test langsamer als 12 s
```
Durations history not updated and the test counter not reset (run not green).

## Failures

### 1. A11, save proposal and open name of an attached message have no `.eml` (class c, possibly b)
- Test: `An_attached_message_is_proposed_and_opened_as_an_eml_file` for the part names `forwarded_a` and the empty name (the variant `forwarded_b.eml` is green).
- Expected: A11 says "A `message/rfc822` part is saved or opened as its message (`.eml`, level Everything else)". Read literally the dialog proposes a name ending in `.eml` and the opened file ends in `.eml`.
- Actual: the proposed name is `forwarded_a` (empty name: `anhang-1`), i.e. `SafeFileName` carries no `.eml`. Level Ask and `CanOpen` are right.
- Cause: the requirement does not say whether the `.eml` is appended for a part without that extension, or whether "(`.eml`, ...)" only describes the level. I cannot derive it, so class c. If appended is meant, the product deviates (b): a saved `anhang-1` has no extension and Windows cannot open it as a mail.
- Reproduction: `dotnet test MailWarden.sln --filter "FullyQualifiedName~An_attached_message_is_proposed_and_opened_as_an_eml_file"`.
- Decision needed from the planner: append `.eml` (then the product changes) or loosen the test to "Ask level, no extension required" (then A11 should drop the `.eml`).

## Own test errors found and corrected in this round (class a, each within 1 attempt)
- Fake `CopyAttachmentAsync` and `AttachmentCopyTests` moved to the new signature with `expected` (U1 void). Case 9 now checks: index outside, null `expected` (`ArgumentNullException`), missing EML, EML deleted after showing, changed EML (swapped parts, other size, other name, other type), unmatching `expected` without any read before (3 variants), positive control. Fake now records `CopyExpected`.
- `new MailContentException("...")` does not exist (parameterless only): changed to `new MailContentException()`.
- Two synchronous tests carried `Timeout` (xUnit rejects it): removed.
- `Rows_match...`: an unnamed part shows the placeholder `(Ohne Namen)`; the test now requires a non-empty name there instead of equality with the empty file name.
- `An_attached_message_becomes_an_eml_file...`: the `.eml` assertion on `SafeFileName` was a format assumption; the row test checks level/CanOpen/HasContent only, the behaviour (proposal and opened path) moved to the new test above.
- Case 41 (`ShellViewModelTests`): `CopyAttachmentAsync` added to the read-model member list (intended, read only); `ReadingPaneViewModel.SaveAttachmentAsync` exempted from the "no write-style method" scan (it writes a local file after the dialog, never the mailbox). Both are changes to an existing test, as agreed in the handover for the first; the second is my judgement from A2/A12, please confirm.

## Added for fix.1 / handover
- Case 10: `Save_marks_the_target_as_from_the_internet`, `Save_over_a_target_with_another_zone_ends_with_zone_3` (target empty and with content, previously `ZoneId=1`). Green on NTFS (`K:`). The non-NTFS path (save without mark, open refuses) stays untested (no such volume).
- `HasContent`: `A_row_without_a_size_has_content_only_as_an_attached_message` (green).
- Constructor: host and open folder both or neither (`ArgumentException`), green.
- `DataPaths`: `COM0`, `LPT0.txt`, `CONIN$`, `CONOUT$` rejected, green.
- Not tested: `ConfirmationRequest.AcceptText` (the harness records only the texts of the question; the field is a presentation detail).

## Testzahlen je Projekt
| Projekt | Tests | grün | rot |
|---|---|---|---|
| MailWarden.Core.Tests | 536 | 536 | 0 |
| MailWarden.Infrastructure.Tests | 1844 | 1844 | 0 |
| MailWarden.Presentation.Tests | 795 | 793 | 2 |

Log: `.test-logs\last-run.log`.

---

# AP-04c test result (tester, step verify.2)

**Verdict: PASS** (3271 of 3271 tests green, S3, no deviation of the product found)

```
Stufe:        S3 (test-map: TestSupport changed -> FULL)
Auswahl nach: voll
Ergebnis:     3271 von 3271 grün (Suite gesamt: 3271)
              Core 536/536 · Infrastructure 1847/1847 · Presentation 888/888
Fehlschläge:  keine
Nicht gelaufen: nichts ausgelassen
Laufzeit:     62.6 s (3 Läufe nacheinander: 1.3 + 44.2 + 17.1 s; keine Historie im Worktree, Grenzen 300 s = Mindestwert)
Abbruch:      –
Auffällig:    Wächter-Lauf über die .sln wird vorzeitig beendet, siehe unten
```

## Testzahlen je Projekt
| Projekt | Tests | grün | rot |
|---|---|---|---|
| MailWarden.Core.Tests | 536 | 536 | 0 |
| MailWarden.Infrastructure.Tests | 1847 | 1847 | 0 |
| MailWarden.Presentation.Tests | 888 | 888 | 0 |

Before (verify.1): 536 / 1844 / 795. New: Infrastructure +3 (junction), Presentation +93.

## Auffällig: Wächter mit `dotnet test MailWarden.sln`
The `--done-regex` of the project configuration matches the summary of the first finished project; the watchdog then stops the rest after the grace period (result `nachlauf`, code 123, Infrastructure cut off). This is not a test failure and not a product problem. The S3 run therefore ran per test project, each through the watchdog (`dotnet test tests\MailWarden.<Project>.Tests --no-build`), logs `.test-logs\run-<Project>.log`. Suggestion for the planner: accept per-project runs as the S3 form, or give the solution run a done-regex that only the last summary can meet.

## Changes in this round
- Removed uses of `AttachmentNames` (ReadingPaneViewModelTests, ReadingPaneBuildGuardTests). Case 54 now checks `Attachments`, `MaxAttachmentRows` (500), `HasHiddenAttachments`, `HiddenAttachmentsNotice` (boundaries 0, 3, 50, 499, 500, 501, 100000). The notice wording is not checked, only the missing count.
- A11 (decided): rows, save proposal and opened file of an attached message end in `.eml` (`forwarded_a.eml`, `anhang-1.eml`, `evil.exe.eml`, `forwarded_b.eml` unchanged), level Ask, extension column `EML`. `message/delivery-status` with a size keeps its name without `.eml`. The two reds of verify.1 are resolved.
- `AttachmentRow.ExtensionText`: capitals, no dot, empty without extension; long name; unusable name; follows the safe name, not the claimed type.
- `Classify`: `zip` moved from Harmless to Ask; a sample of the longer SaveOnly list (`mdb`, `accdb`, `pyc`, `winget`, `msrcincident`, `cer`, `msh1xml`, `appcontent-ms`, `bas`, `pl`, also in capitals); `slk`, `iqy`, `pps`, `one` stay Ask. Only these ten extensions are tested, the full list is in impl-notes.md, which I may not read.
- `MustAskBeforeOpening`: Trash asks (table and theory); Trash added to the case 16 theories (ask, yes) and to saving asks nothing.
- `ToSafe`: invisible characters and variation selectors (U+034F, U+FE0F, U+2800, U+3164, U+1D173, U+E0100, a tag character, zero-width characters, BOM) removed; the eleven wide or code-page forms become `_`; emoji and plane-2 characters stay; a lone surrogate leaves valid text; a long emoji name is cut without tearing a pair; the risk follows the extension left after the removal.
- Notices: `AttachmentNotice` is `UiText.AttachmentCopying` during a save and an opening, `UiText.AttachmentSaved` after a save, null after an opening, a cancelled dialog and an answer of no (checked by the `UiText` property plus the words `kopiert` / `gespeichert`). A failed save after a change of mail goes to `Shell.Banner` (`UiText.BannerAttachmentSaveFailed`), the new mail shows nothing, the next attachment action (open, save, cancelled save) clears the banner. A failed opening after a change of mail is only logged, no banner. Without a change of mail the banner stays empty.
- Case 18: host false gives a notice and no log line from the view model (`Log.Entries` empty); a decoding failure keeps its log line with the type name.
- Case 19b: the role is read again. A changed role (8 pairs, also to a role that asks for none, from a risky to a safe one, to unknown) and an unreadable role (`FolderRoleFailure`) ask, with yes one opening. An unchanged readable role asks nothing, saving never asks, SaveOnly stays blocked. The question names no file.
- Case 19c: a yes after another mail was selected opens nothing (no copy, no file, no notice, no banner); the positive control opens. Needed `ShellHarness.ConfirmationRequests` (TestSupport, hence S3). A variant "mail closed during the question" is not tested: `ReadingPaneViewModel.Close` is no mail-close that a test can call.
- Case 12, `AttachmentFiles.Clear` with real junctions (`mklink /J` via `cmd`, no elevation): in the `<guid>` folder, directly in `attachments-open`, and `attachments-open` itself a junction. The target keeps content, a nested file, the read-only attribute and the folder attributes; the link entry is gone. The test cleanup removes links first and clears the read-only mark; everything stays inside the test output.

## Cross-check of the junction tests against the state before fix.2
Done without touching `src\` of the test worktree: a separate detached worktree of `fa026f9` under `_scratch\old-fa026f9`, only `AttachmentFilesTests.cs` copied in, built, the junction tests run (`--filter AttachmentFilesTests.Clear_removes`). At `fa026f9` all three junction tests were RED (`AssertTargetUntouched`: the target was damaged), the older `Clear_removes_all_prepared_files` green. On the fix.2 state all three are green. The scratch worktree was removed afterwards.

## Own test errors in this round (class a)
- `EntryExists` threw `DirectoryNotFoundException` when `Clear` had removed the guid folder itself (1 attempt, fixed).

## Not tested / open
- The non-NTFS path (save without mark, open refuses): no such volume.
- File symbolic links in `Clear` (need elevation or developer mode).
- `FileSavePicker` and the launch: Dennis measures them (cases 21 to 26); the window runs in no test.
- `AtomicFile.WriteNewFileAsync` has no test of its own; its two users are covered (the `FileEmlStore` tests and the `AttachmentFiles` tests, all green).
- Testzähler in TODO.md and the column *Getestet* of PLAN.md are not changed (instruction); the counter would be reset after this green S3. `.test-logs\durations.jsonl` is written (git-ignored).

Logs: `.test-logs\run-Core.log`, `run-Infrastructure.log`, `run-Presentation.log`.
