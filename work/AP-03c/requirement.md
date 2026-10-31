---
id: AP-03c
slug: command-queue
title: Command queue local → server (flags, move, trash, permanent delete, empty folder) with retry
test-scope: S3
test-filter: "Category=feature:sync|Category=feature:storage|Category=smoke"
base: main @ 3d646e8 + this requirement (builder and tester)
status: ready (2026-10-31)
---

# AP-03c – Command queue local → server

## Goal
User actions (and later the classifier, AP-07c) change mail on the server through a persistent queue, never
directly. A command is stored first and then executed by an executor with its own IMAP session. Failures are
retried. The local store changes only after the server confirmed the change. This is the first AP that writes to the
server, so safety comes before convenience. The schedule and IDLE follow in **AP-03d** (split decided by Dennis
2026-10-31).

## Decisions by Dennis (2026-10-31)
- **Delete like Outlook:**
  - Delete / Del key → move to the account's Trash.
  - Shift+Delete → delete permanently at once.
  - "Ordner leeren" (empty folder) for Trash and Junk deletes the messages there permanently.
- **Real-account test:** the messages currently in the Junk folders are verified junk. They may be used for every
  command, including permanent delete and emptying the Junk folder. No other message on the real accounts may be
  changed.
- Split AP-03c (queue + executor) / AP-03d (background service: schedule + IDLE): accepted.

## Context
- Read side: `work\AP-03a\requirement.md` (R1–R11, fix.2, fix.3) and `work\AP-03b\requirement.md` (B1–B7, fix.2).
  `SyncEngine` and the read-only `IMailTransport` stay unchanged. The read-only guarantee of the sync pass (AP-03a
  case 20) must still hold.
- Storage: schema v3 (`SchemaMigrations`), `IMessageStore` (`GetUidFlagsAsync`, `UpdateFlagsAsync`,
  `GetUidEmlPathsAsync`, `DeleteByUidsAsync`), `IEmlStore`, `IFolderStore`.
- IMAP: Outlook.com and Gmail both support MOVE (RFC 6851) and UIDPLUS (RFC 4315). Gmail specifics:
  - MOVE out of a label folder removes that label;
  - Trash is `[Gmail]/Bin` (role Trash);
  - Junk is `[Gmail]/Spam`;
  - a message expunged from Bin or Spam is gone for good.

## Non-goals
- No schedule, no timer, no IDLE, no automatic run (AP-03d). In AP-03c the caller runs the executor, as the probe
  does. AP-03d and AP-04 wire it up later.
- No UI and no keyboard handling (AP-04). The UI will map Del / Shift+Del / "Ordner leeren" to the methods below.
- No optimistic local change at enqueue time. The UI can show pending commands from `ListAsync` (AP-04).
- No folder creation (CREATE) and no APPEND (AP-07c / AP-05).
- No new packages.

## Contract (binding)

### C1 – Core types (`MailWarden.Core.Commands`)
```csharp
public enum MailCommandKind { SetFlags = 1, Move = 2, MoveToTrash = 3, DeletePermanently = 4 }

public enum MailCommandStatus { Pending = 1, Failed = 2 }   // completed commands are removed from the queue

/// A queued server change for messages of one folder. FolderUidValidity is the UIDVALIDITY of the source folder at
/// enqueue time; the executor never acts when the server reports another value. TargetFolderId only for Move and
/// MoveToTrash. FlagsToAdd/FlagsToRemove only for SetFlags. LastError holds an exception type name or a fixed
/// English text of this app, never server text.
public sealed record MailCommand(long Id, Guid AccountId, MailCommandKind Kind, long FolderId, uint FolderUidValidity,
    IReadOnlyList<uint> Uids, long? TargetFolderId, MessageFlags FlagsToAdd, MessageFlags FlagsToRemove,
    MailCommandStatus Status, int Attempts, DateTimeOffset CreatedAt, DateTimeOffset? NextAttemptAt, string? LastError);

/// Queue for the UI and the classifier. Every Enqueue validates, stores one command and returns it; nothing is
/// changed locally or on the server at this point.
public interface IMailCommandQueue
{
    /// Adds/removes Seen, Flagged, Answered, Draft. Deleted is never allowed here (use the delete commands).
    Task<MailCommand> EnqueueSetFlagsAsync(long folderId, IReadOnlyCollection<uint> uids, MessageFlags flagsToAdd,
        MessageFlags flagsToRemove, CancellationToken ct = default);

    /// Move to another folder of the same account.
    Task<MailCommand> EnqueueMoveAsync(long folderId, IReadOnlyCollection<uint> uids, long targetFolderId,
        CancellationToken ct = default);

    /// Del key: move to the account's folder with role Trash (resolved now, stored as TargetFolderId).
    Task<MailCommand> EnqueueMoveToTrashAsync(long folderId, IReadOnlyCollection<uint> uids, CancellationToken ct = default);

    /// Shift+Del: delete permanently (from any folder).
    Task<MailCommand> EnqueueDeletePermanentlyAsync(long folderId, IReadOnlyCollection<uint> uids, CancellationToken ct = default);

    /// "Ordner leeren": only for role Trash or Junk. Takes the UIDs stored locally for the folder now and enqueues
    /// DeletePermanently for exactly these (messages that arrive later are never touched). Null when the folder has
    /// no local messages.
    Task<MailCommand?> EnqueueEmptyFolderAsync(long folderId, CancellationToken ct = default);

    /// Pending and failed commands of the account, oldest first.
    Task<IReadOnlyList<MailCommand>> ListAsync(Guid accountId, CancellationToken ct = default);

    /// Removes a command (pending or failed) without executing it. Unknown id = no-op.
    Task DiscardAsync(long commandId, CancellationToken ct = default);
}

/// Executor side of the queue.
public interface IMailCommandStore
{
    /// Pending commands of the account whose NextAttemptAt is null or ≤ now, oldest first.
    Task<IReadOnlyList<MailCommand>> ListDueAsync(Guid accountId, DateTimeOffset now, CancellationToken ct = default);
    /// Removes a completed command.
    Task CompleteAsync(long commandId, CancellationToken ct = default);
    /// Records a failed attempt: Attempts + 1, LastError, NextAttemptAt; permanent → Status Failed (never retried).
    Task RecordFailureAsync(long commandId, string error, DateTimeOffset? nextAttemptAt, bool permanent,
        CancellationToken ct = default);
}
```
Validation at enqueue (`ArgumentException`/`ArgumentOutOfRangeException`, nothing stored):
- the folder must exist;
- UIDs non-empty, no 0, at most 1,000; duplicates are removed, and the stored list is ascending;
- `FolderUidValidity` = the stored folder value, which must be ≠ 0 (a never-synced folder is rejected);
- SetFlags: add and remove are disjoint, not both None, only Seen | Flagged | Answered | Draft;
- Move: the target exists, belongs to the same account and is not the source;
- MoveToTrash: the account has a folder with role Trash (else `InvalidOperationException`), and the source is not
  that Trash folder (the UI asks and calls DeletePermanently instead);
- EmptyFolder: the source role is Trash or Junk.

### C2 – storage: schema v4 + `SqliteMailCommandQueue`
- Append migration v4, `CurrentSchemaVersion` = 4. Table `mail_commands`:
  - `id`, `account_id` (FK accounts ON DELETE CASCADE), `kind`;
  - `folder_id` (FK folders ON DELETE CASCADE), `folder_uid_validity`, `uids` (TEXT, ascending, comma-separated);
  - `target_folder_id` (NULL, FK folders ON DELETE CASCADE);
  - `flags_add`, `flags_remove`, `status`, `attempts`;
  - `created_at`, `next_attempt_at`, `last_error` (ISO "O" for dates).

  When a folder is removed by the sync, its commands disappear with it.
- `SqliteMailCommandQueue(SqliteDatabase, TimeProvider)` implements both interfaces (namespace
  `MailWarden.Infrastructure.Storage`).

### C3 – write transport (Core interface + MailKit)
```csharp
// MailWarden.Core.Sync
public interface IMailCommandTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    /// SELECT (read-write). Returns UIDVALIDITY of the folder.
    Task<uint> SelectFolderAsync(string fullName, CancellationToken ct = default);
    Task AddFlagsAsync(IReadOnlyList<uint> uids, MessageFlags flags, CancellationToken ct = default);
    Task RemoveFlagsAsync(IReadOnlyList<uint> uids, MessageFlags flags, CancellationToken ct = default);
    /// UID MOVE to the target folder; returns the new UIDs in the target per source UID (COPYUID), empty when the
    /// server sent none. UIDs that no longer exist are ignored by the server.
    Task<IReadOnlyDictionary<uint, uint>> MoveAsync(IReadOnlyList<uint> uids, string targetFullName, CancellationToken ct = default);
    /// STORE +FLAGS \Deleted for exactly these UIDs, then UID EXPUNGE of exactly these UIDs.
    Task ExpungeAsync(IReadOnlyList<uint> uids, CancellationToken ct = default);
}
// IMailTransportFactory – addition
Task<IMailCommandTransport> ConnectForCommandsAsync(Account account, CancellationToken ct = default);
```
**Safety rules (MailKit implementation, mandatory):**
- S1: Never send a plain `EXPUNGE` or a `CLOSE`. Both remove every `\Deleted` message of the folder, including ones
  this app did not mark. Leave a selected folder only by selecting another one, by `UNSELECT`, or by logout.
  `ExpungeAsync` requires UIDPLUS. Without it → `MailTransportException(Protocol)` before anything is sent.
- S2: `MoveAsync` requires the MOVE capability. Without it → Protocol. There is no COPY+EXPUNGE fallback in AP-03c.
- S3: Error mapping and own wording as in `MailKitTransport` (no server text, no token). Reuse the B6 `ImapSession`
  helper for connect and login.
- The builder checks how MailKit 4.18.2 implements `CloseAsync(false)`, `ExpungeAsync(uids)` and `MoveToAsync`, and
  documents it in the notes.

### C4 – executor (`MailWarden.Infrastructure.Commands`)
```csharp
public sealed record CommandOptions(int MaxAttempts = 5);   // 1..20, else ArgumentOutOfRangeException
public sealed record CommandRunReport(int Completed, int Retrying, int Failed);

public sealed class CommandExecutor
{
    public CommandExecutor(IMailTransportFactory transportFactory, IAccountStore accounts, IFolderStore folders,
        IMessageStore messages, IEmlStore emlStore, IMailCommandStore commands, CommandOptions options,
        TimeProvider timeProvider);

    /// Executes the due commands of the account in queue order with one command session.
    public Task<CommandRunReport> ExecutePendingAsync(Guid accountId, CancellationToken ct = default);
}
```
- **E1 Order:** the due commands (`ListDueAsync(now)`) run one after another, oldest first. With no due command, no
  connection is opened.
- **E2 Guard:** `SelectFolderAsync(source)` returns a UIDVALIDITY ≠ `FolderUidValidity` → permanent failure
  "UIDVALIDITY changed" (nothing is sent for this command). A missing source or target folder in the local store
  (deleted meanwhile) → permanent failure.
- **E3 Server action per kind:**
  - SetFlags → `AddFlagsAsync` / `RemoveFlagsAsync`.
  - Move, MoveToTrash → `MoveAsync(target)`.
  - DeletePermanently with source role Trash or Junk → `ExpungeAsync(uids)`.
  - DeletePermanently with any other source role → `MoveAsync(trash)`, then `SelectFolderAsync(trash)` and
    `ExpungeAsync(new UIDs from the COPYUID map)`. A UID without a mapping is not expunged and stays in Trash.
    The command then completes with the Error text "moved to trash, n not expunged" in the report; nothing is lost.
    No Trash folder → permanent failure.
- **E4 Local effect, only after the server succeeded:**
  - SetFlags → `UpdateFlagsAsync` with the stored flags plus or minus the change, only for locally stored UIDs.
  - Move, MoveToTrash, DeletePermanently → delete the EML files of the source UIDs (failures skipped, as in fix.2 F2),
    then `DeleteByUidsAsync(source, uids)`. Moved messages appear in the target with the next sync pass.

  Then `CompleteAsync`. A storage failure in the local effect still completes the command, because the server
  changed and the next sync repairs the local state. It is counted as Completed.
- **E5 Failures:**
  - `AuthenticationFailedException`, `MailTransportException(Authentication)` and `OperationCanceledException`
    → propagate. The current command stays unchanged and its attempt is not counted.
  - `MailTransportException(Network)` → `RecordFailureAsync(transient)`, then propagate. The run ends; the
    remaining commands stay due.
  - `MailTransportException(Protocol)` or any other exception → `RecordFailureAsync`, then the next command
    continues. Attempts after this failure ≥ `MaxAttempts` → permanent (Failed). Otherwise `NextAttemptAt` = now +
    2^(attempts−1) minutes, at most 60 minutes.
  - `LastError` = exception type name, or a fixed text of this app (E2/E3), never the exception message.
- **E6:** The read-only sync pass is unchanged and has no write path. The executor never touches folders or
  messages other than the command's source/target UIDs.

### C5 – CommandProbe (`_scratch\command-probe`, git-ignored; builder writes it, planner runs it)
`CommandProbe <dataRoot> <address> <action> [args]`, with silent tokens only (same credential setup as the SyncProbe).
It prints only counts, UIDs, sizes and roles, never subjects or addresses.

| Action | Effect |
|---|---|
| `junk` | List the Junk folder: local UIDs with size and flags |
| `seen <uid>` / `unseen <uid>` | SetFlags |
| `trash <uid>` | MoveToTrash |
| `delete <uid>` | DeletePermanently |
| `empty-junk` | EmptyFolder on Junk |
| `queue` | List pending and failed commands |
| `run` | `ExecutePendingAsync`, then one sync pass, then print the counts |

**Hard guard:** every enqueue action refuses (exit 1, nothing stored) unless the source folder's role is Junk. The
one exception is `delete <uid>` on a UID that is currently in Trash because of an earlier `trash` by this probe. The
probe remembers those UIDs in `<dataRoot>\command-probe-trash.json`.

## Expected behaviour (test cases, fakes + temporary SQLite/EML)
| # | Case | Expected |
|---|---|---|
| 1 | Enqueue SetFlags (add Seen) for 2 UIDs | 1 command Pending, Uids ascending, FolderUidValidity stored; message flags unchanged locally; no transport call |
| 2 | Enqueue validation: empty/0/1001 UIDs, unknown folder, folder UidValidity 0, add∩remove ≠ ∅, both None, Deleted flag, Move to same folder / other account / unknown target, MoveToTrash from Trash or without Trash, EmptyFolder on Inbox | exception per C1, nothing stored |
| 3 | EmptyFolder on Junk with 3 local messages; a 4th arrives locally afterwards | command has exactly the 3 UIDs; null for an empty folder |
| 4 | Schema v4: fresh DB and v3 DB with data | version 4, table exists, old rows intact; folder delete cascades its commands |
| 5 | ListAsync / DiscardAsync / ListDueAsync with NextAttemptAt in past/future | as stated |
| 6 | Execute SetFlags | Select → AddFlags (and/or RemoveFlags) with exactly the UIDs; local flags updated; command removed; report Completed 1 |
| 7 | Execute Move | MoveAsync(target name); source rows and EML files gone; command removed |
| 8 | Execute MoveToTrash | like 7 with the Trash folder |
| 9 | Execute DeletePermanently in Junk | ExpungeAsync(exactly the UIDs); MoveAsync never called; rows/EML gone |
| 10 | Execute DeletePermanently in Inbox (COPYUID map for both UIDs) | MoveAsync(trash) → Select(trash) → ExpungeAsync(mapped UIDs) |
| 11 | Same as 10, map empty | nothing expunged, command completed, local rows gone (messages are in Trash on the server) |
| 12 | UIDVALIDITY mismatch on Select | no Add/Move/Expunge call; command Failed (permanent), LastError says UIDVALIDITY |
| 13 | No due command | factory never called |
| 14 | Protocol failure on command 1 of 2 | command 1 Attempts 1, NextAttemptAt = now + 1 min, Pending; command 2 executed |
| 15 | Protocol failure, attempt 5 of MaxAttempts 5 | Failed, not listed by ListDueAsync |
| 16 | Backoff sequence | 1, 2, 4, 8 … minutes, capped at 60 (TimeProvider) |
| 17 | Network failure on command 1 of 3 | command 1 attempt recorded; exception propagates; commands 2–3 untouched; transport disposed |
| 18 | Authentication failure / cancellation | propagates; command unchanged (Attempts 0) |
| 19 | Storage failure in the local effect after server success | command completed; report Completed |
| 20 | Commands run oldest first, one session for all | factory called once; order of Select calls |
| 21 | LastError never contains the exception message (fake throws with a marker text) | marker absent |
| 22 | Sync pass unchanged | AP-03a case 20 (no write operation on `IMailTransport`) still green; `IMailCommandTransport` is a separate interface |
| 23 | Positive control | queue → executor → sync: a moved message disappears from the source and appears in the target after the sync pass (fake server moves it) |
| 24 | Real accounts (planner, CommandProbe, Junk only; see C5) | seen/unseen on a junk mail changes webmail state and back; trash → appears in Trash, gone from Junk; delete from Trash and from Junk → gone from the server (sync-check: counts match); empty-junk on one account → Junk empty, nothing else changed (sync-check of all other folders unchanged) |

## Done when
- Build `-warnaserror` green; S3 green (central building blocks: Core interfaces, schema).
- Review APPROVE. Security review without Kritisch/Hoch. It is mandatory: this is the first server write, with
  deleting actions.
- Documenter: README (command queue, delete semantics); `docs\` stays local. PLAN rows AP-03c.
- Case 24 green (planner).
