# AP-03c – Security Review secure.1 (security-reviewer, opus; recorded by the planner)

**Scope:** `git diff 40dc8b7 f077770 -- src`, `_scratch\command-probe\Program.cs`, `work\AP-03c\requirement.md`, `impl-notes.md`.
**Trigger:** first write access to real mailboxes; user and classifier actions that delete.
**Benchmark:** OWASP Top 10, GDPR, project CLAUDE.md (security profile).
**Result:** 0 Critical · 0 High · 1 Medium · 7 Low. The reviewer could not write files, so the planner recorded this
content.

## M1 – UIDVALIDITY is bound at enqueue time, but the caller supplies the UIDs (Medium)
`SqliteMailCommandQueue.cs:47/60/86/109` (`RequireSyncedFolderAsync`), `:330`; the guard is `CommandExecutor.cs:218-222`.
- **Scenario:** the UI or classifier holds UIDs from UIDVALIDITY generation V1. A sync detects the reset to V2 and
  reloads the folder. Shift+Del from the stale view now stores an old UID together with V2. The guard passes, and UID
  MOVE + UID EXPUNGE permanently deletes another message.
- **Recommendation:** the enqueue methods receive the `expectedUidValidity` that the caller read together with the
  UIDs; a mismatch → `ArgumentException`. Additionally, every UID must be stored locally in the source folder.

## Low findings
- **N1** `MailKitCommandTransport.cs:147-155`, `CommandExecutor.cs:271-286`. A COPYUID map with unequal length or
  duplicate target UIDs is still paired by index, and the Trash UIDVALIDITY of the target UIDs is not compared with
  `SelectFolderAsync(trash)`.
  - Unequal/duplicate map → empty map.
  - Validity mismatch → do not expunge.
- **N2** `SqliteMailCommandQueue.cs:115-136`. The empty-folder snapshot is taken at enqueue time, not when the user
  looked at the folder.
  - AP-04: the confirmation shows the snapshot count.
  - Optional: an `upToUid` limit.
- **N3** No lock against parallel executors or processes (`CommandExecutor.cs:12/103-127`, WAL). The probe's run
  check can be bypassed through a TOCTOU race. Running a command twice is harmless, but `Attempts` can be lost.
  - AP-03d: a named mutex per data folder.
  - The probe runs only on a copy while no app or service is running.
- **N4** `MailKitCommandTransport.cs:189-194`. If STORE `\Deleted` succeeds but UID EXPUNGE fails, the flag stays,
  and another client may expunge the message later.
  - Best effort: `UID STORE -FLAGS.SILENT (\Deleted)`; keep the original exception.
- **N5** `SqliteMailCommandQueue.cs:407-439`. One unreadable row breaks `ListAsync` and `ListDueAsync` for the whole
  account.
  - Skip such rows one at a time and mark them Failed with a fixed text.
- **N6** (probe) `Program.cs:408-414`, `:181-201`, `:569-575`. Weak mapping of Trash records: a missing Message-ID
  matches another message without one. There is no re-check at delete, and no Trash UIDVALIDITY in the record.
- **N7** (probe) `Program.cs:162`, `:547-550`. Message-IDs are stored in `command-probe-trash.json` with no time limit.
  - Store a hash only, and delete the file after case 24.

## Checked without findings
- S1/S2: no `CloseAsync`, no `ExpungeAsync()` without UIDs; UIDPLUS is checked before sending; `\Deleted` is set only
  for exactly the UIDs; PERMANENTFLAGS are checked; MOVE needs MOVE + UIDPLUS (D1); `ToUniqueIds` rejects UID 0 and
  removes duplicates.
- Folder switch:
  - `selectedFolder = null` before every SELECT;
  - a `[READ-ONLY]` grant and UIDVALIDITY 0 are rejected;
  - dispose sends LOGOUT only.
- Account and folder checks run in both the queue and the executor; Trash is resolved per account; roles come from
  server flags.
- Stale commands:
  - a mismatch fails the command permanently without sending anything;
  - FK cascade on folder removal;
  - Failed is never reset.
- The partial delete (E3) loses nothing.
- The sync path is unchanged (separate interface and session).
- `LastError` holds a type name or a fixed text only; transport errors use own wording; SQL is parameterized; probe
  output holds counts, UIDs, sizes, flags and roles only.
- `dotnet list package --vulnerable --include-transitive`: no vulnerable packages (2026-10-31).
- Not verified by the security reviewer itself: the MailKit source claims. S1/S2 hold independently of them because
  the calls are checked before sending.
