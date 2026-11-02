# AP-04c - docs notes (document)

## Changed
- `README.md`: status paragraph (attachments can be opened and saved); reading pane bullet shortened; new bullet "Attachments" (rows, extension column, 500-row notice, Speichern, Öffnen, internet mark and protected view in Office, three levels, Junk/Quarantäne/Screener/Trash always ask, `attachments-open` emptied at start and exit, nothing on the server); data folder table row `attachments-open\`.
- `PRIVACY.md`: paragraph on the decoded copy in `attachments-open`; "Last updated" set to 2026-11-02.
- `work\manual-tests.md:133`: "Anhangnamen" replaced by the new row content.
- `CLAUDE.md` (main folder, not the worktree): one Stolperfalle on `\?\` paths, `DirectoryInfo` normalisation and `RecurseSubdirectories` following junctions; template `AttachmentFiles.Clear`. FileSavePicker/launch behaviour left out until measured.
- Not touched: `TODO.md`, `PLAN.md`, code, tests. CRLF kept, UTF-8 without BOM. No commit.

## Open points / differences
1. Task text says "Kopien entschlüsselter Anhänge"; the code decodes MIME (base64 etc.), it does not decrypt. PRIVACY.md says "decoded copy".
2. README says the program is chosen by Windows and promises nothing about programs. `WindowAttachmentHost` is unmeasured (`impl-notes.md`): "Öffnen" and the save dialog may still differ on a real run; README must be checked after the measurement.
3. README states that an unknown folder role asks (A10); it names no "unknown" in the UI text beyond "Der Ordner dieser Mail ist unbekannt." - fine, but unmeasured in the window.
4. Known limit not in README: a mail moved to another folder keeps its old folder role for the question until the list reloads (TODO.md, accepted).
5. README mentions the blocked-list examples only (`exe`, `bat`, `ps1`, `js`, `msi`, `lnk`); the full list is in `impl-notes.md`, not in user docs.
6. `CLAUDE.md` in the main folder is ignored by git (`.gitignore:11`) and not tracked: the edit is on disk only, nothing to commit.
