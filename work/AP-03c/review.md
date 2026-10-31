# AP-03c – review.1 (reviewer)

Scope: `git diff 40dc8b7 f077770 -- src work/AP-03c/impl-notes.md PLAN.md` plus `_scratch\command-probe\Program.cs`
(worktree, git-ignored). `tests\` not read. Build `dotnet build MailWarden.sln -warnaserror` at HEAD 9ec6c90 (src
unchanged since f077770): 0 warnings, 0 errors.

## Verdict: APPROVE

No blocker. The SHOULD items on the probe (S1, S2) should be fixed **before case 24 runs on the real accounts**.
They concern the git-ignored tool, not the product code.

## Checked (OK)

- **S1/S2 against the MailKit 4.18.1 source** (`ImapFolder.cs`, `UniqueIdSet.cs`, from GitHub). The 4.18.2 XML docs in
  the NuGet cache describe the same fallbacks.
  - `QueueCloseCommand :668` sends CLOSE only for `expunge: true`. The transport never calls `Close*`.
  - `Open() :377` only resets the old folder locally, so a folder switch sends only SELECT.
  - `ExpungeAsync(uids) :4359` falls back to SEARCH, STORE and a plain EXPUNGE without UIDPLUS. The transport checks
    UIDPLUS before anything is sent (`MailKitCommandTransport.cs:172`) and again right before the call (`:193`).
  - `MoveToAsync :5686` falls back to COPY+STORE+EXPUNGE without MOVE, and to a MOVE by sequence numbers without
    UIDPLUS. The transport requires both capabilities before anything is sent (`:128-129`). MailKit checks the same
    `Engine.Capabilities`.
  - Dispose sends only LOGOUT (`:217`).
  - A plain `ExpungeAsync()` call does not exist in `src`.
  - Exceptions halfway: at worst, our own UIDs keep `\Deleted` (see NIT 3). No path leads to an untargeted expunge.
- **Exactness:**
  - `STORE +\Deleted` and `UID EXPUNGE` use the same UID set (`:188-194`).
  - Trash UIDs come only from COPYUID entries of requested source UIDs (`:145-155`, `CommandExecutor.cs:271-279`).
  - COPYUID pairing: `TryParse` keeps the server order. If MailKit splits the MOVE into chunks, `AddRange` can sort.
    Even then, every source UID in the map is a command UID, so the expunged Trash set is exactly the set of moved
    messages.
  - PERMANENTFLAGS are checked, so MailKit cannot drop a flag silently.
- **Guards:**
  - The local checks and the UIDVALIDITY comparison (`CommandExecutor.cs:188-222`) run before any write.
  - Enqueue validation follows C1, with the argument checks before the DB checks.
  - EmptyFolder takes a snapshot of the local UIDs and is exempt from the limit (Q2).
- **E4/E5/D4:**
  - The local effect runs only after server success.
  - Storage failures are swallowed and the command completes. Cancellation propagates.
  - Network failures record an attempt that never becomes permanent, then propagate.
  - Authentication failures and cancellation propagate without a recorded attempt.
  - The backoff is 1, 2, 4 … 32, 60 minutes.
  - `LastError` is always `GetType().Name` or a fixed const. The `RunAsync` mapping uses own wording.
- **Schema v4:**
  - The migration is appended only. `CurrentSchemaVersion` is 4.
  - There are 3 FK cascades (account, source, target), and `PRAGMA foreign_keys = ON` is active.
  - All SQL is parameterized.
- **Read-only sync:** `IMailTransport`, `MailKitTransport` and `SyncEngine` are unchanged. The factory only extracts
  `ConnectClientAsync`. `IMailCommandTransport` is a separate interface.
- **Probe guard:**
  - Every enqueue needs exactly one Junk folder and a locally stored Junk UID.
  - `run` refuses when any pending command lies outside the guard (`Program.cs:255-264`). Failed commands are not
    executed.

## BLOCK

None.

## SHOULD

1. **The probe's Trash exception can hit a Trash message that the probe did not put there.**
   - Where: `_scratch\command-probe\Program.cs:391-429`, matching at `:408-414`.
   - How: the record is resolved as soon as exactly one Trash message has the same Message-ID, size and date.
     Resolution does not wait for the probe's MoveToTrash command to complete. So if the move is still retrying or
     has failed, and Trash already holds a matching copy (a duplicate spam), that copy gets resolved.
   - When `MessageId` is null, `string.Equals(null, null)` is true, so only size and date must match.
   - Why it matters: this breaks Dennis's rule "no other message may be changed".
   - Proposal:
     - At `trash`, also store the highest local Trash UID (or UIDNEXT). Accept only a resolved UID above it; UIDs in
       a folder only grow.
     - Resolve only after the MoveToTrash command has left the queue.
     - Refuse `trash` when `MessageId` is null.
2. **`empty-junk` also takes Junk mails that arrived after Dennis checked the folder.**
   - Where: `Program.cs:203-219`.
   - Why it matters: only the mails that were in Junk at decision time are verified junk. The snapshot taken at
     enqueue includes every mail synced into Junk up to that point, and those can be false positives on real
     accounts.
   - Proposal: `empty-junk <expectedCount> <maxUid>`, refusing when the local count or highest UID differs. At the
     least, the planner runs `junk` right before and compares with the verified list.
3. **New duplicate: deleting EML files while skipping failures.**
   - Where: `CommandExecutor.cs:332-353` duplicates `SyncEngine.cs:591-609` (same loop, same catch).
   - Proposal: one internal helper in `Infrastructure` (for example `EmlFiles.DeleteSkippingFailuresAsync(IEmlStore,
     IEnumerable<string?>, ct)`), used by both callers. The executor would pass `uids.Select(emlPaths.GetValueOrDefault)`.
4. **New duplicate: finding the Trash folder (D9).**
   - Where: `CommandExecutor.cs:380-384` and `SqliteMailCommandQueue.cs:300-304`.
   - Risk: the same rule lives in two places and can drift. If they diverge, enqueue and executor pick different
     Trash folders.
   - Proposal: one shared helper, for example a static `FolderRoles.FindTrash(IEnumerable<MailFolder>)` in Core, which
     both callers use.

## NIT

1. `CommandExecutor.cs:123-127` (D7): the executor never reads `IsConnected`.
   - Effect: after an `ImapProtocolException`, MailKit disconnects. The next, innocent command then gets a Network
     attempt plus backoff.
   - Proposal: check `transport.IsConnected` before each command and end the run without recording an attempt. Or
     document that the interface member is unused for now.
2. `SqliteMailCommandQueue.cs:417`: on a corrupt row, the `StorageException` text contains the whole `uids` column.
   For an EmptyFolder command that can be thousands of UIDs. Proposal: shorten the text, or name only the command id.
3. `MailKitCommandTransport.cs:188-194`: if the run fails between `STORE +\Deleted` and `UID EXPUNGE`, our own UIDs keep
   `\Deleted`. After a discard, they stay marked on the server, and another client's plain EXPUNGE would remove them.
   That matches the user's intent and is harmless. Proposal: mention it in the remarks or in `docs\classifier.md`/README
   (documenter).
4. `Program.cs:340-350`: the run guard accepts every folder with role Junk, while enqueue requires exactly one.
   Proposal: use `RequireSingleFolder` in the run guard too, for consistency.

## Plan

PLAN rows AP-03c 1–6 are marked ✅ and match the diff. Row 7 is marked ⚠️ (probe run pending), which is correct. The
Q1 decision (no report field) is recorded in the requirement, and a TODO entry for AP-04 should follow (documenter).