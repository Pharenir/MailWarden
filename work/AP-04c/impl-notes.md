# AP-04c - implementation notes (implement.1)

STEP-RESULT: implemented in full against the contract, with the one deviation on A9 below. No window was started.

Build from the worktree:
- `dotnet build src\MailWarden.App\MailWarden.App.csproj -warnaserror`: 0 warnings, 0 errors (holds App, Presentation, Infrastructure, Core).
- `dotnet build MailWarden.sln -warnaserror`: **1 error, in the tests**, expected and for the planner to merge with the tester:
  `tests\MailWarden.Presentation.Tests\TestSupport\Fakes.cs(215,46)`: CS0535, `FakeMailboxReadModel` lacks `IMailboxReadModel.CopyAttachmentAsync(MailMessage, int, Stream, CancellationToken)`. `tests\` was neither read nor changed.

Branch `feat/ap-04c-attachments`. No new package, no csproj change.

## Deviation from the contract
**A9: the signature carries no expected values.** `CopyAttachmentAsync(message, index, destination, ct)` has nothing to compare name and size with. The signature is the contract and the tester's fake implements it, so it stayed. Instead `MailboxReadModel` remembers the attachment lists it delivered through `ReadContentAsync`, per EML path, the last 8 (`MailboxReadModel.cs:152`), and hands them to `MailContentReader.CopyAttachmentAsync`.
- Read, change the EML, copy: `MailContentException`. This is the flow of test case 9.
- Copy without a read before: no list known, so no check; the bytes of that position are delivered. Test case 8 can run that way.
- The comparison is the whole `MailAttachmentInfo` record: name, size and type. A9 names only name and size; a changed type is another part as well.
- A longer list in the new EML with the same entry at that position still passes: only the addressed entry counts.

If the planner prefers an explicit parameter (`MailAttachmentInfo expected`), that is a small change to the interface, `ReadingPaneViewModel.ContentWriterOf` and the fake, and the remembered lists go away. Stateless would be cleaner; I did not change a contract the tester writes against.

## Additions the contract leaves open
- **`ConfirmationRequest.AcceptText`** (`ShellViewModel.cs:29`, init property, default as before). The dialog button was fixed to "Endgültig löschen", which is wrong for an attachment. The attachment question uses "Trotzdem öffnen". `MainWindow` reads the property. Existing questions are unchanged.
- **`ShellViewModel` constructor**: two optional parameters at the end, `IAttachmentHost? attachmentHost = null, string? attachmentOpenFolder = null` (`ShellViewModel.cs:139`), so existing call sites keep compiling. Both or neither, otherwise `ArgumentException`. Without them the rows are listed and an action only sets `AttachmentNotice` to a fixed text. `App` passes both. Positional order after `documentBuilder`: host, folder.
- **The question takes its turn among the actions** (`ShellViewModel.ConfirmOpeningInTurnAsync`): the shell holds one dialog at a time, and `ContentDialog` allows only one.
- `MailAttachmentInfo.IsAttachedMessage` and `HasContent` are computed, so record equality and all constructor calls stay as they were.
- `AttachmentFileName.ToSafeForMessage` (`.eml`, A11).
- `ReadingPaneViewModel.HasAttachments`, `HasAttachmentNotice`, in the style of `HasContentNotice`, for the view.
- `AttachmentFiles.MarkFailureDataKey`: the mark failure is a plain `IOException` as the contract says, told apart through `Exception.Data` as in `MailWardenRuntime.InvalidFileNameDataKey`, so the notice can name the reason (A3).
- `DataPaths.AttachmentsOpenRoot`; `DataPaths.IsDosDeviceName` is now public, one rule for both uses. The set gained `CONIN$`, `CONOUT$`, `COM0`, `LPT0`. **This also tightens `ResolveMailPath`**; the paths the store makes are only ids, so nothing changes for them, but a test that expects `COM0` to be allowed would fail.
- `XamlFormat.AttachmentsLine` and `VisibleWhenAny` removed: nothing uses them after the view change. `AttachmentNames` stays, as allowed.
- A successful save or open leaves `AttachmentNotice` null. A double click on one row runs the action once while it runs (`busyAttachmentRows`).

## Meaning of `HasContent`
A part below the depth limit is **not listed at all** today (`ReadParts` before this AP, and unchanged): measured with 40 nested messages, 0 attachments and `HasOpaqueParts` true. So no such row exists. `HasContent` is false only for a part without decodable content (`Size` null and no message type). A row that a test builds through a fake with `Size` null and `message/rfc822` counts as an attached message: content true, `.eml`.

## The lists (A5)
Case-insensitive, last extension of the name after A4.

**SaveOnly**: `exe com scr pif cpl dll ocx sys drv msc gadget application appref-ms xbap vsto vsix` · `bat cmd ps1 ps1xml ps2 ps2xml psc1 psc2 psd1 psm1 cdxml vb vbs vbe js jse ws wsf wsh wsc sct hta py pyw pyz jar jnlp sh` · `msi msp mst msu appx appxbundle msix msixbundle appinstaller ms-appinstaller inf ins isp reg cab` · `lnk url website scf shb shs settingcontent-ms library-ms searchconnector-ms search-ms desklink mapimail xnk diagcab diagcfg diagpkg job theme themepack rdp wsb` · `iso img vhd vhdx wim` · `chm hlp htc` · `xll xla xlam wll ppa ppam ade adp mda mde accde mcf`

**Harmless**: `pdf txt png jpg jpeg gif bmp webp tif tiff docx xlsx pptx odt ods odp zip`

**Ask**: everything else, also no extension, `html`, `svg`, `docm`, `xlsm`, `pptm`, `eml`.

## Rules of the safe name (A4)
`SafeDisplayText.Clean`, then all remaining format characters (joiners too), broken surrogates, every kind of space to a plain one; last part after `\` or `/`; `:` and the other characters Windows refuses become `_`; leading spaces, trailing dots and spaces go; at most 120 characters with an extension of up to 32 characters kept; a reserved name gets a leading `_` (then 121). The opened file lands in a folder of 32 characters below `attachments-open`, so 120 keeps the path short enough for programs that know only 260 characters. `PrepareForOpeningAsync` checks again and throws `ArgumentException`: not a direct child of the new folder, stream name, device name, trailing dot.

## Host in App (`src\MailWarden.App\WindowAttachmentHost.cs`)
- `FileSavePicker` with `InitializeWithWindow`, start location Downloads, the full safe name as proposal, one file type: the extension when it is simple, otherwise `.` (`:58`). **Unmeasured.**
- Opening uses `Process.Start` with `UseShellExecute`, no arguments, working directory = the folder of this one file (`:68`), on the thread pool. Not `Launcher.LaunchFileAsync`: it is a WinRT call, which AP-04e showed can fail in the app without a package, and the requirement says "the shell". `ERROR_CANCELLED` (the user closed a question of Windows) returns true. **Unmeasured.**
- The host is attached to the window after the shell exists (`AttachTo`). Nothing new from WinRT in the start path.

`App` clears the folder after the runtime holds the data lock, so a second instance that fails to start does not empty the folder of the first, and on exit before the runtime is disposed.

## Decisions a reviewer may ask about
- **EML stays open during the copy.** `FileEmlStore` opens with read and delete sharing, so the sync cannot replace this file while it is copied: `WriteAsync` fails with `StorageException` and that cycle retries (`IEmlStore.WriteAsync` documents it). It is the price of not reading into memory. The window is the copy time and only this one file; an EML is rewritten rarely. The database connection is not touched.
- Parsing uses `persistent: true` on the file stream with `MimeParserDefaults`. `SortParts` is the one rule for reading and copying.
- `MeasureDecodedSize` decodes each part once more during a copy, as the sort rule depends on the size.
- Questions hold fixed words only, as `ConfirmationRequest` says: no file name.
- The role comes from `ShowAsync` and stays with the row, so a row cannot meet the role of another mail.

## Tested / untested
Only as self-control, no proof, tester stages not run. Console probe with artificial data, `_scratch\ap04c-probe` (ignored by git):
- Ran green: `ToSafe` for all names of test case 1 and 2; `Classify` samples; base64, quoted-printable and 8bit bytes; attached message and `text/rfc822-headers` written; index outside; changed record refused; `AttachmentFiles` (zone stream reads `ZoneId=3` on `K:`, writer failure, empty target removed, existing target kept, cancelled save, locked and read-only files in `Clear`); read model on a real runtime with an artificial data folder (changed EML, missing EML, no EML); view model through its internal constructor with a fake: rows, inbox without question, `Ask` no and yes, Junk, Quarantine, Screener and unknown role, host false, cancelled dialog, copy failure with type name in the log, another mail during a save.
- Measured: a 148 MB EML, 108 MB decoded, 10 MB allocated during the copy.

Untested: the window, XAML layout, `WindowAttachmentHost` (never run), the mark on a drive without NTFS (no such drive here; code path by reading only), `ShellViewModel` wiring of the question (`ConfirmOpeningInTurnAsync`), the disk-full path.

## Questions
1. A9, see above: keep the remembered lists, or an explicit parameter? `src\MailWarden.Infrastructure\Composition\MailboxReadModel.cs:152`.
2. Saved files carry no zone mark: A2 does not ask for it and a save target may have no NTFS. With a mark, a saved attachment would be treated the same as an opened one, which is what browsers do. For Dennis or the security review: `src\MailWarden.Infrastructure\Mime\AttachmentFiles.cs:140`.
3. `zip` opens at once by A5. Explorer shows the archive, and what is started from inside it depends on Explorer passing the mark on. As decided by Dennis; named for the security review.
