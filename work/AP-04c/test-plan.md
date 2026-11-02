# AP-04c test plan (tester, step author.0)

Basis: `requirement.md` only (A1-A12, contract, cases 1-20). Cases 21-25 are manual and not in the code.
Written without `src\`, without the diff and without a test run (the code does not compile on this branch yet).
Trait: `Category=feature:ui` on all new classes; the Mime and Composition classes carry the same extra tags as their neighbours (`sync`, `storage`).
`tests\test-map.json` needs no entry: `Presentation.Tests/**`, `Infrastructure.Tests/Mime/**` and `Composition/**` are mapped already, and the source globs `src/MailWarden.Presentation/**`, `src/MailWarden.Infrastructure/Mime/**`, `Composition/**` cover `AttachmentPolicy*`, `AttachmentFiles*`, `AttachmentRow*`. Changing `tests\**\TestSupport\**` maps to FULL, so S3 is the run after the merge.

## Files

| File | Cases |
|---|---|
| `tests\MailWarden.Presentation.Tests\AttachmentPolicyTests.cs` | 1, 2, 3, 4, 5, 6 (pure functions) |
| `tests\MailWarden.Presentation.Tests\AttachmentRowTests.cs` | 7, plus row rules of A1, A4, A5, A7, A10-A12 |
| `tests\MailWarden.Presentation.Tests\AttachmentActionsTests.cs` | 13, 14, 15, 16, 17, 18, 19, 20 (+ A2, A6 saving never asks) |
| `tests\MailWarden.Infrastructure.Tests\Mime\AttachmentFilesTests.cs` | 10, 11, 12 (real files, real `Zone.Identifier`) |
| `tests\MailWarden.Infrastructure.Tests\Composition\AttachmentCopyTests.cs` | 8, 9 (real runtime, real EML files) |
| `tests\MailWarden.Presentation.Tests\TestSupport\Fakes.cs` | `FakeMailboxReadModel.CopyAttachmentAsync` (+ `CopyBehavior`, `SetAttachmentBytes`, `CopyCalls`, `DefaultAttachmentBytes`), new `FakeAttachmentHost` |
| `tests\MailWarden.Presentation.Tests\TestSupport\ShellHarness.cs` | `AttachmentHost`, `AttachmentOpenFolder`, `WorkRoot`, `Dispose` |

Data: artificial only (`sender_a@example.test`, names like `report_a.pdf`). Files are written below `AppContext.BaseDirectory` (`ap04c-ui`, `ap04c-files`, `ap04c-copy`) and removed afterwards. No fixtures under `tests\fixtures\`: the MIME messages are built in the tests.
Timeouts: `[Fact(Timeout = TestTimeouts.PerTestMilliseconds)]` on async tests as in the neighbours; the purely synchronous policy tests have none (as `DeepNestingReaderTests` does for sync tests).

## Case coverage

| Case | Where | Remarks |
|---|---|---|
| 1 | `AttachmentPolicyTests` | Theory over all listed inputs plus UNC, leading slash, `lpt9`; generic checks (no `\ / :`, no control/bidi, no trailing dot/space, not reserved, `Path.GetFileName` stable, length <= 255); exact results for trailing dots/spaces, clean names and fallbacks (`anhang-<index + 1>`); 400 characters keeps `.pdf` |
| 2 | same | RTLO removed, `Classify` of the result sees `.exe`; control, C1, bidi isolates, tab, CR/LF removed |
| 3-5 | same | One theory row per blocked/harmless/ask type; upper case; `rechnung.pdf.exe`; `exe.pdf` Harmless |
| 6 | same | Complete table over `Enum.GetValues<FolderRole>()` and `null` for Ask and Harmless; SaveOnly only checked for "does not throw" (see U3) |
| 7 | `AttachmentRowTests` | Real reader on a built message (attachments, linked inline image, attached `message/rfc822`); rows equal `Attachments` by index and name; inline image has no row; also fake-content cases for sizes, cleaning, replacement on another mail |
| 8 | `AttachmentCopyTests` | base64 (all 256 byte values, 3 MB random), quoted-printable and 8bit parts written as raw text so the wire encoding is fixed |
| 9 | same | Index -1/2/99/min/max and no attachments -> `ArgumentOutOfRangeException`; EML file missing and no path -> `FileNotFoundException`; changed EML (swapped parts; same name other size; other name same size) -> `MailContentException` and nothing written to the destination |
| 10 | `AttachmentFilesTests` | Success (300 kB, replaces the empty dialog file, empty part); writer throws with and without prior empty target; existing non-empty target survives a failed save; cancellation; locked target (move fails) leaves no temp file |
| 11 | same | Path `<openFolder>\<guid>\<name>`, content, `path:Zone.Identifier` contains `[ZoneTransfer]` and `ZoneId=3`; two calls -> two folders; throwing writer leaves no file. `RequireNtfs()` fails the test with a clear message if the output is not on NTFS |
| 12 | same | Locked file stays, no exception, the other file gone; second `Clear` finishes; `Clear` without the folder |
| 13 | `AttachmentActionsTests` | Host returns null: no copy call, no notice, no folder, no log line |
| 14 | same | Harmless from Inbox: no question; host called once with a full path under the open folder, file name = `SafeFileName`, content snapshot taken at launch time equals the bytes |
| 15 | same | `.docm`, answer no / yes; also: nothing decoded or written while the question is open |
| 16 | same | Junk, Quarantine, Screener, unknown role (`SetFolderRole(null)`): question, no -> no launch, yes -> one launch; control: folder named "Junk" with role Inbox asks nothing |
| 17 | same | `CanOpen` false; direct call opens nothing, writes nothing, decodes nothing (Inbox and Junk); `rechnung.pdf.exe` |
| 18 | same | Host false; `MailContentException` while opening and while saving; vanished mail (`FileNotFoundException`); failing dialog. Notice set, no exception, log holds `MailContentException` and never `report_a`; no half file, empty dialog file removed; later success clears the notice |
| 19 | same | `CopyBehavior` gated by a `TaskCompletionSource`; mail switched while saving (success, failure) and while opening: rows are the new mail's, `AttachmentNotice` is null |
| 20 | same | Open (harmless, ask yes), save (harmless, blocked), also with failures: `Actions.Calls` and `Controller.Calls` empty, flags unchanged |

## Assumptions to align when merging (planner)

- Q1: How `ReadingPaneViewModel` gets host, folder and the confirmation. The contract only says "constructor gains `IAttachmentHost` and the open folder; `ShellViewModel` passes them on". I fixed the way over `ShellViewModel`, called from `ShellHarness` with NAMED arguments after the existing ones:
  `new ShellViewModel(readModel, controller, actions, dispatcher, settings, log, clock, documentBuilder, attachmentHost: ..., attachmentOpenFolder: ...)`
  where `attachmentOpenFolder` is a `string` path (the VM builds `AttachmentFiles` from it). Names, position and type are guesses. The two direct `new ShellViewModel(...)` calls in `ShellViewModelTests.cs` (lines 344 and 372) use seven arguments and only compile if the new members are optional; otherwise they need the same additions.
- Q2: The confirmation reaches the tests through `ShellViewModel.ConfirmationRequested` (the harness answers with `ConfirmationAnswer`); tests read the text from `ConfirmationTexts` and only require it non-empty.
- Q3: The VM tests read rows from `harness.Shell.Reading.Attachments` after `Reading.ShowAsync(message)`; `Name` is compared with `MailAttachmentInfo.FileName`.
- Q4: `ReadModel.CopyAttachmentAsync` is called with `(message, row.Index, stream, ct)`; the stream is the file stream the VM or `AttachmentFiles` hands out.
- Q5: Existing tests that use `AttachmentNames` (`ReadingPaneViewModelTests`, `ReadingPaneBuildGuardTests`) are untouched; if the builder removes the property they must be changed.

## Unclear points of the requirement (c)

- U1, case 9 / A9: `CopyAttachmentAsync(message, index, destination, ct)` carries neither the shown name nor the size. "Checked against its name and size on reading" therefore needs a reference. My tests assume that the read model remembers what `ReadContentAsync` returned for the message (the list the UI shows), and that a changed EML after that fails with `MailContentException`. If the check is meant differently, the three "changed EML" tests are void. The contract should state where the expected name and size come from.
- U2, case 10: Does `SaveAsync` rethrow the failure or swallow it? Tests only require that an exception arrives (`Assert.NotNull`), not its type. Also not stated: whether a NON-empty existing target survives a failed save (A8 says "no half file"); the test `A_failed_save_keeps_an_existing_non_empty_target` assumes yes.
- U3, case 6: the table says nothing for `SaveOnly` (always "False" because it never opens, or True?). Not asserted.
- U4, case 7 / A11: How a part "below the depth limit has no action" can appear as a row. The reader does not list such parts in `Attachments` (flagged `HasOpaqueParts`), so `HasContent == false` has no producer in the contract. Tested: no row for the deep message; `HasContent` is true on every real row. `HasContent == false` itself is not tested.
- U5, case 18: "a critical log line holds the type name": I read it as the exception type name (the project convention, as in `ClassificationBannerTests`). For "host returned false" there is no exception, so no log line is required, only that the file name is not in the log. Not stated: wording of the notice (only non-empty is checked) and whether a successful save or open sets a notice (only: a cancelled save sets none; after a successful open `AttachmentNotice` is null).
- U6, case 19: what the old save does at the end (file complete, or removed) is not stated; only rows, notice and "no exception" are asserted.
- U7, A11 vs A7: a `message/rfc822` part whose name is `evil.exe`: A11 says `.eml`, A7 says the extension decides. Not tested; tests use `forwarded_a`, `forwarded_b.eml` and an empty name and require `.eml`, level Ask.
- U8, case 1: "a length Windows accepts": asserted as <= 255 characters for the name. The exact limit and how the long names are shortened (`400 x a` + `.pdf` keeps `.pdf`) are open; `file.txt:stream` and `a/b.txt` are only required to be safe, with `a/b.txt` keeping `.txt`.
- U9, A3: "if the mark cannot be written, it is not opened and says so" cannot be reached on NTFS; no test (needs a non-NTFS volume). Same for the `IOException` of `PrepareForOpeningAsync`.
- U10, case 8: "undecodable part" (A8) has no recipe in the cases, no test for `MailContentException` from decoding at the read-model level (only in the VM tests through a fake).
- U11, A5: only the types named in the table are tested ("and similar" is open).
