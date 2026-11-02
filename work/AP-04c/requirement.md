---
id: AP-04c
slug: attachments
title: Open and save attachments; account UI (AP-02b)
test-scope: S2 during the rounds, S3 before the merge
test-filter: Category=feature:ui
status: DRAFT, waiting for the decisions of Dennis
---

# AP-04c: attachments and account UI (draft)

Prepared on the wish of Dennis (2026-11-02). **Not started.** Every "Proposal" below is the planner's until Dennis decides. Branches start from `main` after AP-04e and `chore/plan-archive` are merged.

## What exists (read on `main`, 966df28)
- Attachments are only listed: `MailAttachmentInfo` holds a cleaned name, type and size, and "its content is never kept" (`Infrastructure\Mime\MailContent.cs:9`). The reading pane shows names only (`Presentation\ReadingPaneViewModel.cs:548`). The name is "never used as a path" (`MailContentReader.cs:183`).
- `IMailboxReadModel.ReadContentAsync` reads the EML into memory; there is no way to get the bytes of one part.
- Accounts: `AccountService` has `AddAccountAsync`, `ReauthenticateAsync`, `RemoveAccountAsync` (`Core\Accounts\AccountService.cs`), used only by the AuthProbe. `MailWardenRuntime` builds the token providers but keeps no `AccountService` (`Composition\MailWardenRuntime.cs:164-168`). `MailboxService.AccountsChangedAsync` starts and stops loops at run time (`Background\MailboxService.cs:288`).
- Removing an account cascades in the database (`SchemaMigrations.cs`), but **EML files stay on disk**: `IEmlStore` deletes single files only.
- The app starts nothing from a file today. Only `Launcher.LaunchUriAsync` for links (`App\MailWebViewHost.cs:145`).

## Lesson of AP-04e
WinRT calls can fail in the unpackaged app although they compile (`HighContrastChanged`, `0x80070490`). So `FileSavePicker`, launching a file and the sign-in from the UI are **measured in the theme spike or a sibling before the builder relies on them**, not assumed.

## Part A: attachments
### Proposal
| # | Behaviour |
|---|---|
| A1 | Each attachment of the open mail is a row or chip in the reading pane with name, size, and the actions Öffnen and Speichern. Names go through `SafeDisplayText` as today |
| A2 | Speichern: the save dialog of Windows, proposed name from A4, stream copy of the decoded part, no full copy in memory. Nothing is saved without the dialog |
| A3 | Öffnen: decoded part into `<data>\attachments-open\<guid>\<safe name>`, marked as from the internet (`Zone.Identifier`, zone 3), then opened with the program Windows assigns. If the mark cannot be written (no NTFS), it is not opened and says so. The folder is emptied at the next start and on exit, files still locked stay until the next start |
| A4 | Safe file name, from the raw name of the part, not from the display text: no path parts, no `:` (alternate streams), control and bidi characters removed, trailing dots and spaces removed, reserved names (`CON`, `NUL`, `COM1` and so on) changed, length limit, a fallback name for an empty result |
| A5 | Risk levels by the last extension, after A4: **Blocked** (runs code: `exe`, `com`, `scr`, `pif`, `bat`, `cmd`, `ps1`, `vbs`, `js`, `jse`, `wsf`, `hta`, `msi`, `lnk`, `url`, `reg`, `jar`, `cpl`, `dll`, `iso`, `img`, `vhd`, `chm`, `appinstaller`, `msix` and similar) can only be saved. **Harmless** (`pdf`, `txt`, images, `docx`, `xlsx`, `pptx`, `odt`, `zip` and similar) opens at once. **Everything else**, also no extension and macro formats (`docm`, `xlsm`): a question first |
| A6 | Wish from the test 2026-11-01: from Junk and Quarantine every Öffnen asks first, with the reason. Blocked stays blocked |
| A7 | The claimed MIME type never decides anything: only the extension does, because that is what Windows uses to pick the program |
| A8 | Parts that cannot be decoded, a mail deleted meanwhile, a full disk or a dialog cancelled: a readable message or nothing, no crash, no half file left under the chosen name |
| A9 | The attachment is addressed by its position in the list of the same parse, checked against name and size, so a changed EML cannot deliver another part |

Contract sketch: `IMailboxReadModel.CopyAttachmentAsync(MailMessage message, int index, Stream destination, CancellationToken ct)`; `AttachmentFileName.ToSafe(string rawName, int index)` and `AttachmentRisk.Classify(string safeName)` in `Presentation`, testable without WinUI; `App` only holds the dialog and the launch.

Security review required (attachments, paths from foreign data).

## Part B: account UI (AP-02b)
### Proposal
| # | Behaviour |
|---|---|
| B1 | A window or panel "Konten": every account with address, provider, status from `AccountSyncStatus` |
| B2 | Konto hinzufügen: Microsoft or Google, the browser opens, cancelling leaves nothing behind and is not an error. The new account appears in the tree and syncs without a restart (`AccountsChangedAsync`) |
| B3 | Neu anmelden, offered when the status is `NeedsSignIn`; a different account in the browser is refused with a clear message |
| B4 | Verbindung testen with the existing `ConnectionProbe`; the result names IMAP and SMTP, no server text |
| B5 | Konto entfernen: question with the address and the number of local mails. Sign-out, loop stopped, database cascade, **EML files of the account deleted**. **Nothing on the server changes** |
| B6 | Missing or wrong `auth-config.json`: a clear message instead of a crash; the rest of the app keeps working |
| B7 | Logging rule from AP-02a: of an `AuthenticationFailedException` only provider, reason, code; never `ToString()`. Tokens never leave `ICredentialStore` |
| B8 | Points from `TODO.md` this part closes: the totals of the hint after a removal, cleanup of orphaned grants and `*.tmp` at the start, `DataPaths.CredentialsDirectory`/`AuthConfigPath`, the shared token providers in one `AccountService` |

Security review required (sign-in, token cache, deleting).

## Not in this AP
Sending (AP-05); the contact UI (AP-08); `GenericImap` accounts; "alle Anhänge speichern"; a virus scan; previews inside the app.

## Decisions of Dennis
Filled in after the questions of 2026-11-02.
