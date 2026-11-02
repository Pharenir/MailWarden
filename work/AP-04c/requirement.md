---
id: AP-04c
slug: attachments
title: Open and save attachments
test-scope: S2 during the rounds, S3 before the merge
test-filter: Category=feature:ui
status: DRAFT, waiting for the decisions of Dennis
---

# AP-04c: attachments (draft)

Prepared on the wish of Dennis (2026-11-02). **Not started.** The behaviour below follows the decisions at the end; Dennis still releases the requirement before the pipeline starts. Branches start from `main` after AP-04e and `chore/plan-archive` are merged.

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
| A2 | Speichern: the save dialog of Windows, proposed name from A4, stream copy of the decoded part, no full copy in memory. Nothing is saved without the dialog |
| A3 | Öffnen: decoded part into `<data>\attachments-open\<guid>\<safe name>`, marked as from the internet (`Zone.Identifier`, zone 3), then opened with the program Windows assigns. If the mark cannot be written (no NTFS), it is not opened and says so. The folder is emptied at the next start and on exit, files still locked stay until the next start |
| A4 | Safe file name, from the raw name of the part, not from the display text: no path parts, no `:` (alternate streams), control and bidi characters removed, trailing dots and spaces removed, reserved names (`CON`, `NUL`, `COM1` and so on) changed, length limit, a fallback name for an empty result |
| A5 | Risk levels by the last extension, after A4: **Blocked** (runs code: `exe`, `com`, `scr`, `pif`, `bat`, `cmd`, `ps1`, `vbs`, `js`, `jse`, `wsf`, `hta`, `msi`, `lnk`, `url`, `reg`, `jar`, `cpl`, `dll`, `iso`, `img`, `vhd`, `chm`, `appinstaller`, `msix` and similar) can only be saved. **Harmless** (`pdf`, `txt`, images, `docx`, `xlsx`, `pptx`, `odt`, `zip` and similar) opens at once. **Everything else**, also no extension and macro formats (`docm`, `xlsm`): a question first |
| A6 | From Junk, Quarantine and Screener every Öffnen asks first, with the reason, also for harmless types. Blocked stays blocked. The role of the folder decides, never its name |
| A7 | The claimed MIME type never decides anything: only the extension does, because that is what Windows uses to pick the program |
| A8 | Parts that cannot be decoded, a mail deleted meanwhile, a full disk or a dialog cancelled: a readable message or nothing, no crash, no half file left under the chosen name |
| A9 | The attachment is addressed by its position in the list of the same parse, checked against name and size, so a changed EML cannot deliver another part |

Contract sketch: `IMailboxReadModel.CopyAttachmentAsync(MailMessage message, int index, Stream destination, CancellationToken ct)`; `AttachmentFileName.ToSafe(string rawName, int index)` and `AttachmentRisk.Classify(string safeName)` in `Presentation`, testable without WinUI; `App` only holds the dialog and the launch.

Security review required (attachments, paths from foreign data).

## Not in this AP
The account UI (`work\AP-02b\requirement.md`); sending (AP-05); the contact UI (AP-08); "alle Anhänge speichern"; a virus scan; previews inside the app.

## Decisions of Dennis (2026-11-02)
- Split: AP-04c = attachments first, AP-02b = account UI after it.
- Opening: three levels as in A5: programs and scripts save only, harmless types open at once, everything else after a question.
- Warning before opening: Junk, Quarantine **and Screener** (A6).

## Open before the pipeline starts
- Dennis releases this requirement.
- The planner measures `FileSavePicker` and launching a file in the spike while Dennis is at the computer: both show windows on the desktop.
