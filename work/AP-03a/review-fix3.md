# AP-03a – review fix.3 (reviewer, opus)

**Verdict: APPROVE**

Scope: `git diff e0481ef 0fceccf -- src work/AP-03a/impl-notes-fix3.md` (builder commit only); `tests\` not read.
Build `dotnet build src\MailWarden.App\MailWarden.App.csproj -warnaserror` green (0 warnings, 0 errors).
Bundled SQLite: SQLitePCLRaw 2.1.13 → e_sqlite3 3.53.3 (source id 2026-06-26), `RENAME COLUMN` needs ≥ 3.25 → supported.

## Checked points
1. **G1 every run, floor fixed once** – `SyncEngine.cs:275-294`: with state the stored floor is used unchanged, without
   state `InitialFloorAsync` decides it; the window filter only applies when there is no state. `SyncEngine.cs:339-344`
   saves exactly that floor, never a value derived from fetch results. A failed folder saves nothing → still "first sync"
   next time (correct per R8). Floor rules in `InitialFloorAsync` (`SyncEngine.cs:475-496`) match G1 (no window / empty
   folder → 0, window empty → max server UID, otherwise min window UID − 1).
2. **Retry of per-message failures and null fetches** – failures are counted only (`SyncEngine.cs:307-311`), nothing
   moves the floor; a failed or null UID stays missing locally, is still a server UID and (with window) is ≥ min window
   UID > floor → candidate next pass. Error text identical to F1. review.2 NIT (window dropped after failure on first
   sync) is resolved.
3. **Migration v3** – appended (`SchemaMigrations.cs:85-100`), v1/v2 unedited; rename + reset in one migration, which
   the runner executes in its own transaction together with the version row (`SqliteDatabase.cs:266-298`). Existing v2:
   rename keeps folder_id/mod_seq/date, reset to 0. Fresh DB: v1 → v2 (creates `highest_synced_uid`) → v3 (renames)
   sequentially – works. No views/triggers reference `folder_sync_state`, so the rename has no side effects.
   `CurrentSchemaVersion` 3 and the consistency guard match.
4. **Performance** – normal runs: same round trips as before (OPEN, SEARCH ALL, one FETCH per candidate, flag FETCH).
   Extra cost only for UIDs the server keeps not delivering (one FETCH each per pass) – accepted by G1.
   **Builder decision (window search on every first sync with active window and non-empty folder): accepted.** It costs
   one SEARCH SINCE per folder once, only with `InitialSyncDays > 0` (default 0), and is required to decide the floor.
5. **Docs** – `FolderSyncState`, `SyncEngine` remarks, `SyncOptions`, `FolderSyncResult.Error`,
   `SqliteFolderSyncStateStore` describe G1/G2 and the accepted consequence. No leftover of `HighestSyncedUid` in `src\`
   (only the released v2 SQL, correct). Remaining wording see NIT.
6. **R3/R4/R7** – R3 deletes the state before `GetAsync` (`SyncEngine.cs:245` → `:275`), so a reset decides a new
   floor with the window again (contract). R4 unchanged; vanished UIDs are never candidates (not in SEARCH ALL).
   R7 unchanged: `localFlags` read before the pass, modseq from the old state; newly fetched messages carry their flags.

## BLOCK
None.

## SHOULD
- `PLAN.md:158`, `:159`, `:163` – rows not adapted to fix.3: row 2 "ältere werden auch später nicht nachgeladen" now has
  the accepted exception (old-dated UID above the floor), row 3 "lädt nur neue UIDs" is now "every missing UID above the
  floor, retried", row 7 still "Schema v2". Planner/documenter before merge (no Sollverhalten row for the retry behaviour).
- `README.md:51` – "schema version 2" and "how far each folder has been synced" (high-water wording) → schema 3 / floor.
  Documenter before merge.
- `K:\Projekte\Windows-Tools\MailWarden\_scratch\sync-check\Program.cs:102-103` (git-ignored, outside the diff) still
  reads `state?.HighestSyncedUid` – it will not compile against fix.3, and the "Done when" re-run of the planner needs it.
  Rename to `SkippedUpToUid` (and the output label) before the re-run.

## NIT
- `SyncEngine.cs:492-494` – a UID 0 in the window silently gives floor 0, so the window is dropped from the second pass on
  (stiller Fallback). F3 treats UIDVALIDITY 0 as Protocol error; consistent would be `MailTransportException(Protocol)`
  (folder Error, no state). Practically unreachable with MailKit (it does not deliver UID 0), so only a NIT.
- `SyncEngine.cs:486-495` (residual risk of the contract, not of the builder) – if the server's SEARCH SINCE on the first
  sync is incomplete in the same way as SEARCH ALL was in the finding, a too-high floor hides in-window UIDs permanently.
  Only with `InitialSyncDays > 0`. Suggest a TODO line for AP-03b (e.g. verify the floor via INTERNALDATE of the UIDs just
  below it).
- `SyncEngine.cs:347` "False when the UID vanished", `MailKitTransport.cs:185` "an empty answer means the UID no longer
  exists", `IMailTransport.cs:38` "null when the UID no longer exists" – the finding shows null can be transient;
  wording "not delivered (vanished or transient; retried next pass)" would match G1.
- `IFolderSyncStateStore.cs:6` and `SyncEngine.cs:56` still say "sync progress"; with a floor "sync state" fits better
  (`FolderSyncState` summary was already changed).
- `TODO.md:130-132` – "FETCH-null vs. HighestSyncedUid" and the review.2 NIT (first sync with failures) are resolved by
  fix.3; planner can close them.
