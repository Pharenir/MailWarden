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

# review.2 (fix.2, reviewer, 2026-10-31)

Scope: `git diff c2f7ced e4b68f7 -- src work/AP-03c/impl-notes.md` plus the probe `_scratch\command-probe\Program.cs`
(worktree, git-ignored). `tests\` was not read.
Build `dotnet build MailWarden.sln -warnaserror`: `src` 0 warnings / 0 errors. The only error is CS0738 in
`tests\...\FakeCommandTransport.cs:84` (expected, the tester is adapting it). Probe build: 0 / 0.

**Verdict: APPROVE.** The probe may be run against the real accounts.

## Checks

- **F1 (closes secure M1):** `SqliteMailCommandQueue.cs:304-330` `RequireSourceAsync` checks, in this order: folder
  exists, UIDVALIDITY ≠ 0, `expectedUidValidity` = stored value, every UID stored locally. All four Enqueue methods
  call it before `InsertAsync`. Move and MoveToTrash call it before the target/Trash checks. Nothing is stored when a
  check fails. If a sync resets the folder between the check and the insert, the command still carries the old
  UIDVALIDITY, so the executor rejects it (E2). Safe.
- **F2:** `MailKitCommandTransport.cs` `ToCopyUidMap` returns Empty on any of: unequal counts, a repeated or 0
  destination UID, mixed destination UIDVALIDITY or UIDVALIDITY 0, an unrequested source UID, a repeated source UID.
  The executor checks the map again in `CommandExecutor.cs` `TrashUidsToExpunge` (a key outside the command, a
  repeated or 0 value, UIDVALIDITY 0). It runs `SELECT trash` and compares the result with `DestinationUidValidity`
  before `ExpungeAsync`. The only other expunge path (`expungeInPlace`) affects only `command.Uids` in the source
  folder, after the UIDVALIDITY guard. No path expunges a UID that is not a mapped destination of this command.
- **F3:** `MailKitCommandTransport.cs:184-194` + `UnmarkDeletedBestEffortAsync`. The `try` covers only the UIDPLUS
  re-check and `UID EXPUNGE`. Cleanup is `UID STORE -FLAGS.SILENT (\Deleted)` on the same `uniqueIds`, with its own
  10 s token. Every failure is swallowed, then `throw;` rethrows the original exception, which `RunAsync` maps.
  `StoreAsync` with UIDs can send neither EXPUNGE nor CLOSE.
- **F4:** each row is read in its own `try` (`StorageException`, `InvalidCastException`, `FormatException`,
  `OverflowException`). All columns except `target_folder_id`/`next_attempt_at`/`last_error` are `NOT NULL`, so the
  uncaught `InvalidOperationException` (NULL) cannot occur. The stub is neutral (no UIDs, UIDVALIDITY 0) and is
  persisted as Failed with the fixed text. `ListDueAsync` skips it. The `uids` error text no longer contains the
  value (review.1 NIT 2).
- **F5:** `EmlFiles.DeleteSkippingFailuresAsync` behaves the same as both old copies (null/missing → skip, cancellation
  propagates). `FolderLookup.FindTrash` uses the same rule as both old copies. There is no other Trash lookup in
  `src`. Both review.1 SHOULDs are closed.
- **Probe P1/P2/N3:** each server-changing step goes through one of the guarded commands below. The sync pass opens
  folders read-only (`MailKitTransport.cs:97`, EXAMINE, so no `\Seen` side effect).
  - Junk: `RequireVerifiedJunkFolder` + `RequireVerifiedJunkUidAsync` check the single Junk folder, the folder id and
    UIDVALIDITY from the allow-list, that the UID is in the list, and that it is stored locally. SetFlags is limited
    to Seen. MoveToTrash only to the single Trash folder.
  - `empty-junk` checks local ⊆ list before the enqueue and the snapshot ⊆ list after it (discard on mismatch).
  - The run guard (`Program.cs:419-452`) checks every Pending command: folder id, command UIDVALIDITY = list value,
    UIDs ⊆ list, kind/flags/target. A Trash delete is accepted only when it is bound to its record by
    `DeleteCommandId`, with the same Trash UIDVALIDITY. The executor then compares the server UIDVALIDITY again.
  - Trash exception:
    - The record holds the hash, size, date, Trash UIDVALIDITY, highest Trash UID and move id.
    - It is resolved only after the move left the queue and the Junk UID is gone locally, and only on exactly one
      match.
    - At `delete <uid> trash`, `MatchesRecord` runs again against the current local row, and the enqueue uses the
      recorded UIDVALIDITY (the F1 check then applies again).
    - The record is removed after the delete completed and the UID is gone locally.
  - Failure paths fail safe. For example, if `SaveTrashRecords` fails after an enqueue, the run guard refuses the
    unbound delete.
  - The allow-list file in `_private` uses the expected keys (`accounts[].accountId/junkFolderId/uidValidity/uids`).
    I checked only the structure, not the values. Unknown or missing keys → refused.
  - I found no path that changes a non-junk message.

## BLOCK

None.

## SHOULD

None.

## NIT

1. `src\MailWarden.Core\Sync\IMailCommandTransport.cs:47-48`: the list of "inconsistent" cases in the return
   description misses "a repeated source UID" and "UIDVALIDITY 0". The implementation (`ToCopyUidMap`) and the class
   remarks already contain both. Proposal: add both cases to the interface description so that both texts list the
   same cases.
2. `SqliteMailCommandQueue.cs:318-320`: F1 loads the UID map of the whole source folder (`GetUidFlagsAsync`) for every
   Enqueue, although the check concerns at most 1,000 UIDs. This is acceptable for now; `EnqueueEmptyFolderAsync:137`
   uses the same pattern. For AP-04 (Del key in large Inbox folders), a targeted `uid IN (...)` query could replace
   it.
3. `Program.cs:180-216` (`trash <uid>`): a second `trash` for the same Junk UID is not refused.
   - Effect: two records with the same hash can both resolve to the same Trash UID. After the delete, the second
     record stays forever.
   - Risk: none. `MatchesRecord` fails once the row is gone.
   - Proposal: refuse `trash` when a record for this Junk UID already exists.
4. `Program.cs:199-200`: the recorded "highest Trash UID" is the local value. The residual risk is accepted by the
   decision. Proposal for the planner: use the order `run` (sync) → `trash` → `run`, with no other client active
   between these steps.
