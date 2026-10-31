---
id: AP-03a
slug: sync-down
title: Mail transport (IMAP via MailKit) and one-way sync server → local (folders, messages, flags, deletions, UIDVALIDITY, schema v2)
test-scope: S3
test-filter: "Category=feature:sync|Category=feature:storage|Category=smoke"
base: chore/session-handover (builder and tester)
status: ready (2026-10-31)
---

# AP-03a – Sync server → local

## Goal
Bring the mail of every account into the local store: folder list with roles, messages as EML + metadata + full-text
index, server flags, server-side deletions, UIDVALIDITY resets. Read-only towards the server. The command queue
(local → server), IDLE and the background schedule follow in AP-03b; the classifier is wired in AP-07c.

## Context
- Storage (AP-01): `IFolderStore`, `IMessageStore`, `IEmlStore`, `IAccountStore` in `src\MailWarden.Core\Storage\`;
  SQLite implementations and `SchemaMigrations.cs` in `src\MailWarden.Infrastructure\Storage\`. `IFolderStore.UpsertAsync`
  keys on (AccountId, FullName) and updates role + UIDVALIDITY; `IMessageStore.UpsertAsync` keys on (FolderId, Uid);
  `IEmlStore.RelativePathFor(accountId, folderId, uid)`; EML write is atomic.
- Accounts (AP-02a): `AccountService.GetAccessTokenAsync(accountId)` (silent token, `AuthenticationFailedException`
  with `InteractionRequired` when a sign-in is needed); `ProviderProfiles.For(provider)` (IMAP host/port/security);
  `FolderRoleResolver.Resolve(provider, fullName, separator, flags)` → `FolderClassification(Role, Hidden)`;
  `MailKitFolderFlags.From(FolderAttributes)`; `ConnectionProbe` shows the XOAUTH2 login with MailKit
  (`src\MailWarden.Infrastructure\Providers\ConnectionProbe.cs`).
- AP-00 findings: Outlook.com has no SPECIAL-USE; Gmail `[Gmail]/All Mail` contains everything (hidden by the resolver);
  Gmail labels appear as folders, so one message can be stored once per folder – accepted.
- Projektregeln: Core has no IO and no packages; secrets never in logs/exceptions; tests only with artificial data
  (`tests\fixtures\`), never against real servers.

## Non-goals
- No writes to the server (no flag changes, moves, deletes, folder creation) – AP-03b/AP-07c.
- No IDLE, no timer, no reconnect/backoff loop, no parallel sync of several accounts – AP-03b.
- No logging infrastructure (AP-04); errors are returned in the report or thrown.
- No generic IMAP password accounts (`GenericImap` → NotSupportedException in the transport factory).
- No deduplication of Gmail label copies; no attachment extraction; no size limit per message.

## Packages
None new (MailKit/MimeKit already referenced by Infrastructure).

## Contract (binding)

### `MailWarden.Core.Sync` (Core, no IO)
```csharp
public sealed record RemoteFolder(string FullName, char Separator, ServerFolderFlags Flags);

/// HighestModSeq 0 = server has no CONDSTORE for this folder.
public sealed record RemoteFolderState(uint UidValidity, uint UidNext, ulong HighestModSeq, int MessageCount);

public sealed record RemoteFlags(uint Uid, MessageFlags Flags);

/// Content: the complete raw message (RFC 5322), owned and disposed by the caller.
public sealed record RemoteMessage(uint Uid, MessageFlags Flags, DateTimeOffset InternalDate, long Size, Stream Content);

public enum MailTransportFailure { Network, Authentication, Protocol }

/// Message: one English sentence; never a token, never a full server dump.
public sealed class MailTransportException : Exception
{
    public MailTransportException(MailTransportFailure failure, string message, Exception? inner = null);
    public MailTransportFailure Failure { get; }
}

/// One authenticated IMAP session. Not thread-safe; one open folder at a time.
public interface IMailTransport : IAsyncDisposable
{
    /// All folders of the account (including NoSelect/NonExistent containers, the caller filters).
    Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct = default);

    /// Opens the folder READ-ONLY (EXAMINE) and closes the previously open one.
    Task<RemoteFolderState> OpenFolderAsync(string fullName, CancellationToken ct = default);

    /// UIDs of the open folder, ascending. since != null → only messages with INTERNALDATE on/after that day (IMAP SINCE).
    Task<IReadOnlyList<uint>> SearchUidsAsync(DateTimeOffset? since, CancellationToken ct = default);

    /// Flags of the open folder. changedSinceModSeq 0 → all messages; > 0 → only messages changed since (CONDSTORE).
    Task<IReadOnlyList<RemoteFlags>> FetchFlagsAsync(ulong changedSinceModSeq, CancellationToken ct = default);

    /// Complete message without setting \Seen (BODY.PEEK[]). Null when the UID no longer exists.
    Task<RemoteMessage?> FetchMessageAsync(uint uid, CancellationToken ct = default);
}

public interface IMailTransportFactory
{
    /// Connects and authenticates. Token/sign-in problems surface as AuthenticationFailedException (AP-02a type);
    /// connection/protocol problems as MailTransportException.
    Task<IMailTransport> ConnectAsync(Account account, CancellationToken ct = default);
}

/// Sync progress of one folder. HighestSyncedUid: every UID ≤ this value was either stored or deliberately skipped
/// (initial window). HighestModSeq: CONDSTORE value of the last completed flag sync (0 = none).
public sealed record FolderSyncState(long FolderId, uint HighestSyncedUid, ulong HighestModSeq, DateTimeOffset? LastSyncedAt);

public interface IFolderSyncStateStore
{
    Task<FolderSyncState?> GetAsync(long folderId, CancellationToken ct = default);   // unknown → null
    Task SaveAsync(FolderSyncState state, CancellationToken ct = default);           // upsert; unknown folder → StorageException
    Task DeleteAsync(long folderId, CancellationToken ct = default);                  // missing → no-op
}
```

### `IMessageStore` additions (Core interface + SQLite implementation)
```csharp
/// UID → flags of all messages of the folder; empty for an unknown folder.
Task<IReadOnlyDictionary<uint, MessageFlags>> GetUidFlagsAsync(long folderId, CancellationToken ct = default);

/// UID → EmlPath (null when the row has none) of all messages of the folder; empty for an unknown folder.
Task<IReadOnlyDictionary<uint, string?>> GetUidEmlPathsAsync(long folderId, CancellationToken ct = default);

/// Sets the flags of existing messages in one transaction; unknown UIDs are ignored. Does not touch the FTS index.
Task UpdateFlagsAsync(long folderId, IReadOnlyDictionary<uint, MessageFlags> flags, CancellationToken ct = default);
```

### Schema v2 (`SchemaMigrations`, append – never edit version 1)
```sql
CREATE TABLE folder_sync_state (
    folder_id          INTEGER NOT NULL PRIMARY KEY REFERENCES folders (id) ON DELETE CASCADE,
    highest_synced_uid INTEGER NOT NULL,
    highest_mod_seq    INTEGER NOT NULL,
    last_synced_at     TEXT    NULL -- ISO 8601 round-trip ("O")
);
```
`SqliteDatabase.CurrentSchemaVersion` = 2. A v1 database migrates in place; existing rows are untouched.
`SqliteFolderSyncStateStore(SqliteDatabase)` implements `IFolderSyncStateStore` (namespace `MailWarden.Infrastructure.Storage`).

### `MailWarden.Infrastructure.Mime`
```csharp
public static class MimeBodyText
{
    /// Plain text of the message for search and preview: TextBody when it contains non-whitespace, otherwise the
    /// HtmlBody converted to text (script/style content dropped, tags removed, entities decoded), otherwise "".
    /// Whitespace runs collapse to one space; result trimmed and cut to maxChars (> 0, else ArgumentOutOfRangeException).
    public static string Extract(MimeMessage message, int maxChars);
}
```

### `MailWarden.Infrastructure.Sync`
```csharp
/// InitialSyncDays: window of the first sync of a folder; 0 = everything; < 0 → ArgumentOutOfRangeException.
public sealed record SyncOptions(int InitialSyncDays = 90);

public sealed record FolderSyncResult(string FolderFullName, FolderRole Role, int Added, int FlagsUpdated, int Deleted,
    bool UidValidityReset, string? Error);

public sealed record SyncReport(Guid AccountId, int FoldersAdded, int FoldersRemoved, IReadOnlyList<FolderSyncResult> Folders);

public sealed class SyncEngine
{
    public SyncEngine(IMailTransportFactory transportFactory, IAccountStore accounts, IFolderStore folders,
        IMessageStore messages, IEmlStore emlStore, IFolderSyncStateStore syncStates, SyncOptions options,
        TimeProvider timeProvider);

    /// One complete pass over the account. Unknown account → ArgumentException.
    public Task<SyncReport> SyncAccountAsync(Guid accountId, CancellationToken ct = default);
}

/// MailKit implementation (IMAP, XOAUTH2 with AccountService tokens, endpoints from ProviderProfiles).
public sealed class MailKitTransportFactory(AccountService accountService) : IMailTransportFactory;
```

### Sync rules (SyncEngine)
- **R1 Folders.** Resolve every `RemoteFolder` with `FolderRoleResolver`. Hidden folders are never stored or synced.
  Visible ones are upserted (role from the resolver; UIDVALIDITY keeps the stored value, 0 for new folders). Stored
  folders that are missing on the server or now hidden are removed: delete the EML files of their messages (via
  `GetUidEmlPathsAsync`), then `IFolderStore.DeleteAsync` (cascades rows, FTS and sync state). `FoldersAdded` /
  `FoldersRemoved` count these.
- **R2 Order.** Inbox first, then Junk, then all other folders by FullName (ordinal, case-insensitive).
- **R3 UIDVALIDITY.** After `OpenFolderAsync`: stored UIDVALIDITY ≠ 0 and ≠ server value → delete all EML files and
  messages of the folder, delete its sync state, `UidValidityReset = true`; then store the server UIDVALIDITY.
- **R4 Deletions.** Server UIDs = `SearchUidsAsync(null)`. Local UIDs not on the server → delete their EML files, then
  `DeleteByUidsAsync`. `Deleted` counts them.
- **R5 New messages.** Candidates = server UIDs not stored locally and > `HighestSyncedUid` (0 without state). Without
  state and `InitialSyncDays > 0` the candidates are additionally limited to `SearchUidsAsync(today − InitialSyncDays)`
  (date from `TimeProvider`, local calendar day). Process ascending: fetch → write EML (`RelativePathFor`) → parse with
  MimeKit from the stored bytes → upsert row + body text. Fetch returns null → skip. Then
  `HighestSyncedUid = max(old value, highest server UID)` – older messages outside the initial window are never
  fetched later.
- **R6 Row mapping.** `MessageId` = MimeKit MessageId (may be null); `Subject` = Subject ?? ""; `FromAddress`/`FromName`
  = first mailbox of From (else "" / ""); `Date` = Date header when present, otherwise INTERNALDATE; `Flags` from the
  fetch; `HasAttachments` = MimeKit `Attachments` not empty; `Size` = `RemoteMessage.Size`; `EmlPath` = the relative
  path; `TextPreview` = `MimeBodyText.Extract(message, 200)`; body text for FTS = `MimeBodyText.Extract(message, 100_000)`.
  A message MimeKit cannot parse at all is still stored (EML + row with empty Subject/From/preview, INTERNALDATE).
- **R7 Flags.** For UIDs stored locally and present on the server: `FetchFlagsAsync(state.HighestModSeq)` when both the
  server `HighestModSeq` and the stored value are > 0, otherwise `FetchFlagsAsync(0)`. Differences → `UpdateFlagsAsync`;
  `FlagsUpdated` counts changed rows. Server wins (no local edits exist before AP-03b).
- **R8 State.** After a folder completed: save `FolderSyncState(folderId, HighestSyncedUid, server HighestModSeq,
  now)`. A folder that failed keeps its previous state (rows already stored stay; R5 skips them next time).
- **R9 Errors.** `MailTransportException` with `Network` or `Authentication`, `AuthenticationFailedException` and
  `OperationCanceledException` abort the whole sync (propagate). Any other exception inside one folder
  (`Protocol`, `StorageException`, …) is recorded as `Error` = "<ExceptionType>: <message>" for that folder and the
  next folder continues. The transport is disposed in every case.
- **R10 Read-only.** The engine never calls anything that changes the server; `\Seen` stays as it was.
- **R11 EML before row.** Write the EML file before the row; delete rows only after their files (missing file = fine).
  A crash between the two leaves at most an orphan file that the next run overwrites.

### MailKit transport
- Connect with `ProviderProfiles.For(account.Provider).Imap`, SSL on connect, XOAUTH2 (`SaslMechanismOAuth2`) with
  `AccountService.GetAccessTokenAsync(account.Id)`; the token never appears in exception messages.
- Exception mapping: socket/IO/timeouts → Network; `AuthenticationException` → Authentication;
  `ImapCommandException`/`ImapProtocolException`/`FolderNotFoundException` → Protocol.
- `OpenFolderAsync` uses `FolderAccess.ReadOnly`; `HighestModSeq` only when the server announces CONDSTORE.
- Flag mapping: `\Seen \Answered \Flagged \Deleted \Draft` → `MessageFlags`; keywords ignored.
- Not covered by automated tests (real servers); verified manually with the SyncProbe.

### SyncProbe (manual check for Dennis, git-ignored)
Console program `_scratch\sync-probe\` in the builder worktree (planner moves it to the main checkout after the
merge): `SyncProbe <dataRoot> <address> [initialDays]` uses the AP-02a credential store in `<dataRoot>\credentials`
(signed in before with the AuthProbe), syncs into `<dataRoot>` and prints only counts per folder (role, added, flags
updated, deleted, error type) and the duration – never subjects, addresses or bodies. Never opens a browser
(InteractionRequired → message "sign in with the AuthProbe first").

## Expected behaviour (test cases, fake transport + temporary SQLite/EML)
| # | Case | Expected |
|---|---|---|
| 1 | First sync, folders INBOX, Sent (\Sent), `[Gmail]/All Mail` (\All), `[Gmail]` (\NoSelect) | INBOX and Sent stored with roles; All Mail and container not stored; `FoldersAdded` 2 |
| 2 | Folder vanished on the server | folder, rows, EML files and sync state gone; `FoldersRemoved` 1 |
| 3 | Folder becomes hidden | treated like 2 |
| 4 | INBOX with 3 messages (artificial EML fixtures), InitialSyncDays 0 | 3 rows with mapped fields, 3 EML files, FTS finds a body word, state HighestSyncedUid = max UID |
| 5 | InitialSyncDays 30, messages at 10 and 60 days | only the 10-day message stored; later syncs never fetch the 60-day one |
| 6 | Second sync, one new UID | only that UID fetched (fake records fetch calls) |
| 7 | Server flags changed (Seen added) | row flags updated; `FlagsUpdated` 1; FTS text unchanged |
| 8 | CONDSTORE: stored modseq 10, server 12 | `FetchFlagsAsync(10)` called |
| 9 | No CONDSTORE (HighestModSeq 0) | `FetchFlagsAsync(0)` |
| 10 | Message deleted on the server | row and EML file gone; `Deleted` 1 |
| 11 | UIDVALIDITY changed | all old rows/files of the folder gone, messages re-fetched; `UidValidityReset` true |
| 12 | Fetch returns null for one UID | skipped, others stored, no error |
| 13 | Message without Date header | Date = INTERNALDATE |
| 14 | HTML-only message | preview/FTS contain the text, no tags, no script content |
| 15 | Unparseable bytes | stored with empty Subject/From, EML kept |
| 16 | Protocol error while syncing one folder | that folder has Error, the next folder still synced, failed folder keeps old state |
| 17 | Network error | SyncAccountAsync throws MailTransportException; already stored rows are complete (row ⇔ EML) |
| 18 | Cancellation after 2 of 4 messages, then a new run | no duplicates, all 4 present at the end |
| 19 | Order | Inbox opened first, Junk second (fake records order) |
| 20 | Engine never calls a write operation | fake has none; additionally `OpenFolderAsync` is the only open |
| 21 | Unknown account id | ArgumentException |
| 22 | v1 database file with data → open with v2 code | schema_version 2, table `folder_sync_state` exists, old rows intact |
| 23 | `SqliteFolderSyncStateStore`: save/get/overwrite, unknown folder → StorageException, folder delete cascades | as stated |
| 24 | `GetUidFlagsAsync`/`GetUidEmlPathsAsync`/`UpdateFlagsAsync` incl. unknown folder and unknown UIDs | as stated |
| 25 | `MimeBodyText.Extract` text/HTML/empty/maxChars ≤ 0 | as stated |
| 26 | `SyncOptions(-1)` | ArgumentOutOfRangeException |
| 27 | Positive control: full pass over two folders with mixed changes produces exactly the counts in the report | report matches store contents |
| 28 | Real accounts (SyncProbe, Dennis) | counts plausible against webmail; server unchanged (unread stays unread) |

## Done when
- Build `-warnaserror` green; S3 green (central building blocks touched: Core interfaces, schema, SyncEngine).
- Security review without Kritisch/Hoch (trigger: e-mail data on disk, data from the server).
- README updated by the documenter; PLAN rows AP-03a filled; case 28 handed to Dennis.
