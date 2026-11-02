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
| A1 | Each attachment of the open mail is a row or chip in the reading pane with name, size, and the actions Öffnen and Speichern. Names go through `SafeDisplayText` as today. The extension of the safe name stands in a column of its own, always visible, in capitals without the dot, empty without an extension. At most 500 rows; a notice names how many have no row, those can be neither saved nor opened here |
| A2 | Speichern: the save dialog of Windows, proposed name from A4, stream copy of the decoded part, no full copy in memory. Nothing is saved without the dialog. The saved file carries the internet mark as in A3 where the target can hold it; on a target without NTFS (a USB stick) the file is saved without the mark, this does not fail the save. `AttachmentNotice` shows "Anhang wird kopiert …" during the copy and "Anhang gespeichert." afterwards |
| A3 | Öffnen: decoded part into `<data>\attachments-open\<guid>\<safe name>`, marked as from the internet (`Zone.Identifier`, zone 3), then opened with the program Windows assigns. If the mark cannot be written (no NTFS), it is not opened and says so. `AttachmentNotice` shows "Anhang wird kopiert …" during the copy and is null after a successful start. The folder is emptied at the next start and on exit, files still locked stay until the next start. Emptying never follows a link (junction, symbolic link): the link itself is removed, its target stays untouched. It ends for any content of the folder and also removes names Windows would normalise (`.. `, `...`, `x.`). An open folder that is itself a link receives no file: `IOException` |
| A4 | Safe file name, from `MailAttachmentInfo.FileName` (cleaned, not cut): no path parts, no `:` (alternate streams), control, bidi and other invisible characters removed by whole code points, also variation selectors; wide forms of reserved characters and the other characters that common code pages turn into reserved ones become `_` (list in Decisions fix.3), trailing dots and spaces removed, reserved names (`CON`, `NUL`, `COM1` and so on) changed, length limit, a fallback name for an empty result |
| A5 | Risk levels by the last extension, after A4: **Blocked** (runs code: `exe`, `com`, `scr`, `pif`, `bat`, `cmd`, `ps1`, `vbs`, `js`, `jse`, `wsf`, `hta`, `msi`, `lnk`, `url`, `reg`, `jar`, `cpl`, `dll`, `iso`, `img`, `vhd`, `chm`, `appinstaller`, `msix` and similar, general types of Windows and Office, the final list in `impl-notes.md`; types of single programs stay out and land in Everything else) can only be saved. **Harmless** (`pdf`, `txt`, images, `docx`, `xlsx`, `pptx`, `odt` and similar) opens at once. **Everything else**, also no extension, `zip` and macro formats (`docm`, `xlsm`): a question first |
| A6 | From Junk, Quarantine, Screener and Trash every Öffnen asks first, with the reason, also for harmless types. Blocked stays blocked. The role of the folder decides, never its name. The role is read again before opening: when it differs from the role at showing, or cannot be read, a question comes. After the question the row must still belong to the shown mail, otherwise nothing opens. The same holds after the preparation: another mail by then means no start, the prepared file is removed, no notice. The question names no file |
| A7 | The claimed MIME type never decides anything: only the extension does, because that is what Windows uses to pick the program |
| A8 | Parts that cannot be decoded, a mail deleted meanwhile, a full disk or a dialog cancelled: a readable message or nothing, no crash, no half file left under the chosen name. A save that fails after another mail was selected shows in the banner of the shell, not on the new mail |
| A9 | The attachment is addressed by its position in `MailContent.Attachments` and checked against the entry the row was made from (`MailAttachmentInfo`: name, type and size) on every copy, also without a read before, so a changed EML cannot deliver another part: `MailContentException` then |
| A10 | An unknown folder role counts as Junk for A6, as it does for embedded images (`ReadingPaneViewModel.cs:309`) |
| A11 | A `message/rfc822` part is saved or opened as its message; its safe name always ends in `.eml` (`forwarded_a.eml`, `anhang-1.eml`, `evil.exe.eml`), level Everything else; a part below the depth limit has no action. Measured in implement.1: such a part gets no row at all (`MailContent.Attachments` leaves it out, the notice of `HasOpaqueParts` covers it), so `HasContent` is false only for a part without decodable content |
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
    public static bool MustAskBeforeOpening(AttachmentRisk risk, FolderRole? folderRole);  // Ask, or Junk, Quarantine, Screener, Trash, null
}

// src\MailWarden.Presentation\AttachmentRow.cs
public sealed class AttachmentRow
{
    public int Index { get; }
    public string Name { get; }            // for display
    public string SizeText { get; }        // empty when the size is unknown
    public string ExtensionText { get; }   // last extension of SafeFileName in capitals without the dot; empty without one
    public AttachmentRisk Risk { get; }
    public bool CanOpen { get; }           // false for SaveOnly
    public bool HasContent { get; }        // false for a part without decodable content: no action at all (a part below the depth limit gets no row, A11)
    public string SafeFileName { get; }
}

// src\MailWarden.Presentation\IAttachmentHost.cs, implemented in App; tests use a fake
public interface IAttachmentHost
{
    Task<string?> PickSaveTargetAsync(string proposedFileName, CancellationToken ct = default); // null when cancelled
    Task<bool> OpenWithDefaultProgramAsync(string fullPath);                                    // false when Windows refused; the host writes the one log line
}

// ReadingPaneViewModel (constructor gains IAttachmentHost and the open folder; ShellViewModel passes them on)
public const int MaxAttachmentRows = 500;
public IReadOnlyList<AttachmentRow> Attachments { get; }  // empty without a mail, at most MaxAttachmentRows; AttachmentNames is gone
public string? HiddenAttachmentsNotice { get; }           // how many attachments have no row; null when all have one
public bool HasAttachments { get; }                       // also HasAttachmentNotice, HasHiddenAttachments
public Task SaveAttachmentAsync(AttachmentRow row);       // never throws; a failure shows in AttachmentNotice
public Task OpenAttachmentAsync(AttachmentRow row);       // never throws; asks through ConfirmationRequested where A5/A6 say so
public string? AttachmentNotice { get; }                  // "Anhang wird kopiert …", "Anhang gespeichert." or why it failed; null when there is none, after a cancelled dialog, after a successful start

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
    // <openFolder>\<guid>\<safeFileName> with Zone.Identifier ZoneId=3; IOException when the mark cannot be written or the open folder is a link, and then no file stays.
    public void Discard(string preparedPath);                     // removes a prepared file that will not be opened
    public void Clear();                                          // best effort: locked files stay until the next start; a link is removed, never followed
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

## Decisions fix.3 (2026-11-02, planner, after review.2 and secure.2)
- `Clear` walks on `\\?\` paths, so it hits the exact name; a folder named `.. `, `...` or `. ` no longer resolves to the folder itself or the one above, and `x.` and `x ` go as well. A set of visited folders stays as the last guard: it must end in every case.
- Right before listing a folder its attributes are read again for a reparse point. The rest of that race is accepted.
- Files: `File.Delete` first, attributes only when `ReadOnly` stands in the way, and only that one, so a hard link does not take `Hidden` and the like from its target. A read-only hard link remains an accepted rest.
- A junction with `ReadOnly`: the attribute goes from the link itself, then a second delete; checked to be a junction first. A symbolic link that cannot be deleted stays. A normal read-only folder in the open folder gets its attribute reset as well.
- `PrepareForOpeningAsync` refuses when the open folder itself is a link.
- `InvalidCharacters` grows by U+00A6, U+01C0, U+02BA, U+030E, U+0589, U+201C, U+201D, U+201F, U+2033, U+2036, U+2044, U+2215, U+2216, U+2217, U+2223, U+2236, U+2329, U+232A, U+2758, U+3008, U+3009, beside U+FF02, U+FF0A, U+FF0F, U+FF1A, U+FF1C, U+FF1E, U+FF1F, U+FF3C, U+FF5C, U+00A5, U+20A9.
- After `PrepareForOpeningAsync` the file does not open when the mail has changed; it is removed, no notice.
- Blocked list: `mat psdm1 p7r spc crl cat p7c stl` added.
- Banner text of a failed save: only "Der Anhang konnte nicht gespeichert werden.", it also fits a mail that is gone or a part that does not match.

## Decisions fix.2 (2026-11-02)
Of Dennis:
- `zip` moves from Harmless to Everything else.
- Trash joins the roles that ask (A6).
- The extension stands in a column of its own in the row, always visible (`ExtensionText`). The question still names no file.

Of the planner, after review.1 and secure.1:
- `Clear` walks by itself and never follows a link. A link is removed as a link: `Directory.Delete(path, recursive: false)` for one to a folder, `File.Delete` for one to a file, no change of attributes on a link.
- A11: a part the parser made a message always gets `.eml`, level Ask.
- The blocked list grows by general types of Windows and Office. Types of single programs stay out and get the question.
- A save that fails after a change of mail goes to the banner of the shell (`ShellBanner`), the new mail shows nothing.
- Feedback for long copies through `AttachmentNotice` (A2, A3).
- `AtomicFile.WriteNewFileAsync` is the one way for `FileEmlStore` and `AttachmentFiles` to write a temporary file from a stream. `FileEmlStore` keeps its exceptions.
- `OpenAttachmentAsync` reads the role again; in doubt, and for another role, the question comes. After the question the row must still belong to the shown mail.
- `ToSafe` works on whole code points and removes invisible characters and variation selectors; eleven wide or code-page forms of reserved characters become `_`.
- At most 500 rows plus a notice. The parser stays unchanged.
- `AttachmentNames` and its helpers are removed.
- A refused start has one log line, written by the host, because only it knows the error code. Log lines name the type and the code, never a file name or path.

Accepted without code: see `TODO.md`.

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
| 2 | RTLO, control or invisible characters in the name, also outside the first plane (variation selectors, tag characters), wide forms of separators, U+00A5, U+20A9 | Removed or `_`; emoji stay; the extension Windows would see is the one classified |
| 3 | `Classify`: every blocked type of A5, also `.EXE` and `rechnung.pdf.exe` | `SaveOnly` |
| 4 | `Classify`: `pdf`, `txt`, images, `docx`, `xlsx`, `pptx`, `odt` | `Harmless` |
| 5 | `Classify`: `docm`, `xlsm`, `html`, `svg`, `xyz`, `zip`, no extension, `exe.pdf` | `Ask` for all but `exe.pdf` (`Harmless`) |
| 6 | `MustAskBeforeOpening` for every risk and every role, also null | True for `Ask`, and for Junk, Quarantine, Screener, Trash and null; false for `Harmless` elsewhere |
| 7 | Mail with attachments, inline image, nested mail; a mail with more than 500 attachments | Rows match `MailContent.Attachments`; the inline image has no row; the nested mail ends in `.eml` with level `Ask`; `ExtensionText` is the extension of `SafeFileName` in capitals; 500 rows and `HiddenAttachmentsNotice` |
| 8 | `CopyAttachmentAsync` for base64, quoted-printable and 8bit parts | The exact bytes |
| 9 | Index outside; EML missing; `expected` differs in name, type or size, also without a read before | The stated exceptions, no bytes of another part |
| 10 | `SaveAsync` succeeds; the writer throws; the disk write fails | Target complete with stream `Zone.Identifier` holding `ZoneId=3` (on NTFS); no temporary file; an empty target removed |
| 11 | `PrepareForOpeningAsync`; with the open folder as a link; `Discard` | File under `<openFolder>\<guid>\`, stream `Zone.Identifier` holds `ZoneId=3`, the content is right; `IOException`, nothing in the target of the link; file and its folder gone |
| 12 | `Clear` with a locked file; with a junction to a folder outside, also read-only; with entries named `.. `, `...`, `. `, `x.`, `x `; with a read-only folder; with a hidden file that has a hard link outside | No exception; it ends; the rest is gone; the link is gone, its target and its attributes unchanged; nothing outside is touched; `Hidden` stays on the hard link outside |
| 13 | Save cancelled in the dialog | Nothing written, no notice |
| 14 | Open harmless from the inbox | No question, host called with the prepared path, notice null afterwards |
| 15 | Open `Ask` type, answer no; answer yes | No / one call of the host |
| 16 | Open harmless from Junk, Quarantine, Screener, Trash, unknown role | A question first |
| 17 | Open `SaveOnly` | `CanOpen` false; calling it anyway opens nothing |
| 18 | Host returns false; decoding fails | `AttachmentNotice` set, no exception. Decoding: a critical log line holds the type name and the code, never the file name. Host false: the view model writes no line, the host has written the one line of a refused start |
| 19 | Another mail selected while saving; the save then succeeds; the save then fails | The save ends for the old mail; the rows and the notice belong to the new one; a failure shows in the banner of the shell, not as a notice; the next attachment action clears the banner |
| 19a | Save and open running | `AttachmentNotice` is "Anhang wird kopiert …" during the copy; "Anhang gespeichert." after a save; null after an open |
| 19b | The role read before opening differs from the role at showing, or the read fails | A question first, also for harmless types and when the new role asks for none |
| 19c | Another mail selected during the question, then yes; another mail selected during the preparation | Nothing opens; in the second case the prepared file is gone and there is no notice |
| 20 | Open or save | No flag changes, no command queued |

Manual, Dennis (`work\manual-tests.md`), artificial test mails only:
| # | Situation | Expected |
|---|---|---|
| 21 | Save a PDF, an Office file and an image | The dialog proposes the name; the file opens afterwards |
| 22 | Open a PDF from the inbox | Opens at once; the file properties show the internet mark |
| 23 | Open from Junk, from the Screener and from the Trash | A question first |
| 24 | A mail with `rechnung.pdf.exe` | Only Speichern; no way to open it |
| 25 | Close the app, start it again | `attachments-open` is empty |
| 26 | A mail with a very long attachment name and a `.zip` | The extension column stays visible; a question first |

## Done when
S3 green on the merged branch, reviewer APPROVE, security-reviewer without Critical/High, documenter done, cases 21 to 26 by Dennis, CPM by Dennis.

## Decisions after fix.2 (planner, 2026-11-02)
- The reading of the role uses the folder of the shown row. A mail moved to another folder while the list still shows the old row keeps its old folder until the next show. Accepted, the window is small and the list reloads: `TODO.md`.
- The five other wide forms of reserved characters (`＊`, `＜`, `＞`, `？`, `｜`, as code points in A4) also become `_`, one rule instead of a list that a reader has to question.
- A failed opening after a change of mail is only logged; the user is not waiting for a file then.
