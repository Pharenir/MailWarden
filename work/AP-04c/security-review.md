# AP-04c security review

## secure.1 (security-reviewer, 2026-11-02): 0 Critical, 0 High, 3 Medium, 10 Low
Written down by the planner, because the role cannot write. Installed programs of the machine are deliberately not named here, the repo is public.
- M1: `Clear` followed junctions and removed files outside the folder, read-only ones too (measured). Not reachable by a mail alone: it places one regular file per folder. A link can come from an extractor working there. Would be High if an unelevated extractor restores a junction; not settled. Fixed in fix.2.
- M2: `zip` as Harmless let its contents be started with one more click. Dennis moved it to Ask.
- M3: types that start code were only at Ask, siblings at SaveOnly. The general Windows and Office types moved to SaveOnly; fix.2 holds the list.
- L1 "harmless" depends on the program assigned on the machine, and the mark only helps where that program reads it (accepted, Dennis informed). L2 the role is a snapshot (fix.2 reads it again; a moved mail remains, accepted). L3 a question could concern the old mail (fix.2). L4 Trash lost the question (Dennis: Trash asks). L5 the name can hide the extension (Dennis: own column). L6 invisible and best-fit characters (fix.2). L7 no limit of parts: rows limited to 500; the parser itself goes to `TODO.md`. L8 filling the disk through opening: accepted, one click per copy. L9 leftovers after a crash and folder rights: accepted, AP-13. L10 a saved library in the downloads folder: accepted, it needs a deliberate confirmation.
- Held: about 120 hostile names against real files: the level always matched the extension on disk, no name reached outside the folder, `Harmless` never gave something executable. `CreateNew` and a new folder per file. Every way to the host goes through the read-back of the mark. `OpenAttachmentAsync` enforces `CanOpen` itself. Logs hold type names and fixed texts only.
- Not checked: `WindowAttachmentHost`, the reaction of programs to the mark, drives without NTFS, a full disk.

## secure.2 (security-reviewer, 2026-11-02): 0 Critical, 0 High, 0 Medium, 6 Low
Stand `dadca1f`. M1 closed in all asked positions, M2 and M3 done. N3, N5, N6, N7 hold: 141 hostile names and 200,000 random names, the column always shows the extension that is classified, and the row index stays right with more than 500 parts.
New: S1 `Clear` hung forever on a folder like `.. ` (normalised onto itself), also at the start of the app. S2 swapping a folder for a link between check and delete, narrow, needs a process of the same user. S3 a hardlink lost the attributes of its target. S4 a read-only link stayed, and the next opening wrote into its target. S5 eleven characters did not cover the best-fit mapping. S6 a file still opened after a change of mail during the copy.
All fixed in fix.3, except for the rest of S2 (accepted, `TODO.md`) and a read-only hardlink or symbolic link (accepted).
Not checked: the host, the window, symbolic links (no right here), real extractors, drives without NTFS.
