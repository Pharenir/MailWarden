---
id: AP-03b
slug: sync-robust-fast
title: Sync robust and fast – batched fetch, progress, reconnect within a pass, shared IMAP connect, Gmail Important
test-scope: S3
test-filter: "Category=feature:sync|Category=feature:accounts|Category=feature:storage|Category=smoke"
base: chore/session-handover-4 (builder and tester)
status: ready (2026-10-31)
---

# AP-03b – Sync robust and fast

## Goal
Make the one-way sync from AP-03a usable for real mailboxes. It has to get faster, report its progress, survive a lost
connection within a pass, and stop syncing Gmail's "Important" folder. Everything stays read-only towards the server.
The command queue (local → server), IDLE and the schedule follow in **AP-03c** (plan split 2026-10-31).

## Context (findings case 28, real accounts, 2026-10-31)
- Outlook.com syncs about **4 messages/s** (845 Sent messages ≈ 197 s, 2,233 messages in total). The transport needs
  two round trips per message: one FETCH for metadata, one BODY.PEEK[].
- The SyncProbe prints nothing until the pass ends, so Dennis thought it hung and cancelled it twice.
- Gmail `[Gmail]/Important` came without the `\Important` attribute (role None, visible). It is synced like a normal
  folder and holds duplicates of other folders.
- Deferred from AP-03a review/secure (`TODO.md`):
  - a message that always raises `ImapProtocolException` blocks the account (MailKit disconnects, so every later
    command fails as Network);
  - duplicate IMAP connect/folder-list code in `MailKitTransportFactory`/`MailKitTransport` vs. `ConnectionProbe`;
  - review-fix3 NIT: UID 0 inside the initial window silently sets the floor to 0.
- Code: `src\MailWarden.Infrastructure\Sync\` (`SyncEngine`, `MailKitTransport`, `MailKitTransportFactory`,
  `SyncOptions`), `src\MailWarden.Core\Sync\IMailTransport.cs`, `src\MailWarden.Infrastructure\Providers\ConnectionProbe.cs`,
  `src\MailWarden.Core\Accounts\FolderRoleResolver.cs`. Contract history: `work\AP-03a\requirement.md`. Its R1–R11 and
  fix.2/fix.3 decisions stay valid unless changed below.

## Non-goals
- No write operation on the server, no command queue, no IDLE, no timer or schedule (AP-03c).
- No start-up cleanup of orphan EML/`*.tmp`, no `IEmlStore.DeleteFolderAsync` (AP-04 start routine, TODO).
- No change to the folder-list rules, except Gmail Important (B5).
- No new packages (MailKit 4.18.2 is present).

## Contract (binding)

### B1 – batched message fetch (Core interface + MailKit)
```csharp
// MailWarden.Core.Sync.IMailTransport – additions
/// True while the session is connected and authenticated; false after a lost connection or after dispose.
bool IsConnected { get; }

/// Fetches several messages of the open folder (metadata + BODY.PEEK[], never sets \Seen) with a bounded number of
/// IMAP commands that does not grow with the number of UIDs (target: one FETCH for the metadata of all UIDs plus one
/// for all bodies). Calls onMessage once per delivered message, in ascending UID order, and awaits it before the next
/// call. The callback owns RemoteMessage.Content and disposes it. UIDs the server returns nothing for are skipped
/// (no callback). Transport failures → MailTransportException as for the other methods; an exception thrown by
/// onMessage propagates unchanged and ends the call.
Task FetchMessagesAsync(IReadOnlyList<uint> uids, Func<RemoteMessage, CancellationToken, Task> onMessage,
    CancellationToken ct = default);
```
- Implementation hint (`MailKitTransport`): one `FetchAsync(uids, UniqueId|Flags|InternalDate|Size)` plus
  `ImapFolder.GetStreamsAsync(uids, ImapFetchStreamAsyncCallback)` – the builder verifies this against MailKit 4.18.2.
  The INTERNALDATE rule of `FetchMessageAsync` stays: a delivered message without INTERNALDATE → Protocol.
  `FetchMessageAsync` (single) stays and is used for the fallback.
- `SyncOptions` gets `int FetchBatchSize = 50` (init property, validated 1..500, else ArgumentOutOfRangeException;
  same pattern as `InitialSyncDays`). The positional constructor `SyncOptions(int InitialSyncDays = 0)` stays
  source-compatible.

### B2 – engine uses batches (contract change R5)
- Candidates (fix.3 G1, unchanged) are processed in ascending batches of `FetchBatchSize`. Per delivered message
  everything stays as before: EML first, parse the stored bytes, row + FTS. A failure in the callback for one
  message is a per-message failure (F1 counting and Error text unchanged). The batch then continues with the next
  message; it is not aborted.
- A batch whose `FetchMessagesAsync` throws `MailTransportException(Protocol)` → **fallback**: every UID of that
  batch that was not delivered yet is fetched singly with `FetchMessageAsync`. Each single fetch uses the
  per-message failure rule. Other exceptions follow B4.
- Per-message failures are retried on the next pass (fix.3, unchanged).

### B3 – progress
```csharp
namespace MailWarden.Infrastructure.Sync;

/// Progress of a running pass. FolderNumber is 1-based in sync order; Processed counts candidates handled so far
/// (stored, skipped or failed); Candidates is the number of missing UIDs found for the folder (0 until known).
public sealed record SyncProgress(Guid AccountId, long FolderId, FolderRole Role, int FolderNumber, int FolderCount,
    int Processed, int Candidates);

// SyncEngine – new overload; the existing SyncAccountAsync(accountId, ct) calls it with progress null.
public Task<SyncReport> SyncAccountAsync(Guid accountId, IProgress<SyncProgress>? progress, CancellationToken ct = default);
```
- Reported synchronously via `IProgress<T>.Report`:
  - once when a folder starts (Processed 0, Candidates 0);
  - once when its candidates are known;
  - after every batch;
  - once when the folder ends (also on error).
- Never contains folder names, subjects or addresses. `FolderId` lets the UI look the name up.
- SyncProbe (`_scratch\sync-probe`, planner): prints one line per progress report, at most one per second plus
  every folder start/end, without names.

### B4 – reconnect within a pass (contract change R9)
- Abort the whole pass (unchanged): `AuthenticationFailedException`, `MailTransportException(Authentication)`,
  `OperationCanceledException`; also any failure while connecting or reading the folder list.
- **New:** inside a folder, `MailTransportException(Network)` no longer aborts the pass. The folder ends with
  Error "<ExceptionType>: <message>" and keeps its previous state, like any other folder failure.
- After **every** folder failure the engine checks `transport.IsConnected`. If it is false:
  1. dispose the transport;
  2. connect a new one via the factory;
  3. continue with the next folder.

  If the factory throws, the pass aborts with that exception.
- `SyncOptions` gets `int MaxReconnectsPerPass = 3` (init, validated 0..10). One more lost connection than allowed →
  the pass aborts with a `MailTransportException(Network)` whose message says the connection was lost too often.
  Folders already finished keep their results.
- Consequence: a message that always raises `ImapProtocolException` costs its folder (the remaining candidates
  after it), not the account. Accepted for now; documented in the XML doc.
- The transport that is current when the pass ends or aborts is disposed (unchanged guarantee).

### B5 – Gmail "Important" hidden (Core, `FolderRoleResolver`)
- New rule for `MailProvider.Google`, after rule 4: full name `[Gmail]/Important` or `[Google Mail]/Important`
  (ordinal, case-insensitive) without one of the role flags → (None, hidden). Update the XML doc list of rules.
- Ich bin mir unsicher whether Gmail localizes this name for accounts in other languages; the real account uses
  English names. Localized names are out of scope.

### B6 – shared IMAP session code (review SHOULD 2 of AP-03a)
- One internal helper in `MailWarden.Infrastructure` (name chosen by the builder, e.g. `ImapSession`). It covers:
  - connect (endpoint, `ToSocketOptions`);
  - XOAUTH2 login;
  - listing the personal-namespace folders (without the empty namespace root).
- `MailKitTransportFactory`/`MailKitTransport` and `ConnectionProbe` use it. The folder-list result of
  `ConnectionProbe` must stay the same as today, including the namespace-root handling: the AP-02a tests
  stay green unchanged.

### B7 – UID 0 in the initial window (review-fix3 NIT)
- A UID 0 returned by the window search → the folder fails with `MailTransportException(Protocol)` (same as F3 for
  UIDVALIDITY 0). Nothing is stored for it, and no state is saved.

## Expected behaviour (test cases, fake transport + temporary SQLite/EML)
| # | Case | Expected |
|---|---|---|
| 1 | 120 missing UIDs, FetchBatchSize 50 | `FetchMessagesAsync` called 3× with 50/50/20 ascending UIDs; `FetchMessageAsync` never; all 120 stored |
| 2 | FetchBatchSize 0 / 501; MaxReconnectsPerPass −1 / 11 | ArgumentOutOfRangeException; defaults 50 and 3 |
| 3 | Batch: server returns nothing for 2 of 10 UIDs | 8 stored, no error; the 2 are retried next pass (fix.3) |
| 4 | Batch: EML write fails for 1 of 10 (store throws) | 9 stored; Error "1 message(s) failed (first: StorageException)"; next pass fetches only that UID |
| 5 | Batch throws Protocol; singles succeed | fallback: every UID of the batch fetched singly, all stored, no error |
| 6 | Batch throws Protocol; one single throws Protocol | others stored; Error "1 message(s) failed (first: MailTransportException)" |
| 7 | Progress: 2 folders, 60 candidates in folder 1 (batch 50) | reports in order: F1 start (0/0), F1 candidates known (0/60), after batch (50/60), after batch (60/60), F1 end, F2 start …; FolderNumber 1..2, FolderCount 2; no report contains a folder name (record has none) |
| 8 | Progress null | pass works as before (no exception) |
| 9 | Network failure while syncing folder 2 of 3, transport reports IsConnected false | folder 2 Error starts with "MailTransportException", state unchanged; factory called a 2nd time; folder 3 synced on the new transport; old transport disposed |
| 10 | Network failure in every folder, MaxReconnectsPerPass 2 | after the 3rd lost connection the pass throws MailTransportException(Network); results of earlier folders are stored; all transports disposed |
| 11 | Protocol failure in a folder, transport still connected | no reconnect (factory called once), next folder continues |
| 12 | Authentication failure inside a folder | pass aborts (unchanged) |
| 13 | Reconnect: factory throws on the 2nd connect | pass aborts with that exception |
| 14 | Gmail `[Gmail]/Important` without flags; `[Google Mail]/Important`; Microsoft folder named `[Gmail]/Important` | Google: (None, hidden) both; Microsoft: (None, visible) |
| 15 | `ConnectionProbe` against the fake/folder list | AP-02a tests unchanged and green (positive control for B6) |
| 16 | Window search returns UID 0 (InitialSyncDays 30) | folder Error starts with "MailTransportException"; no rows, no state |
| 17 | Positive control: AP-03a/fix.3 suite | all existing sync tests green; only tests whose fake needs the new members are adjusted (category c) |
| 18 | Real accounts (planner, SyncProbe + `_scratch\sync-check` on a copy of `probe-data` with the Sent folder of the big account reset) | resync at least 3× faster than 4 messages/s; progress lines appear during the run; 0 mismatched folders; unread unchanged |

## Done when
- Build `-warnaserror` green; S3 green (central building blocks: Core interfaces, SyncEngine).
- Review APPROVE. Security review: no trigger. The pass stays read-only, no new data sinks, and paths are unchanged.
  The planner records this decision.
- Documenter: README (sync section: batches, progress, reconnect, Gmail Important), PLAN rows AP-03b filled.
- Case 18 green (planner).
