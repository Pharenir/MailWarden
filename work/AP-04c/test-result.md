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
