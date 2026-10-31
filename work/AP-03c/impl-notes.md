# AP-03c – implementation notes (builder, implement.1)

Branch `feat/ap-03c-command-queue`, base `main @ 40dc8b7` (requirement commit). Build:
`dotnet build MailWarden.sln -warnaserror` → `src` projects 0 warnings / 0 errors; the only error is in the test project
(`FakeMailTransportFactory` lacks `IMailTransportFactory.ConnectForCommandsAsync`, CS0535) – expected, the tester
adds the member. No tests were run or edited.

## Contract items

### C1 – Core types (`src\MailWarden.Core\Commands\`, `src\MailWarden.Core\Sync\`)
- `MailCommandKind.cs`, `MailCommandStatus.cs` – values as in the contract (stored as integers).
- `MailCommand.cs:27` – record exactly as in the contract.
- `IMailCommandQueue.cs:20` – signatures as in the contract; the XML doc lists the exception per validation rule
  (table below).
- `IMailCommandStore.cs:6` – signatures as in the contract.
- `Sync\IMailCommandTransport.cs:11` – signatures as in the contract.
- `Sync\IMailTransportFactory.cs:28` – `ConnectForCommandsAsync` added (abstract member, no default implementation).

### C2 – schema v4 + queue
- `Storage\SchemaMigrations.cs:98` – `Version4`: table `mail_commands` with the contract columns; FKs to `accounts`
  and `folders` (source and target) with `ON DELETE CASCADE`; indexes `(account_id, id)`, `(folder_id)`,
  `(target_folder_id)`. `SqliteDatabase.cs:15` → `CurrentSchemaVersion = 4`.
- `Storage\SqliteMailCommandQueue.cs:17` – implements both interfaces; constructor `(SqliteDatabase, TimeProvider)`.
  Uses `SqliteFolderStore`/`SqliteMessageStore` internally on the same database for lookups.
  - Enqueue: `:38` SetFlags, `:53` Move, `:80` MoveToTrash, `:103` DeletePermanently, `:115` EmptyFolder.
  - `:138` ListAsync (pending + failed, `ORDER BY id`), `:154` DiscardAsync, `:160` ListDueAsync, `:185`
    CompleteAsync, `:192` RecordFailureAsync.
  - Validation helpers `:227` NormalizeUids, `:253` ValidateFlagChange, `:283` RequireSyncedFolderAsync, `:298`
    FindTrashFolderAsync.

Validation → exception type (nothing stored in every case):

| Rule | Exception |
|---|---|
| `uids` null | `ArgumentNullException` |
| `uids` empty | `ArgumentException` |
| UID 0 contained | `ArgumentOutOfRangeException` |
| more than 1,000 distinct UIDs (counted after removing duplicates); not for EmptyFolder (Q2 fix) | `ArgumentOutOfRangeException` |
| SetFlags: a flag outside Seen/Flagged/Answered/Draft (incl. Deleted) in add or remove | `ArgumentOutOfRangeException` |
| SetFlags: both None / add ∩ remove ≠ ∅ | `ArgumentException` |
| unknown source folder / folder UIDVALIDITY 0 | `ArgumentException` |
| Move: unknown target / other account / same folder | `ArgumentException` |
| MoveToTrash: no Trash folder in the account | `InvalidOperationException` |
| MoveToTrash: source is the Trash folder | `ArgumentException` |
| EmptyFolder: role not Trash/Junk | `ArgumentException` |

Check order: argument-only checks (UIDs, flags) before database checks.

### C3 – MailKit command transport
- `Sync\MailKitCommandTransport.cs:31` (internal) – `SelectFolderAsync :70`, `MoveAsync :120`, `ExpungeAsync :168`,
  `StoreFlagsAsync :230` (Add/Remove), `DisposeAsync :203`.
- `Sync\MailKitTransportFactory.cs:36` `ConnectForCommandsAsync`; connect + XOAUTH2 now in the private helper
  `ConnectClientAsync :43`, shared with `ConnectAsync` (uses the B6 `ImapSession.ConnectAsync` as before).
- Error mapping: every MailKit call runs through `MailKitTransport.RunAsync` (same classification and wording as the
  sync transport, no server text, no token). Own Protocol failures (`Rejected(...)`) say "IMAP server host:port …".

### C4 – executor (`src\MailWarden.Infrastructure\Commands\`)
- `CommandOptions.cs:10` – `MaxAttempts` 1..20 validated like `SyncOptions` (positional + init).
- `CommandRunReport.cs:9`.
- `CommandExecutor.cs:36`:
  - E1 `ExecutePendingAsync :103` – `ListDueAsync(now)` first; empty → `(0,0,0)`, no factory call. Then the account
    (unknown → `ArgumentException`), one `ConnectForCommandsAsync`, commands in list order, transport disposed in
    `finally`.
  - E2 `ExecuteOnServerAsync :182` – local checks first (source missing or other account, target missing, Trash missing
    for a delete outside Trash/Junk), then `SelectFolderAsync(source)` `:218` and the UIDVALIDITY comparison `:221`.
  - E3 – switch in `ExecuteOnServerAsync`; `MoveToTrashAndExpungeAsync :261` for DeletePermanently outside Trash/Junk.
  - E4 `ApplyLocalEffectAsync :292` – SetFlags: `(stored | add) & ~remove` for every locally stored UID of the
    command (one `UpdateFlagsAsync`, skipped when none is stored); other kinds: EML delete skipping failures, then
    `DeleteByUidsAsync(source, uids)`. Every non-cancellation exception is swallowed, then `CompleteAsync`.
  - E5 `ExecuteCommandAsync :138`, `RecordAttemptAsync :356`, `RetryDelay :372`, `PropagatesUnrecorded :387`.
  - Fixed texts (public consts): `UidValidityChangedError` = "UIDVALIDITY changed", `SourceFolderMissingError`,
    `TargetFolderMissingError`, `NoTrashFolderError`. Other failures: `exception.GetType().Name` (e.g.
    "MailTransportException"), never the message.

### C5 – CommandProbe
- `_scratch\command-probe\` **in the worktree** (`.worktrees\feat-ap-03c\_scratch\command-probe`, git-ignored),
  because its project references point to `..\..\src` and only this branch has the new code. Copy of the SyncProbe
  structure; the original in the main checkout is unchanged. Not run.
- Actions as in C5 plus: `delete <uid> trash` (the Trash exception), `discard <id>` (to clean up failed commands).
- Guard: every enqueue needs exactly one folder with role Junk and the UID stored locally there. Exception
  `delete <uid> trash`: the UID must be a resolved Trash UID in `<dataRoot>\command-probe-trash.json` and stored
  locally in Trash. `trash <uid>` records (account, junk UID, Message-ID, size, date); after `run` (executor + sync
  pass) the record is resolved to the Trash UID by an exact, unique match on Message-ID + size + date (no match or
  several → stays unresolved, delete refused). `run` additionally refuses when any pending command of the account lies
  outside this guard. Output: counts, UIDs, sizes, flags, roles; Message-IDs are stored in the JSON but never printed.
- Note for the planner: opening a data root with this build migrates its DB to schema v4; the SyncProbe/sync-check
  built from `main` then refuse it (`SchemaVersionException`). Use a copy of `probe-data`, or build those tools in the
  worktree.

## MailKit evidence (S1/S2)

Source: MailKit tag **4.18.1** from GitHub (`jstedfast/MailKit`, `MailKit/Net/Imap/ImapFolder*.cs`). There is no
4.18.2 tag on GitHub; the 4.18.2 nuspec release notes say only "Bumped MimeKit dependency to 4.18.2", so the IMAP code
is the same as 4.18.1. Ich bin mir unsicher: this is inferred from the release notes, not from a 4.18.2 source tree.

| Question | Finding (4.18.1 source) | Consequence |
|---|---|---|
| `CloseAsync(false)` | `CloseAsync` (`ImapFolder.cs:781`) → `QueueCloseCommand` (`:668`): `expunge` → `"CLOSE"`; else `UNSELECT` if the server has UNSELECT; else **nothing is sent** (only local state reset, the server keeps the folder selected). | Transport never calls `CloseAsync`/`Close` at all. |
| Folder switch | `OpenAsync(access)` (`:661`) queues `SELECT`/`EXAMINE %F` (`QueueOpenCommand :557`); on success `Open()` (`:377`) only resets the old folder locally (`Reset`, `OnClosed`). No CLOSE, no UNSELECT. Re-opening the same folder sends SELECT again. RFC 3501 6.4.2: SELECT, EXAMINE and LOGOUT close the selected mailbox **without** expunge. | Leaving a folder = next `SelectFolderAsync` or dispose (LOGOUT). |
| Plain EXPUNGE call sites | `QueueExpungeCommand :4099` (`"EXPUNGE\r\n"`) is used by `Expunge()/ExpungeAsync()` without UIDs (`:4154`, `:4203`) and by `Expunge(uids)/ExpungeAsync(uids)` **when the server lacks UIDPLUS** (`:4286`, `:4380`: SEARCH deleted-not-in-set, STORE −\Deleted, plain EXPUNGE, STORE +\Deleted back). The only CLOSE is `Close(expunge: true)`. | `ExpungeAsync()` without UIDs is never called; `ExpungeAsync(uids)` only after a UIDPLUS check (twice: before anything is sent, and right before the call). |
| `ExpungeAsync(IList<UniqueId>)` with UIDPLUS | `CheckState(open, rw)`, empty list → return; then `UID EXPUNGE %s` per (split) UID set (`ExpungeAsync :4359`, command at `:4390`). It does **not** set `\Deleted` itself. | Transport first sends `UID STORE set +FLAGS.SILENT (\Deleted)` (`StoreAsync`, `ImapFolderFlags.cs:238`), then `UID EXPUNGE set` with the same set. |
| `\Deleted` actually stored | `QueueStoreCommands` formats `request.Flags & PermanentFlags` (`ImapFolderFlags.cs`); flags the folder cannot store are silently dropped. | `ExpungeAsync` requires `\Deleted` in PERMANENTFLAGS, Add/RemoveFlags require their flags there (Protocol before sending). |
| `MoveToAsync(uids, dest)` | (`ImapFolder.cs:5686`) no MOVE → `CopyToAsync` + `StoreAsync(+\Deleted)` + `ExpungeAsync(uids)` (→ plain EXPUNGE without UIDPLUS); MOVE without UIDPLUS → `GetIndexesAsync` + MOVE by **sequence numbers**, returns `UniqueIdMap.Empty`; MOVE + UIDPLUS → `UID MOVE %s %F` per (split) set. | Transport requires **MOVE and UIDPLUS** before anything is sent (see decision D1). |
| Return value / COPYUID | `ProcessMoveToResponse` + `GetCopiedUids` (`:5344`) read the COPYUID response code of each command (untagged `* OK [COPYUID …]` codes are added to `ic.RespCodes`, `ImapEngine.cs:3059`; the first COPYUID per command counts). `UniqueIdMap(src, dest)`; no COPYUID → `UniqueIdMap.Empty`. Sets keep server order (`UniqueIdSet.TryParse`, `SortOrder.None`), `TryGetValue` pairs by index. Outlook.com may send an empty COPYUID (`ImapEngine.cs:2090`) → empty sets. | Transport pairs `Source[i]`→`Destination[i]`, keeps only requested UIDs; an empty/short map only means "not expunged" (E3). |
| Dispose | `DisconnectAsync(quit: true)` sends only `LOGOUT` (`AsyncImapClient.cs:886`). | No CLOSE on dispose. |

## Decisions within the contract
- **D1 (S2, stricter):** `MoveAsync` requires UIDPLUS in addition to MOVE. Reason: without UIDPLUS MailKit converts the
  UIDs into sequence numbers and sends a non-UID MOVE (race with concurrent expunges, wrong messages could move).
  Outlook.com and Gmail both have UIDPLUS, so no practical loss. The executor cannot observe the difference (Protocol).
- **D2:** `SelectFolderAsync` fails with Protocol when the server grants only read-only access (`[READ-ONLY]`) or
  reports UIDVALIDITY 0; no folder counts as selected then.
- **D3:** `AddFlagsAsync`/`RemoveFlagsAsync` reject Deleted or unknown bits with `ArgumentOutOfRangeException`
  (`\Deleted` only inside `ExpungeAsync`); None → no command sent.
- **D4 (E5 network):** a network failure records the attempt with the same backoff (`now + 2^(attempts−1)` min, max 60)
  but is **never** permanent, regardless of `MaxAttempts`. A permanent failure stores `NextAttemptAt = null`.
- **D5 (E3):** when the COPYUID map yields no Trash UID, Trash is not selected and nothing is expunged; the command
  completes (case 11). Trash UIDs 0 from a broken map are dropped.
- **D6 (E4):** cancellation during the local effect or `CompleteAsync` propagates; the command stays pending.
  Executing it again is harmless (STORE idempotent; MOVE/EXPUNGE of vanished UIDs are no-ops; a repeated
  DeletePermanently outside Trash finds no COPYUID and leaves the messages in Trash).
- **D7:** no `IsConnected` check between commands. After a lost connection the next command fails with Network
  (`ServiceNotConnectedException` → Network), records one non-permanent attempt and ends the run (literal E5).
- **D8:** due check of `ListDueAsync` is done in .NET (`NextAttemptAt <= now`, exact instant) instead of SQLite
  `julianday` (milliseconds only). Queue order = `id` ascending (insertion order), not `created_at` text.
- **D9:** Trash folder = first folder of the account with role Trash in `IFolderStore.ListAsync` order, same rule in
  queue and executor.
- **D10:** a source folder that now belongs to another account counts as missing (permanent "Source folder no longer
  exists").

## Open questions
1. **E3 "Error text … in the report":** the contract says the command completes "with the Error text 'moved to trash,
   n not expunged' in the report", but `CommandRunReport(int Completed, int Retrying, int Failed)` has no error field
   and a completed command is deleted (no `LastError`). I kept the signature exactly; the text is not surfaced anywhere.
   Planner: add a field (e.g. `IReadOnlyList<string> Notes`) in a fix round, or drop the sentence.
2. **Resolved (planner Q2, fix after implement.1):** `EnqueueEmptyFolderAsync` is exempt from the 1,000-UID limit and
   creates one command with all local UIDs (`NormalizeUids(..., enforceLimit: false)`). Original question:
   **EmptyFolder with more than 1,000 local messages** threw `ArgumentOutOfRangeException` (common UID limit). A
   large Trash cannot be emptied in one call. Options: several commands (return type would change) or a higher limit
   for this case.
3. **Trash UIDVALIDITY between MOVE and SELECT(trash):** the COPYUID map carries the Trash UIDVALIDITY, the interface
   returns plain UIDs, and the executor does not compare the `SelectFolderAsync(trash)` result with anything. A Trash
   recreated in the milliseconds between the two commands could make the expunge hit other Trash messages. Accepted
   as negligible; could be closed by comparing with the COPYUID validity inside the transport.
4. Existing tests that assert `CurrentSchemaVersion == 3` (if any) need the tester's update (category c).

## For the tester
- Fakes must implement `IMailTransportFactory.ConnectForCommandsAsync` (the build of the test project fails until then).
- LastError values: the four public consts on `CommandExecutor`, or the exception type `Name`.
- Executor checks local folders before `SelectFolderAsync`; missing source/target/Trash → no transport call at all for
  that command (but the session is already open when other commands are due).
- Case 11: with an empty map, `SelectFolderAsync(trash)` is **not** called.
- SELECT per command: the executor calls `SelectFolderAsync(source)` once for **every** command, also when the
  previous command used the same folder (no reuse; the UIDVALIDITY guard runs per command). DeletePermanently
  outside Trash/Junk adds a second `SelectFolderAsync(trash)` when the map is not empty.

## fix.2 (after verify.1 PASS, review.1 APPROVE, secure.1 0/0/1/7)

Build `-warnaserror`: `src` projects and the probe 0 warnings / 0 errors. Test project: CS0738
`FakeCommandTransport.MoveAsync` must return `Task<CopyUidMap>`. The compiler stops after declaration errors, so the
call sites of the four Enqueue methods (new `expectedUidValidity` parameter) will show up as the next errors once the
fake is adapted. No tests run or edited.

### New signatures
```csharp
// MailWarden.Core.Sync
public sealed record CopyUidMap(uint DestinationUidValidity, IReadOnlyDictionary<uint, uint> Uids)
{
    public static CopyUidMap Empty { get; }   // (0, empty)
}
// IMailCommandTransport
Task<CopyUidMap> MoveAsync(IReadOnlyList<uint> uids, string targetFullName, CancellationToken ct = default);

// IMailCommandQueue (EnqueueEmptyFolderAsync unchanged)
Task<MailCommand> EnqueueSetFlagsAsync(long folderId, uint expectedUidValidity, IReadOnlyCollection<uint> uids,
    MessageFlags flagsToAdd, MessageFlags flagsToRemove, CancellationToken ct = default);
Task<MailCommand> EnqueueMoveAsync(long folderId, uint expectedUidValidity, IReadOnlyCollection<uint> uids,
    long targetFolderId, CancellationToken ct = default);
Task<MailCommand> EnqueueMoveToTrashAsync(long folderId, uint expectedUidValidity, IReadOnlyCollection<uint> uids,
    CancellationToken ct = default);
Task<MailCommand> EnqueueDeletePermanentlyAsync(long folderId, uint expectedUidValidity, IReadOnlyCollection<uint> uids,
    CancellationToken ct = default);

// MailWarden.Core.Model
public static class FolderLookup { public static MailFolder? FindTrash(IEnumerable<MailFolder> accountFolders); }
// SqliteMailCommandQueue
public const string UnreadableRowError = "unreadable command row";
```

### Items
- **F1** `SqliteMailCommandQueue.cs:304` `RequireSourceAsync`: after the existing checks (folder exists, UIDVALIDITY ≠ 0)
  `expectedUidValidity ≠ stored` → `ArgumentException` (param name `expectedUidValidity`), then every UID must be a key
  of `GetUidFlagsAsync(source)` → `ArgumentException` (param `uids`, message has the count only). Order: argument
  checks (UIDs, flags) → folder → never synced → expected UIDVALIDITY → local UIDs → target/Trash checks.
  `EnqueueEmptyFolderAsync` unchanged.
- **F2** transport `MailKitCommandTransport.cs:123` `MoveAsync` + `:291` `ToCopyUidMap`: `CopyUidMap.Empty` when the
  COPYUID sets are empty or of unequal length, a source or destination UID repeats or is 0, a source UID was not
  requested, the destination UIDs carry different UIDVALIDITY values, or that value is 0. Executor
  `CommandExecutor.cs:269` + `:295` `TrashUidsToExpunge`: checks the map again (fakes can return anything): empty,
  UIDVALIDITY 0, a key outside the command UIDs, a repeated or 0 value → nothing expunged, Trash not selected. Otherwise
  `SelectFolderAsync(trash)`; result ≠ `DestinationUidValidity` → nothing expunged. Both paths complete the command
  (E3, local rows of the source are removed; messages stay in Trash on the server).
- **F3** `MailKitCommandTransport.cs:190` + `:276` `UnmarkDeletedBestEffortAsync`: any exception from the UIDPLUS
  re-check or `UID EXPUNGE` after a successful STORE → `UID STORE set -FLAGS.SILENT (\Deleted)` with its own 10 s
  timeout (independent of the caller's token), every failure ignored, then the original exception is rethrown (and
  mapped by `RunAsync` as before). Covers review NIT 3 for the transport.
- **F4** `SqliteMailCommandQueue.cs:439` `ReadCommandsAsync` reads each row in its own try (`StorageException`,
  `InvalidCastException`, `FormatException`, `OverflowException`); an unreadable row becomes a stub (`:482`): id,
  queried account, raw kind (cast, may be undefined), raw folder id, UIDVALIDITY 0, no UIDs, no target, flags None,
  Status Failed, raw attempts (≥ 0), created-at if readable else `DateTimeOffset.MinValue`, LastError
  `UnreadableRowError`. `MarkUnreadableAsFailedAsync :421` persists status Failed + the fixed text. `ListAsync`
  returns the stub; `ListDueAsync` skips it. Review NIT 2: the corrupt-`uids` message names the column only.
- **F5** `Storage\EmlFiles.cs:18` `DeleteSkippingFailuresAsync(IEmlStore, IEnumerable<string?>, ct)` used by
  `SyncEngine.cs:269` (folder removal, behaviour unchanged) and `CommandExecutor.cs:348`
  (`command.Uids.Select(emlPaths.GetValueOrDefault)`). `Core\Model\FolderLookup.cs:13` `FindTrash` used by the queue
  (`:99`) and the executor (`:387`).
- NITs: NIT 1 documented in the executor remarks (`IsConnected` is not read, D7 unchanged); NIT 2 and NIT 3 see F4/F3;
  NIT 4 in the probe (run guard uses the single Junk/Trash folder).

### Probe (`_scratch\command-probe\Program.cs`, git-ignored, worktree)
- **N3:** prints a two-line warning at every start (copy of probe-data only, no app/service/other probe).
- **P2:** reads `<dataRoot>\command-probe-verified.json` (never writes it; case-insensitive JSON, extra keys ignored).
  The account's entry must exist once with UIDVALIDITY ≠ 0. Every Junk action needs the single Junk folder with the
  entry's `junkFolderId` and `uidValidity`, and each UID in `uids` (and stored locally). `empty-junk` refuses unless
  every local Junk UID is in the list; after enqueue the snapshot is checked again (discarded and refused if not). The
  run guard requires the same for every pending Junk command (folder id, command UIDVALIDITY, UIDs). Missing file or
  entry → all Junk actions refused. An account with `uids: []` can only `empty-junk` an empty Junk (nothing queued).
- **P1:** `trash <uid>` refuses without Message-ID or for a never-synced Trash; it records SHA-256(Message-ID) hex,
  size, date, Trash UIDVALIDITY, highest local Trash UID and the move command id. After `run` + sync a record is
  resolved only when the move command left the queue **and** the Junk UID is gone locally, and exactly one Trash
  message matches (UID > recorded highest, same hash, size, date, Trash UIDVALIDITY). `delete <uid> trash` re-checks
  all of it against the current local Trash row, enqueues with the recorded Trash UIDVALIDITY and stores the delete
  command id; the run guard accepts a Trash delete only for records bound to that command id. After the delete left the
  queue and the UID is gone locally, `run` removes the record. `discard` drops records of a discarded move and unbinds
  a discarded delete. A trash-record file in the old format (no hash) → every action refused until it is deleted.
- Residual (accepted by the decision): "highest local Trash UID" lags the server when Trash was not synced right
  before `trash`; an unsynced, older duplicate with the same Message-ID hash, size and date above that UID would make
  the match ambiguous or wrong. The planner syncs right before `trash` (the probe's `run` does that).
