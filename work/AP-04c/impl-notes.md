# AP-04c - implementation notes (implement.1)

STEP-RESULT: implemented in full against the contract as updated by fix.1 and fix.2 (see below). No window was started.

Build from the worktree:
- `dotnet build src\MailWarden.App\MailWarden.App.csproj -warnaserror`: 0 warnings, 0 errors (holds App, Presentation, Infrastructure, Core).
- `dotnet build MailWarden.sln -warnaserror`: **1 error, in the tests**, expected and for the planner to merge with the tester:
  `tests\MailWarden.Presentation.Tests\TestSupport\Fakes.cs(215,46)`: CS0535, `FakeMailboxReadModel` lacks `IMailboxReadModel.CopyAttachmentAsync(MailMessage, int, MailAttachmentInfo, Stream, CancellationToken)`. `tests\` was neither read nor changed.

Branch `feat/ap-04c-attachments`. No new package, no csproj change.

## fix.2 (decisions of Dennis and the planner, recorded in `requirement.md`)
- **`Clear`:** own walk with a stack, one level per listing, attributes of the entry itself. Link to a folder: `Directory.Delete(recursive: false)`. Link to a file: `File.Delete`, no `SetAttributes`. The open folder itself as a link: only the link goes. A folder with a reparse tag of a cloud provider counts as a link too, so such a data folder would keep its contents: on the safe side.
- **A11:** `MailAttachmentInfo.IsAttachedMessage` is true for every listed `message/rfc822`, whatever its size, because the parser does not list that type unless it made a message of it. Other `message/*` types and `text/rfc822-headers` still only with `Size` null, so a measured delivery report stays what it is.
- **Role read again:** `currentRole` from `GetFolderRoleAsync` with the `FolderId` of the row. **Limit:** this sees a change of the role of that folder or a folder that is gone. A mail moved to another folder keeps its old `FolderId` in the row until the pane shows it again; the read model has no way to look a message up by id.
- **Question text:** one sentence for the role now, otherwise "Der Ordner dieser Mail hat sich geändert." when it differs.
- **Banner:** only for a failed save, as decided (`ShellBanner.BannerSource.AttachmentSave`). A failed open after a change of mail is logged only. The next attachment action clears the banner.
- **N7, one log line:** the host writes it, because only it has the code. The view model writes none for `false`. A fake that returns `false` therefore produces no line, case 18 in the requirement says so.
- **N11:** I read it as: the class comment said everything runs off the caller's thread, and `Clear` does not. Without the text of the review this is an assumption.
- **N6, wide forms:** only the six characters of the decision. Other wide forms (U+FF0A, U+FF1F, U+FF1C, U+FF1E, U+FF5C) stay as they are.
- **Removed:** `ReadingPaneViewModel.AttachmentNames`, `MailDisplayText.AttachmentNames`, `MaxAttachmentNames`, `UiText.MoreAttachments`. New: `UiText.AttachmentsNotListed`.
- `AtomicFile.WriteNewFileAsync` and `StreamBufferSize` (internal). `FileEmlStore` uses them inside its existing `try`, so its exceptions stay.

## fix.1 (decisions of the planner, recorded in `requirement.md`)
- **A9:** `CopyAttachmentAsync(message, attachmentIndex, expected, destination, ct)`. The reader compares the whole `MailAttachmentInfo` record with the part at that position and throws `MailContentException` on any difference, always, also without a read before. The remembered lists of implement.1 are gone, the read model holds no state for this. `AttachmentRow` keeps the entry (`internal Attachment`). A longer list in a changed EML with the same entry at that position still passes: only the addressed entry counts.
- **A2:** `SaveAsync` writes the mark on the temporary file, then moves. So the target never exists without the mark where the drive can hold one, and an overwritten target takes on the new stream. A drive without the stream saves without the mark, no notice and no log line. `TryWriteMark` is the one way for both uses; only `PrepareForOpeningAsync` turns `false` into the `IOException`.
- Unchanged: `HasContent`, `AcceptText`.

## Deviation from the contract
None after fix.1.

## Additions the contract leaves open
- **`ConfirmationRequest.AcceptText`** (`ShellViewModel.cs:29`, init property, default as before). The dialog button was fixed to "Endgültig löschen", which is wrong for an attachment. The attachment question uses "Trotzdem öffnen". `MainWindow` reads the property. Existing questions are unchanged.
- **`ShellViewModel` constructor**: two optional parameters at the end, `IAttachmentHost? attachmentHost = null, string? attachmentOpenFolder = null` (`ShellViewModel.cs:139`), so existing call sites keep compiling. Both or neither, otherwise `ArgumentException`. Without them the rows are listed and an action only sets `AttachmentNotice` to a fixed text. `App` passes both. Positional order after `documentBuilder`: host, folder.
- **The question takes its turn among the actions** (`ShellViewModel.ConfirmOpeningInTurnAsync`): the shell holds one dialog at a time, and `ContentDialog` allows only one.
- `MailAttachmentInfo.IsAttachedMessage` and `HasContent` are computed, so record equality and all constructor calls stay as they were.
- `AttachmentFileName.ToSafeForMessage` (`.eml`, A11).
- `ReadingPaneViewModel.HasAttachments`, `HasAttachmentNotice`, in the style of `HasContentNotice`, for the view.
- `AttachmentFiles.MarkFailureDataKey`: the mark failure is a plain `IOException` as the contract says, told apart through `Exception.Data` as in `MailWardenRuntime.InvalidFileNameDataKey`, so the notice can name the reason (A3).
- `DataPaths.AttachmentsOpenRoot`; `DataPaths.IsDosDeviceName` is now public, one rule for both uses. The set gained `CONIN$`, `CONOUT$`, `COM0`, `LPT0`. **This also tightens `ResolveMailPath`**; the paths the store makes are only ids, so nothing changes for them, but a test that expects `COM0` to be allowed would fail.
- `XamlFormat.AttachmentsLine` and `VisibleWhenAny` removed: nothing uses them after the view change.
- A double click on one row runs the action once while it runs (`busyAttachmentRows`).

## Meaning of `HasContent`
A part below the depth limit is **not listed at all** today (`ReadParts` before this AP, and unchanged): measured with 40 nested messages, 0 attachments and `HasOpaqueParts` true. So no such row exists. `HasContent` is false only for a part without decodable content (`Size` null and no message type). A row built through a fake with `message/rfc822` counts as an attached message, with or without a size: content true, `.eml`.

## The lists (A5)
Case-insensitive, last extension of the name after A4.

**SaveOnly**: `exe com scr pif cpl dll ocx sys drv msc gadget application appref-ms xbap vsto vsix app mlc msstyles webpnp ops osd osdx fxp prg plg pcd` · `bat cmd ps1 ps1xml ps2 ps2xml psc1 psc2 psd1 psm1 cdxml vb vbs vbe js jse ws wsf wsh wsc sct hta py pyc pyo pyw pyz pyzw pl jar jnlp sh ksh csh pssc msh msh1 msh2 mshxml msh1xml msh2xml bas vbp` · `msi msp mst msu appx appxbundle msix msixbundle appinstaller ms-appinstaller inf ins isp reg cab winget ppkg printerexport` · `lnk url website scf shb shs settingcontent-ms library-ms searchconnector-ms search-ms desklink mapimail xnk diagcab diagcfg diagpkg job theme themepack deskthemepack rdp wsb msrcincident pbk udl prf rat wcx appcontent-ms grp` · `iso img vhd vhdx wim nrg ccd mds` · `cer crt der p7b pfx p12 sst` · `chm hlp htc its cnt hpj` · `xll xla xlam wll ppa ppam mcf vsmacros vsw mdz ade adp mda mde mdb accda accdb accdc accde accdr accdt accdu accdw mdt mdw mad maf mag mam maq mar mas mau mav maw`

**Harmless**: `pdf txt png jpg jpeg gif bmp webp tif tiff docx xlsx pptx odt ods odp`

**Ask**: everything else, also no extension, `zip`, `html`, `svg`, `docm`, `xlsm`, `pptm`, `eml`.

## Rules of the safe name (A4)
`SafeDisplayText.Clean`, then by whole code points all remaining format characters (joiners too), private use and unassigned ones, variation selectors and other default-ignorable ones, broken surrogates, every kind of space to a plain one; last part after `\` or `/`; `:`, the other characters Windows refuses and U+FF02, U+FF0F, U+FF1A, U+FF3C, U+00A5, U+20A9 become `_`; leading spaces, trailing dots and spaces go; at most 120 characters with an extension of up to 32 characters kept; a reserved name gets a leading `_` (then 121). The opened file lands in a folder of 32 characters below `attachments-open`, so 120 keeps the path short enough for programs that know only 260 characters. `PrepareForOpeningAsync` checks again and throws `ArgumentException`: not a direct child of the new folder, stream name, device name, trailing dot.

## Host in App (`src\MailWarden.App\WindowAttachmentHost.cs`)
- `FileSavePicker` with `InitializeWithWindow`, start location Downloads, the full safe name as proposal, one file type: the extension when it is simple, otherwise `.` (`:58`). **Unmeasured.**
- Opening uses `Process.Start` with `UseShellExecute`, no arguments, working directory = the folder of this one file (`:68`), on the thread pool. Not `Launcher.LaunchFileAsync`: it is a WinRT call, which AP-04e showed can fail in the app without a package, and the requirement says "the shell". `ERROR_CANCELLED` (the user closed a question of Windows) returns true. A failure writes the one log line, with the type and the code. **Unmeasured.**
- The host is attached to the window after the shell exists (`AttachTo`). Nothing new from WinRT in the start path.

`App` clears the folder after the runtime holds the data lock, so a second instance that fails to start does not empty the folder of the first, and on exit before the runtime is disposed.

## Decisions a reviewer may ask about
- **EML stays open during the copy.** `FileEmlStore` opens with read and delete sharing, so the sync cannot replace this file while it is copied: `WriteAsync` fails with `StorageException` and that cycle retries (`IEmlStore.WriteAsync` documents it). It is the price of not reading into memory. The window is the copy time and only this one file; an EML is rewritten rarely. The database connection is not touched.
- Parsing uses `persistent: true` on the file stream with `MimeParserDefaults`. `SortParts` is the one rule for reading and copying.
- `MeasureDecodedSize` decodes each part once more during a copy, as the sort rule depends on the size.
- Questions hold fixed words only, as `ConfirmationRequest` says: no file name.
- The role from `ShowAsync` stays with the row, so a row cannot meet the role of another mail; it is compared with the role read before opening.

## Tested / untested
Only as self-control, no proof, tester stages not run. Console probe with artificial data, `_scratch\ap04c-probe` (ignored by git):
- Ran green: `ToSafe` for all names of test case 1 and 2; `Classify` samples; base64, quoted-printable and 8bit bytes; attached message and `text/rfc822-headers` written; index outside; a difference in name, type or size refused with no bytes written; `AttachmentFiles` (zone stream reads `ZoneId=3` on `K:`, also read from the saved target after the move and after overwriting a target, writer failure, empty target removed, existing target kept, cancelled save, locked and read-only files in `Clear`); read model on a real runtime with an artificial data folder (another entry refused without a read before, changed EML after a read, missing EML, no EML); view model through its internal constructor with a fake: rows, inbox without question, `Ask` no and yes, Junk, Quarantine, Screener and unknown role, host false, cancelled dialog, copy failure with type name in the log, another mail during a save.
- Ran green in fix.2: a junction below a guid folder, one directly in the open folder and the open folder itself as a junction: link gone, target and its read-only attribute unchanged; `.eml` names with and without a size, delivery report unchanged; `zip`, the new blocked types; the six characters, variation selectors, tag-plane and private-use characters, emoji kept; `ExtensionText`; `1048575` bytes reads `1,0 MB`; 503 attachments give 500 rows and the notice; notices of save and open; failed save after a change of mail in the banner and cleared by the next action; role changed or unreadable asks; yes after a change of mail opens nothing; `FileEmlStore` writes through the helper, overwrite with an open reader still `StorageException`.
- Measured: a 148 MB EML, 108 MB decoded, 10 MB allocated during the copy.

Untested: the window, XAML layout with the new column, `WindowAttachmentHost` (never run), a symbolic link to a file in `Clear` (no privilege here to make one; code path by reading only), the junction check against the code before fix.2 (not run, so the probe does not show it would have failed there), the mark on a drive without NTFS for opening and for saving (no such drive here; code paths by reading only), `ShellViewModel` wiring of the question (`ConfirmOpeningInTurnAsync`), the disk-full path.

## Questions
1. Role read again: is the limit above enough, or should a moved mail be found? That takes a new read member by message id. `src\MailWarden.Presentation\ReadingPaneViewModel.cs`, `ReadCurrentRoleAsync`.
