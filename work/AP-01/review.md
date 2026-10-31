# AP-01 – Review (Reviewer, step review.1)

Verdict: **BLOCK**

Scope: `git diff chore/app-icon...feat/ap-01-skeleton-storage` without `tests\`, `test-plan.md`, `test-result.md`.
Basis: `work\AP-01\requirement.md` (contract, cases 1–29), `impl-notes.md`, project `CLAUDE.md`, global rules.
Build: `dotnet build MailWarden.sln -warnaserror` → 0 warnings, 0 errors (checked by reviewer).
Probes: `_scratch\ReviewProbe` (git-ignored, references Infrastructure) – results quoted below. Test suite not run,
`tests\` not read.

Overall: contract names, namespaces and signatures match exactly; schema v1 matches the spec; every row change and
its FTS entry share one transaction; all SQL is parameterised (only fixed fragments concatenated in
`SqliteMessageStore.ListAsync`); EML writes are temp file + `File.Move(overwrite)` in the same directory with cleanup
in `finally`; `FileEmlStore` routes every relative path (`WriteAsync`, `OpenReadAsync`, `Exists`, `DeleteAsync`)
through `DataPaths.ResolveMailPath`; argument checks (`Limit <= 0`, `Offset < 0`, `SearchAsync(null)`,
`DataPaths(null)`, relative root) throw the `ArgumentException` family; Core has no package references and no IO;
public members carry XML docs (enforced by CS1591 as error); WinUI csproj is unpackaged and self-contained as
specified. One contract guarantee is broken (BLOCK 1); the rest are improvements.

## BLOCK

1. **FTS sanitising can be broken by a NUL character** – `src\MailWarden.Infrastructure\Storage\MessageSearchIndex.cs:98`
   (used by `SqliteMessageStore.cs:182`).
   - What: `SearchAsync("alpha\0x")` → `StorageException: Searching messages failed: SQLite Error 1: 'unterminated string'`
     (probe). FTS5 reads the MATCH argument as a C string, so everything after `\0` – including the closing quote –
     is cut off.
   - Why: the contract says "user input can never break the FTS syntax"; a pasted search text with a control character
     produces a storage error instead of a (empty) result. It is also misclassified: `StorageException` is meant for
     non-programming storage failures, not for user input.
   - Fix: treat control characters as separators before tokenising, e.g. split on whitespace **and** `char.IsControl`
     (or replace control characters with a space first). Other control characters (`\u0001`) already pass (probe).
     Ask the tester to add `"alpha\0x"` to case 19.

## SHOULD

1. **Message account and folder account are not tied together** – `src\MailWarden.Infrastructure\Storage\SqliteMessageStore.cs:59-61`,
   `SchemaMigrations.cs:32-48` (builder question 2).
   - What (probe): a message with `AccountId = B` in account A's Inbox is accepted; `ListAsync(Role: Inbox, AccountId: B)`
     then returns it, and a second row with the same `(FolderId, Uid)` but another `AccountId` is accepted, so
     `GetByUidAsync(folderId, uid)` becomes ambiguous (returns an arbitrary row).
   - Why: group views filter by `m.account_id`, sync will look up by `(folder, uid)`; a single caller bug would
     silently corrupt both. Contract-conform, but the contract has a hole.
   - Fix (planner decision, cheapest now because v1 is not released): `UNIQUE (id, account_id)` on `folders` and
     `FOREIGN KEY (folder_id, account_id) REFERENCES folders (id, account_id) ON DELETE CASCADE` on `messages`
     (FK violation → `StorageException`, like case 11). Alternative without schema change: check the folder's account
     inside the upsert transaction and throw `StorageException`.

2. **Contract indexes cannot serve the sort order** – `SchemaMigrations.cs:50-51` vs. `SqliteMessageStore.cs:21`
   (builder question 1).
   - What: ordering by `julianday(m.date)` is correct (text order is not time order with mixed offsets), but the
     indexes on the raw `date` text are then unused weight. Probe: an expression index
     `messages (folder_id, julianday(date) DESC, id DESC)` is accepted by SQLite and gives
     `SEARCH m USING COVERING INDEX ... (folder_id=?)` without a temp B-tree.
   - Why: `SchemaMigrations` says "never edit a released entry"; once AP-01 is merged, fixing this needs migration v2.
   - Fix (planner decision): replace the two indexes in v1 with `(folder_id, julianday(date) DESC, id DESC)` and
     `(julianday(date) DESC, id DESC)`, and update the schema line in `requirement.md`.

3. **Comment contradicts the observed behaviour** – `src\MailWarden.Infrastructure\Storage\FileEmlStore.cs:82`.
   - What: "Delete sharing lets a later overwrite or delete succeed while a reader is still open." Overwrite does
     **not** succeed: probe → `StorageException: ... Access to the path is denied.` (also stated in `impl-notes.md`).
   - Why: messages/comments must match actual behaviour; AP-03/AP-04 will rely on this.
   - Fix: comment → "lets a delete succeed while a reader is open; overwriting an open file fails"; add the case to the
     `IEmlStore.WriteAsync` XML doc as `<exception cref="StorageException">`.

4. **`ResolveMailPath` accepts segments that Windows silently rewrites** – `src\MailWarden.Infrastructure\Storage\DataPaths.cs:69`.
   - What (probe): `"accounts/x.eml "` and `"accounts/x.eml."` resolve to `...\accounts\x.eml` (aliases);
     `"accounts/..."` resolves to the directory `...\mail\accounts\`, and `"..."` alone to `MailRoot\` itself – both pass
     the `StartsWith(MailRoot\)` check. No escape from the mail root, but the documented result "absolute path below
     MailRoot" (a file path) does not hold, and `WriteAsync("accounts/...")` ends in an "Access denied" storage error
     instead of an `ArgumentException`.
   - Why: path validation is the defence for later data from outside (security-reviewer trigger "Pfade aus fremden
     Daten"); it should reject everything it does not map 1:1.
   - Fix: additionally reject segments that end with `.` or a space (covers `...`, `x.`, `x `). Optional: reject DOS
     device names (`CON`, `NUL`, …) explicitly instead of relying on the final check (`NUL` is already rejected there).

5. **FTS row deletion duplicated outside `MessageSearchIndex`** – `src\MailWarden.Infrastructure\Storage\SqliteMessageStore.cs:256-259`.
   - What: `DeleteByUidsAsync` contains its own `DELETE FROM messages_fts ...`, while `MessageSearchIndex` claims to be
     "the single place that knows the index layout" (`MessageSearchIndex.cs:9`).
   - Why: new duplication of one responsibility; a later index change (e.g. contentless table) would miss this place.
   - Fix: add `MessageSearchIndex.DeleteForUidAsync(connection, transaction, folderId, uid, ct)` (or a variant taking
     the prepared command) and call it per UID; keep `MessageSearchIndex` as the only writer of `messages_fts`.

## NIT

1. `src\MailWarden.Infrastructure\Storage\SqliteMessageStore.cs:135-136` – missing blank line between the argument
   checks and `try` (all other methods have it).
2. `src\MailWarden.Infrastructure\Storage\FileEmlStore.cs:18` – exception doc says "is 0", but a negative `folderId`
   is rejected too → "folderId is not positive or uid is 0".
3. Consistency of argument checks: `RelativePathFor` rejects `uid == 0` / `folderId <= 0`, `SqliteMessageStore.UpsertAsync`
   stores `Uid = 0` without complaint; such a message can never get an EML path. Consider
   `ArgumentOutOfRangeException` for `Uid == 0` in `UpsertAsync` (IMAP UIDs are ≥ 1). Not required by the contract.
4. `SqliteMessageStore.cs:21` – `julianday()` has millisecond resolution; dates less than 1 ms apart tie and fall back
   to `id DESC` (probe: message 0.4 ms later sorted after the earlier one). Irrelevant for IMAP `Date` headers
   (second precision); mention it in the comment.
5. `src\MailWarden.App\App.xaml.cs:40` – a whitespace-only `MAILWARDEN_DATA_DIR` silently falls back to the default.
   Defensible ("unset"), but document it in the XML doc of `DataDirectoryVariable`.
6. `IAccountStore.cs:12`, `IFolderStore.cs:13` – ordering is implemented case-insensitively (`COLLATE NOCASE`,
   ASCII only); state that in the interface doc so it is part of the contract (see question 4).
7. `MailWarden.sln:1` – file starts with a UTF-8 BOM (global rule: UTF-8 without BOM). Written by `dotnet new sln`;
   harmless for the tooling, remove only if cheap.

## Recommendations to the builder's open questions
1. **Date ordering / indexes** – keep `julianday()` ordering (correct). Recommend expression indexes in v1 before merge
   (SHOULD 2); otherwise record a v2 migration in `TODO.md`. Planner decides.
2. **Account vs. folder consistency** – fix now with the composite FK in v1 (SHOULD 1); the probe shows real ambiguity
   for `GetByUidAsync`. Planner decides.
3. **Second `InitializeAsync` throws** – keep (fail fast, documented in the XML doc). Add it to the contract text so
   the tester may cover it.
4. **NOCASE ordering** – keep; document in the interface (NIT 6). Culture-aware sorting (umlauts) belongs to the UI
   layer if ever needed.
5. **Overwrite while a reader is open** – acceptable for AP-01; fix the misleading comment (SHOULD 3). For AP-03/AP-04:
   the reading pane should read/parse the EML into memory and close the file at once (MimeKit loads it anyway), and the
   command queue retries a failed overwrite. No `FILE_RENAME_FLAG_POSIX_SEMANTICS` P/Invoke now.
6. **WindowsAppSDK metapackage size** – keep the metapackage in AP-01; component packages are a package decision for
   Dennis in AP-13 (installer size).
7. **Invalid `MAILWARDEN_DATA_DIR` crashes silently** – acceptable for AP-01 (fail fast, the .NET runtime writes the
   exception to the Windows Application event log). Add a `TODO.md` entry for the AP that introduces the logger: log the
   startup failure and show a message box.
8. **`PLAN.md` "Umgesetzt"** – correct to follow the coordinator. From the review: rows 1–7 are implemented in
   `src\MailWarden.Infrastructure\Storage\*` (row 2 subject to BLOCK 1 for search robustness), row 8 is build-only; the
   planner marks them at verify.

## Questions to the planner
1. Schema v1 changes before merge (SHOULD 1 composite FK, SHOULD 2 expression indexes)? Both change the contract
   schema line and need tester follow-up (case 11-like test for the FK, no test change for the indexes).
2. Confirm for BLOCK 1 that control characters count as separators (not as an `ArgumentException`); case 18 then stays
   "whitespace only" and `"\0"` alone returns an empty result.
3. The worktree has an uncommitted change in `tests\MailWarden.Infrastructure.Tests\Storage\FileEmlStoreTests.cs`
   (seen in `git status`, not read). Not part of this review; please clarify whose it is before the next commit.
