STEP-RESULT: done (fix.2; case 28 needs real accounts – SyncProbe built, not run)

# AP-03a – Implementation notes (Builder)

Branch `feat/ap-03a-sync-down`. Build: `dotnet build MailWarden.sln -warnaserror` → 0 warnings, 0 errors (after
every file of this step). Nothing under `tests\` read or changed; `test-plan.md`/`test-result.md` not read.

## fix.2 (2026-10-31)
Basis: requirement.md "Decisions fix.2" F1–F7. Build `-warnaserror` (solution incl. tests, compile only) green;
SyncProbe builds. Tests not read or changed. SHOULD 2 (shared connect/list code with `ConnectionProbe`) not touched
(deferred).

| # | Change | File:line |
|---|---|---|
| F1 | R5 catches every non-aborting exception per UID (`AbortsSync` unchanged: Network/Authentication transport errors, `AuthenticationFailedException`, OCE still abort), counts it and continues; R7 runs normally. R8 saves `HighestSyncedUid = lowest failed UID − 1` (candidates ascending → first failure is the lowest), otherwise as before. Folder `Error` = `"<n> message(s) failed (first: <Type.Name>)"` when the folder otherwise completed; a later folder-level exception keeps the old format `"<Type>: <message>"` and the old state. Parse: `LoadAsync` **and** all field/`MimeBodyText.Extract` access in one try; any exception except OCE → unparseable. | `Sync\SyncEngine.cs:191-197`, `:273-297`, `:378-406`, `FolderProgress` |
| F2 | Folder removal (R1): each EML delete in its own try (all exceptions except OCE skipped), folder row deleted anyway. R3/R4 deletes inside a folder are unchanged (failure = folder error). | `Sync\SyncEngine.cs:171`, `:434-454` |
| F3 | Engine: `UidValidity == 0` after open → `MailTransportException(Protocol)` before R3, nothing stored or changed for the folder. Transport: `OpenFolderAsync` throws the same after EXAMINE (folder not marked open). | `Sync\SyncEngine.cs:214-220`; `Sync\MailKitTransport.cs:103-111` |
| F4 | No visible folder with role Inbox (the resolver never yields a hidden Inbox) → `MailTransportException(Protocol)` before any upsert or removal. | `Sync\SyncEngine.cs:132-138` |
| F5 | `title` added to the skipped elements (head itself kept); doc updated. | `Mime\MimeBodyText.cs:14-16`, `:27` |
| F6 | `MimeMessage.LoadAsync(stream, persistent: true, ct)` (EML stream stays open during extraction); `await using Stream remoteContent` directly after the null check (path computation now inside the ownership); `SyncAccountAsync` doc: Protocol for a list without inbox, StorageException for folder-row deletes, EML delete failures skipped (removal) or recorded (inside a folder). | `Sync\SyncEngine.cs:70-79`, `:346`, `:385` |
| F7 | `.gitignore`: `*.db`, `*.db-wal`, `*.db-shm`, `*.eml`, `!tests/fixtures/**/*.eml` (checked with `git check-ignore`: fixtures not ignored). | `.gitignore:18-22` |

- Deviations: none. `FetchAndStoreAsync` keeps one method; the remote stream now lives until the row is stored
  (previously disposed right after the EML write) – negligible, one message at a time.
- Getestet (builder self-check, not a tester run): `_scratch\sync-selfcheck\` **71/71 PASS** – all previous checks
  plus 29 (error format, state = failed − 1, retry fetches only 2 and 4), 30 (StorageException from a failing EML
  store decorator), 31 (file held with `FileShare.None` → folder removed, file left, no exception), 32, 33 (throws,
  folders untouched, transport disposed), 34. My own case 16 and case 3 were adapted to F1/F4.
- Info for the tester (punctual run `Category=feature:sync`, 76/78): two existing tests fail **because of the
  contract change**, not because of a defect – `SyncFolderTests.Folder_that_gets_the_All_flag_is_removed` (its folder
  list has no Inbox → F4) and `SyncErrorTests.Failure_in_the_middle_of_a_folder_keeps_stored_rows_and_old_state_and_resumes_next_time`
  (per-message failure now continues with the next UID → F1). Only the test output was read, not the test code.
- Ungetestet: F1 path "exception inside `MimeBodyText.Extract`" (no input found that makes MimeKit throw there;
  code review only); F3 in `MailKitTransport` (real server).
- Offene Fragen: keine.

## implement.1 (2026-10-31)

### Files
| File | Change |
|---|---|
| `src\MailWarden.Core\Sync\RemoteFolder.cs`, `RemoteFolderState.cs`, `RemoteFlags.cs`, `RemoteMessage.cs` | Contract records, one per file |
| `src\MailWarden.Core\Sync\MailTransportFailure.cs`, `MailTransportException.cs` | Failure enum + exception (undefined enum value → `ArgumentOutOfRangeException`) |
| `src\MailWarden.Core\Sync\IMailTransport.cs`, `IMailTransportFactory.cs` | Transport contract (read-only, no write operation exists) |
| `src\MailWarden.Core\Sync\FolderSyncState.cs`, `IFolderSyncStateStore.cs` | Sync progress contract |
| `src\MailWarden.Core\Storage\IMessageStore.cs` | + `GetUidFlagsAsync`, `GetUidEmlPathsAsync`, `UpdateFlagsAsync` |
| `src\MailWarden.Infrastructure\Storage\SqliteMessageStore.cs` | Implementations of the three methods (`UpdateFlagsAsync`: one transaction, prepared UPDATE per UID, FTS untouched, empty dictionary = no-op) |
| `src\MailWarden.Infrastructure\Storage\SchemaMigrations.cs` | Version 2 appended (`folder_sync_state`, SQL exactly as in the contract); version 1 untouched |
| `src\MailWarden.Infrastructure\Storage\SqliteDatabase.cs` | `CurrentSchemaVersion = 2` |
| `src\MailWarden.Infrastructure\Storage\SqliteFolderSyncStateStore.cs` | New store (upsert; unknown folder → FK violation → `StorageException`; delete missing = no-op; cascade via FK) |
| `src\MailWarden.Infrastructure\Mime\MimeBodyText.cs` | `Extract` + internal `Cut` (HTML via MimeKit `HtmlTokenizer`) |
| `src\MailWarden.Infrastructure\Sync\SyncOptions.cs`, `FolderSyncResult.cs`, `SyncReport.cs` | Contract records (`SyncOptions` validates in ctor **and** in `with`/init) |
| `src\MailWarden.Infrastructure\Sync\SyncEngine.cs` | R1–R11 |
| `src\MailWarden.Infrastructure\Sync\MailKitTransportFactory.cs` | Public factory (token first, then connect + XOAUTH2) |
| `src\MailWarden.Infrastructure\Sync\MailKitTransport.cs` | Internal `IMailTransport` on `ImapClient` incl. exception mapping |
| `src\MailWarden.Infrastructure\Providers\ConnectionProbe.cs` | `ToSocketOptions` private → internal (reused by the factory; behaviour unchanged) |
| `PLAN.md` | AP-03a column *Umgesetzt* filled |

### Interface deviations
None. All names, namespaces and signatures as in `requirement.md`. Additions that are not part of the contract:
`MimeBodyText.Cut` (internal), `MailKitTransport` (internal), `ConnectionProbe.ToSocketOptions` (private → internal).

### Decisions on unclear points
| # | Decision | File:line |
|---|---|---|
| D1 | `MimeBodyText`: order collapse → trim → cut taken literally, so a cut may end with a space (`"Hello World"`, 6 → `"Hello "`). A cut that would split a surrogate pair returns one character less. | `Mime\MimeBodyText.cs:76`, `:152` |
| D2 | HTML → text: every tag acts as a word boundary except inline elements (`a`, `b`, `span`, `font`, …), so `<p>a</p><p>b</p>` → `"a b"` and `wor<b>ld</b>` stays one word. Dropped: content of `script`/`style`, comments, CDATA, doctype. `<title>` content is **kept** (contract only names script/style) – see question Q1. | `Mime\MimeBodyText.cs:14-22`, `:82-123` |
| D3 | Preview = `Cut(Extract(message, 100_000), 200)` instead of a second `Extract(message, 200)` (one HTML pass per message); results are identical by construction (same collapse, same surrogate rule). | `Sync\SyncEngine.cs:335` |
| D4 | Date: header present **and** parseable → Date header; a present but unparseable Date header (MimeKit returns `MinValue`) also falls back to INTERNALDATE. | `Sync\SyncEngine.cs:358` |
| D5 | "Unparseable" = MimeKit throws `FormatException` (observed for control-character garbage, headerless text and empty input). Then MessageId null, Subject/From/preview/body "", HasAttachments false, Date = INTERNALDATE; EML stays. | `Sync\SyncEngine.cs:348` |
| D6 | Error text = `"<Type.Name>: <message>"` (short type name, e.g. `MailTransportException: …`). Counts already done before the failure stay in the folder result. | `Sync\SyncEngine.cs:183-186` |
| D7 | Abort set (R9): every `OperationCanceledException`, `AuthenticationFailedException`, `MailTransportException` Network/Authentication. Errors while reading/storing the **folder list** or removing folders (R1) are not inside a folder → they propagate. | `Sync\SyncEngine.cs:97-168`, `AbortsSync` |
| D8 | R7 calls `FetchFlagsAsync` only when at least one UID stored before this pass is still on the server (a first sync never fetches all flags). Messages added in the same pass are not compared (fresh flags). | `Sync\SyncEngine.cs:262` |
| D9 | R7 extra guard: stored modseq > server modseq (server reset without UIDVALIDITY change) → `FetchFlagsAsync(0)` instead of CHANGEDSINCE. Case 8 (10 → 12) unaffected. | `Sync\SyncEngine.cs:418` |
| D10 | R3 deletions are not counted in `Deleted` (R4 defines `Deleted`); re-fetched messages count in `Added`. | `Sync\SyncEngine.cs:199-207` |
| D11 | Initial window: local midnight `InitialSyncDays` back (`TimeProvider.GetLocalNow().Date`, offset from `TimeProvider.LocalTimeZone`); the window search runs only when the first sync has candidates. A window reaching before year 1 = no window. | `Sync\SyncEngine.cs:399-414`, `:237-248` |
| D12 | R1: folder names compared ordinal (case-sensitive, like the folder table key); a name listed twice by the server counts once. Upsert only for new folders or changed roles. | `Sync\SyncEngine.cs:104-147` |
| D13 | `HighestModSeq` stored bit-exact (`unchecked` ulong ↔ long) instead of rejecting values > `long.MaxValue` (CONDSTORE values are 63-bit; round-trip of any ulong works). | `Storage\SqliteFolderSyncStateStore.cs:38`, `:65` |
| D14 | Transport: `OpenFolderAsync` closes the previous folder implicitly (the next EXAMINE replaces the selection; no explicit CLOSE round trip). Namespace root with empty name skipped in the folder list. | `Sync\MailKitTransport.cs:98`, `:70` |
| D15 | Transport mapping: `ServiceNotConnectedException` and an `OperationCanceledException` without caller cancellation (library timeout) → Network. After an `ImapProtocolException` MailKit drops the connection, so the next folder fails with Network and aborts the pass (contract mapping of the first error stays Protocol). | `Sync\MailKitTransport.cs:271-282` |
| D16 | `FetchMessageAsync`: metadata FETCH (UID, FLAGS, INTERNALDATE, RFC822.SIZE) first – empty → null; then `GetStreamAsync` (BODY.PEEK[]); `MessageNotFoundException` in between → null. Missing INTERNALDATE → `MailTransportException(Protocol)`. Size from RFC822.SIZE, stream length as fallback. | `Sync\MailKitTransport.cs:166-215` |
| D17 | MailKit socket timeout 60 s per operation (ConnectionProbe uses 30 s; large messages on slow lines). Disconnect on dispose: best effort, 5 s. | `Sync\MailKitTransport.cs:23-25`, `:217-243` |

### Tested (builder self-check, not a tester run)
- `_scratch\sync-selfcheck\` (fake transport + temporary SQLite/EML under `bin\…\run-data\`, deleted afterwards):
  **63/63 PASS** – cases 1–27 of the requirement table (incl. 2/3 folder removal with EML/state/FTS, 5 window +
  never refetched, 6 only new UID fetched, 7 flags + FTS unchanged, 8 `FetchFlagsAsync(10)`, 9 `(0)`, 11 reset,
  12 vanished on fetch, 13 INTERNALDATE, 14 HTML/script/style/entities, 15 unparseable, 16 protocol error
  + old state kept + next run completes, 17 network abort + transport disposed + rows complete, 18 cancel after 2
  of 4 → resume without duplicates, 19 order Inbox/Junk/rest, 21, 22 v1 file → v2 with rows/FTS intact, 23, 24,
  25 incl. surrogate cut, 26 incl. `with`, 27 positive control two folders).
- Existing storage tests punctually (`FullyQualifiedName~Infrastructure.Tests.Storage`, via watchdog): 100/100
  passed after the schema bump.
- `_scratch\sync-probe\` builds (not started).

### Untested
- `MailKitTransport` / `MailKitTransportFactory` against a server (no fake IMAP server; contract: verified manually
  with the SyncProbe, case 28). In particular: CONDSTORE/HIGHESTMODSEQ reporting by MailKit with EXAMINE,
  `SearchQuery.DeliveredAfter` → IMAP SINCE (MailKit doc: date resolution only), BODY.PEEK[] in `GetStreamAsync`
  (MailKit always peeks; additionally the folder is EXAMINE'd), exception mapping of real failures.
- Real HTML mails (only artificial snippets).

### SyncProbe (case 28, Dennis)
`_scratch\sync-probe\` (git-ignored; planner moves it to the main checkout after the merge):
`dotnet run --project _scratch\sync-probe -- <dataRoot> <address> [initialDays]`. Needs `<dataRoot>\auth-config.json`
and a prior AuthProbe sign-in in `<dataRoot>\credentials`. Migrates the probe database to schema v2. Prints only
role/counts/error type per folder (numbered, no folder names), totals and duration. InteractionRequired → "sign in
with the AuthProbe first". Ctrl+C cancels cleanly.

### Open questions for the planner
- Q1: Should `<title>` (and possibly all of `<head>`) be dropped in `MimeBodyText` as well? Currently kept (contract
  literal); it would prefix previews of HTML mails that have a title.
- Q2: Local folders with role `Outbox` (later APs) would be removed by R1 because they are not on the server. Not
  relevant yet; R1 should exclude local-only roles once such folders exist.
- Q3: Removing a folder or deleting messages leaves the (empty) directory `accounts/<id>/folders/<folderId>/` and
  any orphan EML from an earlier crash; `IEmlStore` has no directory delete. Cleanup TODO for AP-03b/AP-04?
- Q4: `ProtocolException` handling in a live session (D15): MailKit disconnects, so one bad server answer aborts the
  whole pass. Reconnect belongs to AP-03b.
