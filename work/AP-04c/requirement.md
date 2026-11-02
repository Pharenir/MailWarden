---
id: AP-04c
slug: attachments
title: Open and save attachments
test-scope: S2 during the rounds, S3 before the merge
test-filter: Category=feature:ui
status: released by Dennis 2026-11-02
---

# AP-04c: attachments

Released by Dennis on 2026-11-02. Branches `feat/ap-04c-attachments` (builder), `test/ap-04c-attachments` (tester), both from the commit holding this file, which stands on AP-04e and `chore/plan-archive`. Merge order: AP-04e, `chore/plan-archive`, this. Security review required: attachments, paths from foreign data.

## What exists (read on `main`, 966df28)
- Attachments are only listed: `MailAttachmentInfo` holds a cleaned name, type and size, and "its content is never kept" (`Infrastructure\Mime\MailContent.cs:9`). The reading pane shows names only (`Presentation\ReadingPaneViewModel.cs:548`). The name is "never used as a path" (`MailContentReader.cs:183`).
- `IMailboxReadModel.ReadContentAsync` reads the EML into memory; there is no way to get the bytes of one part.
- The app starts nothing from a file today. Only `Launcher.LaunchUriAsync` for links (`App\MailWebViewHost.cs:145`).

## Lesson of AP-04e
WinRT calls can fail in the unpackaged app although they compile (`HighContrastChanged`, `0x80070490`). So `FileSavePicker` and launching a file are **measured in the theme spike or a sibling before the builder relies on them**, not assumed.

## Behaviour
| # | Behaviour |
|---|---|
| A1 | Each attachment of the open mail is a row or chip in the reading pane with name, size, and the actions Öffnen and Speichern. Names go through `SafeDisplayText` as today |
| A2 | Speichern: the save dialog of Windows, proposed name from A4, stream copy of the decoded part, no full copy in memory. Nothing is saved without the dialog. The saved file carries the internet mark as in A3 where the target can hold it; on a target without NTFS (a USB stick) the file is saved without the mark, this does not fail the save |
| A3 | Öffnen: decoded part into `<data>\attachments-open\<guid>\<safe name>`, marked as from the internet (`Zone.Identifier`, zone 3), then opened with the program Windows assigns. If the mark cannot be written (no NTFS), it is not opened and says so. The folder is emptied at the next start and on exit, files still locked stay until the next start |
| A4 | Safe file name, from `MailAttachmentInfo.FileName` (cleaned, not cut): no path parts, no `:` (alternate streams), control and bidi characters removed, trailing dots and spaces removed, reserved names (`CON`, `NUL`, `COM1` and so on) changed, length limit, a fallback name for an empty result |
| A5 | Risk levels by the last extension, after A4: **Blocked** (runs code: `exe`, `com`, `scr`, `pif`, `bat`, `cmd`, `ps1`, `vbs`, `js`, `jse`, `wsf`, `hta`, `msi`, `lnk`, `url`, `reg`, `jar`, `cpl`, `dll`, `iso`, `img`, `vhd`, `chm`, `appinstaller`, `msix` and similar) can only be saved. **Harmless** (`pdf`, `txt`, images, `docx`, `xlsx`, `pptx`, `odt`, `zip` and similar) opens at once. **Everything else**, also no extension and macro formats (`docm`, `xlsm`): a question first |
| A6 | From Junk, Quarantine and Screener every Öffnen asks first, with the reason, also for harmless types. Blocked stays blocked. The role of the folder decides, never its name |
| A7 | The claimed MIME type never decides anything: only the extension does, because that is what Windows uses to pick the program |
| A8 | Parts that cannot be decoded, a mail deleted meanwhile, a full disk or a dialog cancelled: a readable message or nothing, no crash, no half file left under the chosen name |
| A9 | The attachment is addressed by its position in `MailContent.Attachments` and checked against the entry the row was made from (`MailAttachmentInfo`: name, type and size) on every copy, also without a read before, so a changed EML cannot deliver another part: `MailContentException` then |
| A10 | An unknown folder role counts as Junk for A6, as it does for embedded images (`ReadingPaneViewModel.cs:309`) |
| A11 | A `message/rfc822` part is saved or opened as its message (`.eml`, level Everything else); a part below the depth limit has no action. Measured in implement.1: such a part gets no row at all (`MailContent.Attachments` leaves it out, the notice of `HasOpaqueParts` covers it), so `HasContent` is false only for a part without decodable content |
| A12 | The rows, sizes and risk levels change only with the shown mail; opening and saving never set a flag and never touch the server |

## Schnittstelle (contract)
```csharp
// src\MailWarden.Presentation\AttachmentPolicy.cs
public enum AttachmentRisk { Harmless = 0, Ask = 1, SaveOnly = 2 }
public static class AttachmentFileName
{
    public static string ToSafe(string? name, int index);       // never empty; fallback "anhang-<index + 1>"
}
public static class AttachmentPolicy
{
    public static AttachmentRisk Classify(string safeFileName);                  // last extension, case-insensitive
    public static bool MustAskBeforeOpening(AttachmentRisk risk, FolderRole? folderRole);  // Ask, or Junk, Quarantine, Screener, null
}

// src\MailWarden.Presentation\AttachmentRow.cs
public sealed class AttachmentRow
{
    public int Index { get; }
    public string Name { get; }            // for display
    public string SizeText { get; }        // empty when the size is unknown
    public AttachmentRisk Risk { get; }
    public bool CanOpen { get; }           // false for SaveOnly
    public bool HasContent { get; }        // false below the depth limit: no action at all
    public string SafeFileName { get; }
}

// src\MailWarden.Presentation\IAttachmentHost.cs, implemented in App; tests use a fake
public interface IAttachmentHost
{
    Task<string?> PickSaveTargetAsync(string proposedFileName, CancellationToken ct = default); // null when cancelled
    Task<bool> OpenWithDefaultProgramAsync(string fullPath);                                    // false when Windows refused
}

// ReadingPaneViewModel (constructor gains IAttachmentHost and the open folder; ShellViewModel passes them on)
public IReadOnlyList<AttachmentRow> Attachments { get; }  // empty without a mail; replaces AttachmentNames in the view
public Task SaveAttachmentAsync(AttachmentRow row);       // never throws; a failure shows in AttachmentNotice
public Task OpenAttachmentAsync(AttachmentRow row);       // never throws; asks through ConfirmationRequested where A5/A6 say so
public string? AttachmentNotice { get; }                  // readable message of the last action; null when there is none

// src\MailWarden.Infrastructure\Composition\IMailboxReadModel.cs
Task CopyAttachmentAsync(MailMessage message, int attachmentIndex, MailAttachmentInfo expected, Stream destination, CancellationToken ct = default);
// FileNotFoundException without EML; MailContentException when the part at that position differs from expected (A9) or cannot be decoded;
// ArgumentOutOfRangeException for an index outside the list.

// src\MailWarden.Infrastructure\Mime\AttachmentFiles.cs
public sealed class AttachmentFiles(string openFolder)        // <data>\attachments-open
{
    public Task SaveAsync(Func<Stream, CancellationToken, Task> writeContent, string targetPath, CancellationToken ct = default);
    // Writes beside the target, marks it with Zone.Identifier ZoneId=3 as far as the drive allows, then moves over it; a missing mark is no failure.
    // On failure the temporary file is gone and an empty target from the dialog is removed.
    public Task<string> PrepareForOpeningAsync(Func<Stream, CancellationToken, Task> writeContent, string safeFileName, CancellationToken ct = default);
    // <openFolder>\<guid>\<safeFileName> with Zone.Identifier ZoneId=3; IOException when the mark cannot be written, and then no file stays.
    public void Clear();                                          // best effort: locked files stay until the next start
}
```
The confirmation goes through the existing `ConfirmationRequested` (`ShellViewModel.cs:26`, `:323`), so `ReadingPaneViewModel` gets a callback from the shell. `DataPaths` gets the open folder. `MainWindow` shows the rows, `App` implements `IAttachmentHost` and calls `Clear` on start and exit. The `FileSavePicker` needs `InitializeWithWindow`; launching uses the shell with the default program, never an arguments string from the mail. Neither has been measured yet, see below.

## Not in this AP
The account UI (`work\AP-02b\requirement.md`); sending (AP-05); the contact UI (AP-08); "alle Anhänge speichern"; a virus scan; previews inside the app.

## Decisions of Dennis (2026-11-02)
- Split: AP-04c = attachments first, AP-02b = account UI after it.
- Opening: three levels as in A5: programs and scripts save only, harmless types open at once, everything else after a question.
- Warning before opening: Junk, Quarantine **and Screener** (A6).

- Released: the pipeline may start (2026-11-02).

## Decisions of the planner (fix.1, 2026-11-02)
- A9: `CopyAttachmentAsync` takes the expected entry as a parameter, because the first contract gave it nothing to compare with. No state in the read model.
- A2: saved files carry the internet mark as a best effort.
- A11: a part below the depth limit gets no row, as measured; that is accepted.

## Open
- The planner measures `FileSavePicker` and launching a file in the spike while Dennis is at the computer: both show windows on the desktop. Tested already: the zone mark on `K:` (NTFS). If the measurement fails, `IAttachmentHost` is the only part that changes.

## Test cases
All with artificial data, fixtures under `tests\fixtures\`. The window runs in no test.
| # | Situation | Expected |
|---|---|---|
| 1 | `ToSafe` with `..\..\evil.exe`, `C:\x\a.pdf`, `a/b.txt`, `file.txt:stream`, `CON`, `con.txt`, `NUL.pdf`, `COM1`, `a.pdf.`, `a.pdf   `, empty, null, only dots, 400 characters | No path part, no `:`, no reserved name, no trailing dot or space, a length Windows accepts, fallback for the empty ones; a long name keeps its extension |
| 2 | RTLO or control characters in the name | Removed; the extension Windows would see is the one classified |
| 3 | `Classify`: every blocked type of A5, also `.EXE` and `rechnung.pdf.exe` | `SaveOnly` |
| 4 | `Classify`: `pdf`, `txt`, images, `docx`, `xlsx`, `pptx`, `odt`, `zip` | `Harmless` |
| 5 | `Classify`: `docm`, `xlsm`, `html`, `svg`, `xyz`, no extension, `exe.pdf` | `Ask` for all but `exe.pdf` (`Harmless`) |
| 6 | `MustAskBeforeOpening` for every risk and every role, also null | True for `Ask`, and for Junk, Quarantine, Screener and null; false for `Harmless` elsewhere |
| 7 | Mail with attachments, inline image, nested mail | Rows match `MailContent.Attachments`; the inline image has no row |
| 8 | `CopyAttachmentAsync` for base64, quoted-printable and 8bit parts | The exact bytes |
| 9 | Index outside; EML missing; `expected` differs in name, type or size, also without a read before | The stated exceptions, no bytes of another part |
| 10 | `SaveAsync` succeeds; the writer throws; the disk write fails | Target complete with stream `Zone.Identifier` holding `ZoneId=3` (on NTFS); no temporary file; an empty target removed |
| 11 | `PrepareForOpeningAsync` | File under `<openFolder>\<guid>\`, stream `Zone.Identifier` holds `ZoneId=3`, the content is right |
| 12 | `Clear` with a locked file | No exception; the rest is gone |
| 13 | Save cancelled in the dialog | Nothing written, no notice |
| 14 | Open harmless from the inbox | No question, host called with the prepared path |
| 15 | Open `Ask` type, answer no; answer yes | No / one call of the host |
| 16 | Open harmless from Junk, Quarantine, Screener, unknown role | A question first |
| 17 | Open `SaveOnly` | `CanOpen` false; calling it anyway opens nothing |
| 18 | Host returns false; decoding fails | `AttachmentNotice` set, no exception, no type-less silence; a critical log line holds the type name, never the file name |
| 19 | Another mail selected while saving | The save ends for the old mail; the rows and the notice belong to the new one |
| 20 | Open or save | No flag changes, no command queued |

Manual, Dennis (`work\manual-tests.md`), artificial test mails only:
| # | Situation | Expected |
|---|---|---|
| 21 | Save a PDF, an Office file and an image | The dialog proposes the name; the file opens afterwards |
| 22 | Open a PDF from the inbox | Opens at once; the file properties show the internet mark |
| 23 | Open from Junk and from the Screener | A question first |
| 24 | A mail with `rechnung.pdf.exe` | Only Speichern; no way to open it |
| 25 | Close the app, start it again | `attachments-open` is empty |

## Done when
S3 green on the merged branch, reviewer APPROVE, security-reviewer without Critical/High, documenter done, cases 21 to 25 by Dennis, CPM by Dennis.
