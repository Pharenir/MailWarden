# AP-03a fix.3 – implementation notes (builder)

Branch `fix/ap-03a-missed-uids` (from `main` @ `e0481ef`). Contract: `requirement.md`, section "Decisions fix.3" (G1–G3).
No tests written or changed.

## Changes
| File | Lines | Change |
|---|---|---|
| `src\MailWarden.Infrastructure\Sync\SyncEngine.cs` | 272–293 | G1: candidates = server UIDs not stored locally and `> SkippedUpToUid`; on the first sync (no state) additionally limited to the window UIDs. High-water mark and `lowestFailedUid` removed. |
| same | 337–342 | G2/R8: state saved with the unchanged floor (stored value, or the floor decided on this first sync); per-message failures no longer change it. Error text for per-message failures unchanged. |
| same | 472–496 | New `InitialFloorAsync`: no window or empty folder → (0, no filter); window search empty → (highest server UID, empty filter); otherwise (lowest window UID − 1, window set). |
| same | 14–35 | G3: class remarks describe floor, retry of missing UIDs, accepted consequence, per-message failures. |
| `src\MailWarden.Core\Sync\FolderSyncState.cs` | 7–14 | G2: `HighestSyncedUid` → `SkippedUpToUid`, XML doc per contract. |
| `src\MailWarden.Infrastructure\Storage\SchemaMigrations.cs` | 85–100 | Schema v3 appended: `RENAME COLUMN highest_synced_uid TO skipped_up_to_uid` + `UPDATE ... SET skipped_up_to_uid = 0`. v1/v2 untouched. |
| `src\MailWarden.Infrastructure\Storage\SqliteDatabase.cs` | 15 | `CurrentSchemaVersion` = 3. |
| `src\MailWarden.Infrastructure\Storage\SqliteFolderSyncStateStore.cs` | 9, 25, 38, 57–65 | New column/property name; behaviour unchanged. |
| `src\MailWarden.Infrastructure\Sync\SyncOptions.cs` | 6–11 | G3: doc of `InitialSyncDays` (floor only at first sync, accepted consequence). |
| `src\MailWarden.Infrastructure\Sync\FolderSyncResult.cs` | 17 | Doc of `Error`: removed the `HighestSyncedUid` sentence (failed UIDs are retried because they are still missing). |

## Decisions within the contract
- The window search (`SearchUidsAsync(windowStart)`) now runs on the first sync whenever a window is active and the
  server folder is not empty – also when no candidate is left (e.g. every UID already stored by an earlier failed
  run). Before, it ran only with candidates. Needed so the floor can always be decided. Empty folder → no search,
  floor 0 (contract).
- The window UID set is taken as the server returns it (not intersected with `SEARCH ALL`); the floor is
  `min(window UIDs) − 1`. Guard: a (invalid) UID 0 in the window gives floor 0 instead of wrapping to `uint.MaxValue`.
- `InitialWindowStart()` returning null (0 days or window before year 1) means "no window" → floor 0.

## Points for the tester / planner
- Case 5 depends on the UID order of the fixtures: it holds when the 60-day message has the lower UID. If the 60-day
  message had the higher UID, it lies above the floor and is fetched from the second run on – that is the accepted
  consequence of G1, not a bug.
- Build: `MailWarden.Infrastructure.csproj` and `MailWarden.App.csproj` with `-warnaserror` green. The full solution
  build fails only in `tests\MailWarden.Infrastructure.Tests` (21 errors CS1061 `HighestSyncedUid`, expected – tester
  branch).
- README / `docs\` not checked by the builder (planner checks per G3). `TODO.md:130` still mentions
  "FETCH-null vs. `HighestSyncedUid`" – resolved by this fix, planner may close it.
