---
id: AP-07c
slug: classifier-pipeline
title: Classification pass – new mail only, decision log, app folders, moves via the queue, quarantine retention
test-scope: S3
test-filter: "Category=feature:classifier|Category=feature:sync|Category=feature:storage|Category=smoke"
base: chore/ap-04a-07c-requirements (builder and tester)
status: ready (2026-11-01)
---

# AP-07c – Classification pass

## Goal
After every sync pass, newly arrived mail in the Inbox and Junk folders is classified, the decision with all reasons is
stored, and **in mode `Act`** the mail is moved through the command queue:

| Source | TrustedContact | LikelyLegit | LikelySpam | CertainSpam |
|---|---|---|---|---|
| Inbox | stays | → Screener | → Junk | → Quarantine |
| Junk | → Inbox (rescue) | stays | stays | stays |

`Screener` and `Quarantine` are created on the server when missing. Quarantine retention deletes old mail only when
switched on.

## Safety decisions (planner, 2026-11-01; Dennis can overrule them at the gate)
These protect the real mailboxes and are binding:
1. **Default mode `RecordOnly`:** decisions are stored and nothing moves. `Act` needs an explicit setting.
2. **Existing mail is never classified automatically.** The first pass for a folder only sets its marker. A later
   check of the old stock belongs to AP-08.
3. **`Act` is suspended while the address book is empty** (own account addresses do not count). Otherwise every
   authenticated mail would land in the Screener. The address book is only filled by AP-06. The pass then behaves like
   `RecordOnly` and reports it.
4. **One decision per message:** a message whose key is in the log is never decided again. A mail Dennis moved back to
   the Inbox on another device stays there, and a Gmail label copy causes no second action.
5. **Retention default 0 = never delete.** The clock is the stored decision time, never the mail's `Date` header (which
   the sender controls). Only mail that this app moved to Quarantine can be deleted.

## Context
- Inputs and classifier: `SpamClassifier`, `SpamClassifier.CreateDefaultSignals`, `ClassificationInput`,
  `ClassificationResult`, `ISenderDirectory`, `ClassifierSettingsFile`.
- Example of the input extraction with real data, including link hosts: `_scratch\PstProbe\Program.cs`
  (`ReadBody`, `Patterns`); that one works on PST data, here MimeKit reads the EML. `MimeBodyText.Extract` exists.
- Queue: `IMailCommandQueue.EnqueueMoveAsync`, `EnqueueDeletePermanentlyAsync` (at most 1,000 UIDs; expected UIDVALIDITY
  is the local folder value). `FolderLookup`.
- `FolderRoleResolver` already maps top-level `Screener` and `Quarantine` to their roles. `ImapSession` is the shared
  connection code. `contacts` table exists since schema v1, without a store. `SqliteDatabase.CurrentSchemaVersion` = 4.
- Runs in parallel with AP-04a, which owns `MailWardenRuntime`, `IMessageStore.CountUnreadAsync` and the new
  `MailWarden.Presentation` project. **AP-07c does not touch them, nor `IMessageStore`, `MailboxService` or
  `SyncEngine`.** Wiring the pass into the runtime is D8, done after both are merged.

## Non-goals
- No UI, no "Warum?" view, no feedback ("kein Junk"), no configurable mapping, no direct-delete switch (AP-08).
- No harvesting of contacts and no vCard/CSV import (AP-06), no allow/block list storage (AP-06). Until then
  `GetListMembership` returns `None`; the class name and doc say so.
- No classification of folders other than Inbox and Junk.
- No new package.

## Contract (binding)

### D1 – model (`MailWarden.Core.Classification`)
```csharp
public enum ClassificationMode { RecordOnly = 1, Act = 2 }
public enum ClassificationAction { None = 0, MoveToInbox = 1, MoveToScreener = 2, MoveToJunk = 3, MoveToQuarantine = 4 }
public enum ClassificationOutcome
{
    NoAction = 1,      // decided; routing says stay
    Recorded = 2,      // an action was planned, mode RecordOnly (also while Act is suspended)
    Enqueued = 3,      // move command stored
    TargetMissing = 4, // account has no folder for the target role
    EnqueueFailed = 5, // the queue rejected the command
    ReadFailed = 6,    // EML missing or unreadable, or no usable From address; no tier
}
public sealed record ClassificationPolicy(ClassificationMode Mode, int QuarantineRetentionDays)
{
    public static ClassificationPolicy Default { get; }   // RecordOnly, 0
    /// Days < 0 or > 3650 → ArgumentException.
    public void Validate();
}
public static class ClassificationRouting
{
    /// The table in "Goal". Other source roles → ArgumentOutOfRangeException.
    public static ClassificationAction Route(FolderRole sourceRole, SpamTier tier);
}
public static class MessageKey
{
    /// "m:" + lower-case SHA-256 hex of the trimmed Message-ID; without Message-ID "h:" + lower-case SHA-256 hex of
    /// FromAddress (lower-cased) + LF + Date (UTC, ISO 8601 round trip) + LF + Subject. Size and folder are not part of
    /// it: Outlook.com changes the size on MOVE.
    public static string For(MailMessage message);
}
public sealed record ClassificationLogEntry(Guid AccountId, string MessageKey, long SourceFolderId, uint SourceUid,
    DateTimeOffset ClassifiedAt, ClassificationMode EffectiveMode, ClassificationOutcome Outcome,
    ClassificationAction PlannedAction, SpamTier? Tier, int Score, AuthStatus? AuthStatus, string? Override,
    IReadOnlyList<SignalResult> Signals);
```

### D2 – stores
```csharp
namespace MailWarden.Core.Classification;
public interface IClassificationLog
{
    Task<ClassificationLogEntry?> GetAsync(Guid accountId, string messageKey, CancellationToken ct = default);
    /// Marker of the folder, null when none.
    Task<ClassificationMarker?> GetMarkerAsync(long folderId, CancellationToken ct = default);
    /// One transaction: inserts the entries (an existing (AccountId, MessageKey) stays unchanged) and sets the marker.
    Task CommitAsync(IReadOnlyCollection<ClassificationLogEntry> entries, ClassificationMarker marker, CancellationToken ct = default);
}
public sealed record ClassificationMarker(long FolderId, uint UidValidity, uint ClassifiedUpToUid);

namespace MailWarden.Core.Storage;
public interface IContactStore
{
    Task<IReadOnlyList<Contact>> ListAsync(CancellationToken ct = default);
    /// Insert or update by address (case-insensitive). Returns the stored copy.
    Task<Contact> UpsertAsync(Contact contact, CancellationToken ct = default);
}
```
Schema v5 (`SqliteDatabase.CurrentSchemaVersion` = 5, migration appended):
- `classification_log`: primary key (`account_id`, `message_key`), `account_id` with `ON DELETE CASCADE`; the columns of
  the entry, signals as JSON.
- `classification_marker`: primary key `folder_id` with `ON DELETE CASCADE`.

A database from v1–v4 migrates without data loss. Implementations: `SqliteClassificationLog`, `SqliteContactStore`
(`Infrastructure\Storage`).

### D3 – policy file (`MailWarden.Infrastructure.Classification`)
```csharp
public static class ClassificationPolicyFile
{
    public const string FileName = "classification-policy.json";
    public static ClassificationPolicy Load(string path);
}
```
Keys `mode` (`"recordOnly"`, `"act"`), `quarantineRetentionDays` (integer). A missing file gives the default, a missing
key its default. Same strictness as `ClassifierSettingsFile`: unknown key, duplicate key, wrong type or invalid value →
`FormatException`-style error of that class's pattern, naming the file and the key, not the value.

### D4 – input from an EML (`MailWarden.Infrastructure.Classification`)
```csharp
public static class ClassificationInputReader
{
    /// Parses the EML; the stream is not disposed. Null when no single usable From address with '@' and a domain exists.
    public static Task<ClassificationInput?> ReadAsync(Stream eml, CancellationToken ct = default);
}
```
- `AuthenticationResultsHeaders`: raw values in header order, top-most first.
- `BodyText`: `MimeBodyText.Extract`, at most 200,000 characters.
- `LinkHosts`: lower-cased hosts of `http`/`https` links from `<a href>` of the HTML body or, without HTML, from URLs in
  the text; duplicates allowed; malformed URLs are skipped.

### D5 – sender directory (`MailWarden.Infrastructure.Classification`)
```csharp
/// In-memory snapshot of the contacts plus the addresses of all accounts. Allow/block lists come with AP-06, until then
/// GetListMembership returns None.
public sealed class ContactSenderDirectory : ISenderDirectory
{
    public ContactSenderDirectory(IEnumerable<string> contactAddresses, IEnumerable<string> ownAddresses);
    public int ContactCount { get; }   // contacts only, own addresses do not count
}
```
Addresses are compared case-insensitively and exactly.

### D6 – app folders
```csharp
namespace MailWarden.Core.Sync;
public interface IMailFolderProvisioner
{
    /// Connects, lists the folders, creates the top-level folders "Screener" and "Quarantine" when missing, reads their
    /// UIDVALIDITY and stores them locally (IFolderStore.UpsertAsync) with their roles. An existing server folder of that
    /// name is reused, never recreated. Failures: MailTransportException / AuthenticationFailedException.
    Task EnsureAppFoldersAsync(Account account, CancellationToken ct = default);
}
```
Implementation `MailKitFolderProvisioner` (`Infrastructure\Sync`) with `ImapSession`. It issues only CREATE and read-only
commands: no SELECT for writing, no EXPUNGE, no other change. The local UIDVALIDITY is stored so that the next command
cycle can use the folder before the next sync. It is a separate interface, so the fakes of `IMailCommandTransport` stay
unchanged.

### D7 – pass (`MailWarden.Infrastructure.Classification`)
```csharp
public sealed record ClassificationPassReport(int Classified, int SkippedKnown, int ReadFailed, int MovesEnqueued,
    int RetentionDeletesEnqueued, bool ActSuspendedNoContacts, IReadOnlyDictionary<SpamTier, int> TierCounts);

public sealed class ClassificationPass
{
    public ClassificationPass(IAccountStore accounts, IFolderStore folders, IMessageStore messages, IEmlStore emlStore,
        IContactStore contacts, IClassificationLog log, IMailCommandQueue queue, IMailFolderProvisioner provisioner,
        ClassifierSettings settings, ClassificationPolicy policy, TimeProvider timeProvider);
    public Task<ClassificationPassReport> RunAsync(Guid accountId, CancellationToken ct = default);
}
```
One pass, in this order:
1. Load the account. Build the snapshot from contacts and all account addresses, and create the classifier with
   `CreateDefaultSignals`. An unknown account → `ArgumentException`.
2. **Effective mode:** `Act` with `ContactCount == 0` → `RecordOnly`, `ActSuspendedNoContacts` true.
3. Effective `Act` and a Screener or Quarantine folder missing locally → `EnsureAppFoldersAsync`. Its failure propagates
   out of the pass and nothing has been stored; the mail stays where it is (PLAN AP-07 #4b).
4. For each folder of the account with role Inbox, then Junk, and stored UIDVALIDITY ≠ 0:
   - **No marker, or marker UIDVALIDITY differs:** commit an empty entry list with marker = highest local UID (0 when
     empty). Nothing is classified.
   - **Otherwise:** take the local UIDs above the marker, ascending. For each: known key → count `SkippedKnown`, no new
     entry. Otherwise read the EML into memory, build the input and classify. `EmlPath` null, a missing or unreadable
     file, or a null input → entry with outcome `ReadFailed`, tier null. An exception from `Classify` is handled the
     same way; the entry stores no exception text.
   - **Routing:** action `None` → `NoAction`. Otherwise in effective `RecordOnly` → `Recorded`. Otherwise resolve the
     target by role (`FolderLookup` or role search in the folder list); missing → `TargetMissing`. Otherwise collect
     for enqueueing.
   - **Enqueue:** per target one `EnqueueMoveAsync` with at most 1,000 UIDs per command, source UIDVALIDITY from the
     folder read in this pass. `ArgumentException` or `InvalidOperationException` from the queue → these entries get
     `EnqueueFailed`, and the other commands continue.
   - **Commit:** entries and marker = highest UID handled, in one `CommitAsync`, after enqueueing. A crash in between
     can only repeat a move of UIDs that are already gone, which the server ignores.
   - **Two copies in one pass:** the same key twice in one pass is decided once (the second counts `SkippedKnown`).
5. **Retention:** only in effective `Act` and days > 0. For local messages of the Quarantine folder whose log entry has
   outcome `Enqueued`, action `MoveToQuarantine` and `ClassifiedAt + days <= now`: `EnqueueDeletePermanentlyAsync`, at
   most 1,000 UIDs per command. A message without such an entry is never deleted.
6. **Cancellation** propagates as `OperationCanceledException`; a folder whose commit did not run is handled again by
   the next pass.

Nothing in this pass talks to the server except D6. No log entry, report or exception contains subject, body or
address text beyond the classifier's reason texts in the local database, and these never go to a log file.

### D8 – wiring (after AP-04a and AP-07c are both on `main`; planner gives this as a separate fix step)
In the account cycle of `MailWardenRuntime`: sync, then `ClassificationPass.RunAsync`; when moves or deletes were
enqueued → `MailboxService.TriggerAccount`. Default policy from `classification-policy.json`, so a new install stays
`RecordOnly`. Its own test cases are added then.

### D9 – probe (`_scratch\classification-probe`, builder writes, planner runs)
`ClassificationProbe <dataRoot> stock` classifies **all** local Inbox and Junk mail in memory, writes nothing, and uses a
simulated address book from local Sent-folder recipients. It prints only counts per role and tier and per signal.
`ClassificationProbe <dataRoot> pass <address>` runs one real `ClassificationPass` with the policy file of the root.
Never prints subjects, addresses, domains or folder names. Only ever used on a **copy** of `probe-data`.

## Expected behaviour (test cases; temp SQLite, fake provisioner, real or fake queue, artificial EML fixtures)
| # | Case | Expected |
|---|---|---|
| 1 | Route: all combinations for Inbox and Junk; another role | table in "Goal"; ArgumentOutOfRangeException |
| 2 | `MessageKey`: same Message-ID with different size/folder; with whitespace; without Message-ID | same key; trimmed; "h:" form stable; address case does not matter |
| 3 | Policy validation and file: missing, partial, each invalid kind, unknown key | per D1, D3; error names key and not value |
| 4 | Migration v4 → v5 and from v1 with data | tables exist, old data intact, version 5; a newer version is rejected as before |
| 5 | Log: insert, duplicate key keeps the first entry, marker round trip, signals round trip | per D2 |
| 6 | Account deleted; folder deleted | log and marker entries removed by cascade |
| 7 | Contact store: upsert twice with different case | one row, updated name |
| 8 | Input reader: From with name; no From; two From; no domain; Reply-To; three Auth-Results in order | values per D4; null where stated; order kept |
| 9 | Input reader: HTML links, tracking link with query, mailto ignored, malformed URL; text-only URLs | hosts lower-cased; no exception |
| 10 | Directory: contact in other case; own address; ContactCount | IsContact true for both; count only contacts; membership None |
| 11 | First pass with 50 existing Inbox mails | no entry, marker = highest UID, no command |
| 12 | Next pass with 3 new mails (mode RecordOnly) | 3 entries, outcomes NoAction or Recorded, marker raised, **no command**, provisioner not called |
| 13 | Each tier from Inbox in effective Act | Trusted NoAction; Likely → Screener; LikelySpam → Junk; Certain → Quarantine; commands with correct target and UIDVALIDITY; outcome Enqueued |
| 14 | Trusted contact in Junk, Act | moved to Inbox; other tiers in Junk NoAction |
| 15 | Act with empty address book | like RecordOnly, ActSuspendedNoContacts true, no command, no provisioning |
| 16 | Act with own account address as the only "contact" | still suspended |
| 17 | Act, Screener missing | provisioner called once before moves; a second pass with folders present does not call it |
| 18 | Provisioner throws MailTransportException | propagates; no entry, no marker change, no command; a next pass succeeds and handles the mails |
| 19 | Known key arriving again (moved back by hand) | SkippedKnown, no command, marker raised |
| 20 | Same Message-ID twice among new mails | one entry, one action |
| 21 | UIDVALIDITY of the folder changed since the marker | nothing classified, marker reset to the highest UID |
| 22 | EML missing, EML garbage, no From | ReadFailed, tier null, no action, marker raised, other mails handled, no exception text in the entry |
| 23 | No Junk folder on the account, LikelySpam | TargetMissing, mail stays, others continue |
| 24 | Queue rejects a command | EnqueueFailed for its mails, other targets still enqueued |
| 25 | 2,500 new mails to the same target | at most 1,000 UIDs per command, all covered |
| 26 | Retention 0 with old quarantine mail | no delete |
| 27 | Retention 30: entry 31 days old; entry 29 days old (fake clock); old `Date` header but recent decision; mail without entry | only the first deleted; never the others |
| 28 | Retention in RecordOnly and in suspended Act | no delete |
| 29 | Cancellation between two folders | OperationCanceledException; the committed folder is not repeated, the other is handled next pass |
| 30 | Report counts | match the cases |
| 31 | Provisioner (fake IMAP not available) | no unit test; checked with case 33 |
| 32 | Real data, stock (planner, copy, probe `stock`) | counts plausible against PstProbe findings; no crash; no names in the output |
| 33 | Real server (planner, copy, **only after Dennis's approval and only with test mails from Dennis**) | RecordOnly writes entries and moves nothing; Act with a test contact moves test mails as in the table; Screener and Quarantine created; other devices see the same state |

## Done when
- Build `-warnaserror` green; S3 green (interfaces in Core, schema).
- Review APPROVE and security review without Critical or High (trigger: classifier actions that delete).
- Documenter: `docs\classifier.md` (pass, modes, safety rules, retention), `README.md`, PLAN rows AP-07 #1, #3, #4, #4b, #7, #8.
- Cases 32 and 33 done; D8 only after both APs are on `main`.

## Decisions author.0 (planner, 2026-11-01, questions of the tester)
- Policy file errors: `InvalidDataException`, as in `ClassifierSettingsFile`. Values of `mode` are exact and
  case-sensitive; comments in the file are not allowed.
- Report: `Classified` counts entries with a tier (without `ReadFailed` and `SkippedKnown`). `MovesEnqueued` and
  `RetentionDeletesEnqueued` count **mails (UIDs)**, not commands.
- `MessageKey`: an empty or whitespace-only Message-ID counts as absent (`h:` form). Hash input is UTF-8; the date is
  `date.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)`.
- Log entries have no foreign key to the folder: they stay when a folder is deleted (a decision must survive moves and
  folder changes). Only the marker goes with the folder; the log goes with the account.
- `AuthenticationFailedException` from the provisioner propagates like `MailTransportException` (case 18).
- Input reader: with an HTML body only `<a href>` counts for `LinkHosts`, not `img` and not URLs in the text part.
  `BodyText` limit is exactly 200,000 characters. A From without a domain (`sender_a@`) gives null.
- Constructors: `new SqliteClassificationLog(SqliteDatabase)`, `new SqliteContactStore(SqliteDatabase)`. Namespace of
  `ClassificationPass`, `ClassificationInputReader`: `MailWarden.Infrastructure.Classification`.

## Decisions fix.2 (planner, 2026-11-01, after verify.1 and review.1 BLOCK, secure.1 High)
verify.1: S3 1182/1183 (one failure, see G5). The findings below are checked by the planner against the code where
stated. Binding; they change D1, D4, D6, D7, D9.

### Fixes
- **G1 (review B1, confirmed in `SyncEngine`: UIDVALIDITY is stored before the first fetch, `FolderSyncState` only after
  the folder completed).** `ClassificationPass` gets `IFolderSyncStateStore` as a constructor parameter, directly after
  `IFolderStore`. A folder without a stored sync state is skipped completely in step 4: no marker, no entry. This covers
  an interrupted first sync and a UIDVALIDITY reset (the state is deleted there). The marker is set by the first pass
  that sees a completed sync.
- **G2 (secure H1, also N1, changes safety decision 4 in form, not in purpose).** A sender controls `Message-ID`, so the
  key must also depend on something the sender does not control:
  ```csharp
  public static class MessageKey
  {
      /// As before, but topReceived (the top-most Received header, whitespace normalised to single spaces and trimmed;
      /// null or empty when none) is part of the hash input: "m:" + SHA-256(Message-ID + LF + topReceived), and
      /// "h:" + SHA-256(from + LF + date + LF + subject + LF + topReceived).
      public static string For(MailMessage message, string? topReceived);
  }
  ```
  The top-most `Received` is added by the receiving provider and differs per delivery; it stays the same when the mail
  is moved or copied. So a mail moved back by hand keeps its key and is not decided again, while a new mail with a reused
  Message-ID gets a new key and is classified. Whether it really is stable across a MOVE is checked in cases 32 and 33.
- **G3 (reader, for G2, G5, G7).** New in `ClassificationInputReader`, with `ReadAsync` as a thin wrapper returning
  `Input`:
  ```csharp
  public sealed record ClassificationMessage(bool Parsed, string? TopReceived, ClassificationInput? Input);
  /// Never throws for unparsable bytes (Parsed false). IOException of the stream and cancellation propagate.
  public static Task<ClassificationMessage> ReadMessageAsync(Stream eml, CancellationToken ct = default);
  ```
  The pass computes the key from this result. Without a readable EML, `topReceived` is null.
- **G4 (secure H2).** `AuthenticationResultsHeaders` and `TopReceived` come from the raw header value
  (`Encoding.Latin1.GetString(header.RawValue)`), never the decoded `Header.Value`: encoded words must not be able to
  create tokens. This restores D4 "raw values".
- **G5 (verify.1, the one failing test).** `ReadAsync` returns null for bytes that are no message; `Garbage_bytes_give_null`
  stays as it is.
- **G6 (review S1, secure N3).** Retention skips UIDs that are already in a command of the queue (`IMailCommandQueue.ListAsync`,
  pending and failed). `RetentionDeletesEnqueued` counts only newly enqueued mails, so no repeat and no trigger loop.
- **G7 (secure M1).** A readable message without one usable From (`Parsed` true, `Input` null) is caused by the sender and
  must not stay unfiltered: from the Inbox its planned action is `MoveToScreener`, from Junk `None`. Tier null, score 0,
  `Override` fixed text "Message has no single usable sender". Outcomes as usual (`Recorded`, `Enqueued`, `TargetMissing`).
  `ReadFailed` is only for a missing or unreadable EML and for `Parsed` false; it stays in place and is final (documented).
- **G8 (review S2).** Retention no longer swallows a rejection silently: `ClassificationPassReport` gets a last member
  `int RetentionDeletesRejected` (mails).
- **G9 (review NIT).** `ClassificationRouting`: explicit arm for `CertainSpam`, and an unknown tier throws
  `ArgumentOutOfRangeException` instead of defaulting to the sharpest action.
- **G10 (review NIT).** Correct the comments on a crash between enqueue and commit: the command can run in the next cycle,
  the mail is then moved without a log entry. It is never deleted by retention for that reason (decision 5) and is decided
  again if it is in the Inbox or Junk.
- **G11 (secure N2).** `LinkHosts` holds at most 10,000 entries; more are dropped.
- **G12 (review question 7).** `MailKitFolderProvisioner` subscribes to a folder it just created, so other clients show it
  (`SubscribeAsync`; a failure of that is ignored). An existing folder is not touched. D6 is otherwise unchanged.
- **G13 (accepted deviations of implement.1, now contract).** The provisioner does not overwrite a stored UIDVALIDITY ≠ 0;
  a server folder that does not resolve to the expected role → `MailTransportException` (Protocol) and the pass stops
  with nothing stored; `TierCounts` holds all four tiers; an own address in the contacts table does not count for
  `ContactCount`.
- **G14 (probe, for cases 32/33).**
  - `stock` also prints: mails without a `Received` header; among mails with the same Message-ID in several folders, how
    many share the top-most `Received`; mails without a provider-stamped top `Authentication-Results`, per provider.
  - New mode `execute <address>` runs the pending commands of one account once and refuses when a command has more than
    20 UIDs. It is only used on a copy, with Dennis's test mails.

### Not fixed, on purpose
- Later UIDs below the marker (e.g. a message that failed in the sync) are never classified: safe, documented.
- Retention goes through Trash for Quarantine; an account without Trash fails permanently and visibly: documented.
- `SqliteMailCommandQueue.MaxUidsPerCommand`, `FindFolder`, `href` with tabs (review NITs): stay for now.
- Secure N2 (stream instead of `MemoryStream`), N4 (log retention): `TODO.md` for AP-08.

### Additional test cases (fix.2)
| # | Case | Expected |
|---|---|---|
| 34 | Folder with UIDVALIDITY set and mails, no sync state; then state present; then a new mail | first pass nothing at all (no marker, no entry, no command); second pass marker only; third classifies only the new mail |
| 35 | UIDVALIDITY reset: state deleted, part of the mails reloaded, then state present and all reloaded | no mail classified, marker = highest UID |
| 36 | `MessageKey`: same Message-ID and Received; same Message-ID, other Received; no Received; Received with different whitespace | same key; different; stable; same |
| 37 | Campaign: 3 mails with the same Message-ID and different `Received`, Act | each classified on its own, no `SkippedKnown` |
| 38 | Mail moved back by hand: same Message-ID and same `Received`, new UID | `SkippedKnown`, no command |
| 39 | Retention: mail in Quarantine with the same Message-ID as a decided mail, other `Received` | never deleted |
| 40 | Encoded word in `smtp.mailfrom` of the top header that decodes to `;dmarc=pass header.from=<contact domain>`, followed by a real `dmarc=fail`; sender is a contact | header value is raw, status not `Authenticated`, tier not `TrustedContact` |
| 41 | `ReadMessageAsync`: normal, garbage, no From, stream throws IOException | Parsed/TopReceived/Input as G3; IOException propagates |
| 42 | Two From addresses or empty group, Inbox, Act / RecordOnly; same in Junk | `Enqueued` to Screener / `Recorded`; `NoAction`; tier null; report `Classified` does not count it |
| 43 | Retention: same mail due in two passes, first command still pending; first command failed | one command only; `RetentionDeletesEnqueued` 1 then 0 |
| 44 | Retention: queue rejects the delete | `RetentionDeletesRejected` counts it, `RetentionDeletesEnqueued` does not, no exception |
| 45 | `Route` with an undefined tier value | ArgumentOutOfRangeException |
| 46 | More than 10,000 links | at most 10,000 hosts, no exception |

## Decisions fix.3 (planner, 2026-11-01, after verify.2 PASS 1229/1229, review.2 APPROVE, secure.2 0/0/3/3)
Binding; they change G3, G7, F1 of fix.2 and G14. **`Act` stays off on real accounts until case 33 shows that the top
`Received` survives a MOVE, including one mail moved back by hand (retention 0).** If it does not survive, the key is
reconsidered before `Act` is used: a changing key means a rescued mail is decided again.

### Fixes
- **H1 (secure M1-neu, changes G7).** A readable message without one usable From goes from the Inbox to **Junk**, not the
  Screener: a sender must not be able to choose the better target by writing two From addresses.
  `RouteWithoutSender(Inbox)` → `MoveToJunk`; from Junk `None`. Retention never touches it (action is not
  `MoveToQuarantine`). Cases 42 and the G7 tests change accordingly.
- **H2 (secure N3, review N1, changes F1).** `TopReceived` is computed before the input is built. An exception while
  building the input or from `Classify`, on a message that parsed, routes like H1 (tier null, fixed `Override`).
  `ReadFailed` is only for a missing or unreadable EML and `Parsed` false.
- **H3 (review S1, secure N2).** Retention reads headers only:
  `ClassificationInputReader.ReadTopReceivedAsync(Stream, CancellationToken)` returns the same raw value as `TopReceived`,
  via one shared helper. It does not build the input. Key and result as before; an unreadable file means never delete.
- **H4 (review N2).** `MailKitFolderProvisioner`: `SUBSCRIBE` only after the role check of the folder it just created.
- **H5 (secure, unsure part).** `AuthenticationResultsTokenizer` treats only ASCII whitespace and ASCII control characters
  as separators, so bytes such as 0xA0 or 0x80–0x9F stay in the value.
- **H6 (probe, secure M3, review S2; not committed, in `_scratch`, replaces the 20-UID rule of G14).**
  - `pass` and `execute` refuse to run unless the data root contains a marker file `probe-copy.marker`.
  - `execute <address> <expectedUidCount> <subjectMarker>`: refuses unless the sum of UIDs of all pending commands of the
    account equals `expectedUidCount`, all commands are `Move`, and the subject of every affected mail contains
    `subjectMarker` (case-sensitive). Nothing is executed when one check fails. Output numbers only.
  - `DeletePermanently` is refused without exception.
- **H7 (probe, secure M2, N1, review N4).** `stock` also prints per account:
  - how many mails with **different** Message-IDs share the same top `Received`;
  - how many mails have no `Received`;
  - how many mails have a top `Authentication-Results` that stands below all `Received` headers (hints at a header by the
    sender);
  - how many mails have no usable From.
- **H8 (documentation, documenter).** `docs\classifier.md`: a folder whose first sync never completes is never classified;
  a failed subscribe is not repeated; retention 0 holds for this app only, the provider can clean Junk; failed commands
  block retention for their UIDs until discarded (AP-04b).

### Decided after the measurement (planner, case 32)
- Different Message-IDs sharing a top `Received` (H7) > 0: add a hash of the body text to the key. Without Received > 0:
  such mails are never `SkippedKnown` and never deleted by retention. Both only if the measurement shows it.

### Additional test cases (fix.3)
| # | Case | Expected |
|---|---|---|
| 47 | Two From addresses or empty group from the Inbox, Act / RecordOnly; from Junk | `Enqueued` to Junk / `Recorded` with `MoveToJunk`; `NoAction`. Also replaces the Screener expectation in case 42 |
| 48 | `RouteWithoutSender` for Inbox, Junk, another role | `MoveToJunk`, `None`, ArgumentOutOfRangeException |
| 49 | `ReadTopReceivedAsync`: same value as `ReadMessageAsync().TopReceived` on normal mail and folded header; no Received; garbage | equal; null; null, no exception |
| 50 | Retention with a quarantine mail whose body part is corrupt but whose headers are readable | key still matches, deleted when due |
| 51 | Tokenizer: value containing bytes 0xA0 and 0x85 from the Latin-1 raw value, followed by a real `dmarc=fail` | status stays failed, nothing splits at these bytes; ASCII space and tab still separate (positive control) |

## Result case 32 (planner, 2026-11-01, copy of probe-data, read-only, counts only)
- Stock: Inbox 28 (all `LikelyLegit`, 24 authenticated), Junk 7 (2 `CertainSpam`, 3 `LikelySpam`, 2 `LikelyLegit`).
  Simulated address book: 418 addresses. No crash.
- Inbox and Junk: 0 mails without `Received`, 0 without a usable From, 0 different Message-IDs sharing a top `Received`.
- All 2,664 stored mails: 0 top `Received` values shared by different Message-IDs. On Outlook.com the top `Received` mostly
  has no id token and whole seconds only, so a salvo with a fixed Message-ID, delivered in the same second over the same
  servers, could share a key. **Decision: no body hash for now (measured 0), remaining risk documented in
  `docs\classifier.md`.** Mails without `Received` get no special rule either (none in Inbox or Junk).

## Decisions fix.4 (planner, 2026-11-01, after verify.3 PASS 1259/1259, review.3 APPROVE, secure.3 0/0/0/2)
Binding. This **withdraws the exception part of H2**, which was the planner's own decision: both reviews show that it
turns a programming error into a silent mass move to Junk, including mail from contacts.
- **J1.** An exception while building the input or from `Classify`, on a message that parsed (cancellation and
  out-of-memory excepted): new outcome `ClassificationOutcome.EvaluationFailed = 7`. Tier null, action `None`, no command,
  the mail stays where it is, the marker advances, no exception text. `TopReceived` stays part of the key. A readable mail
  without one usable From still goes to Junk as in H1.
- **J2.** `ClassificationPassReport` gets a last member `int EvaluationFailed` (mails). It is not part of `Classified` or
  `ReadFailed`. D8 and the probe show it.
- **J3 (testability).** `ClassificationPass` gets a last optional constructor parameter
  `IReadOnlyList<ISpamSignal>? signals = null`; null means `CreateDefaultSignals`.
- **J4 (for D8, not now).** D8 adds a brake: when one pass would enqueue more moves than a limit, it records and warns
  instead of moving. The limit is decided with D8.
- **J5 (probe `stock`).** Counts `EvaluationFailed` next to the actions, so case 32 does not understate it.
- **Not changed:** `Trim` in `AuthenticationResultsParser` (F4, review N1): no attack, `TODO.md`.

### Additional test cases (fix.4)
| # | Case | Expected |
|---|---|---|
| 52 | Throwing signal with a marker text (via J3), Inbox and Junk, Act | outcome `EvaluationFailed`, tier null, no command, mail stays, marker raised, `EvaluationFailed` = count, `Classified` and `ReadFailed` 0, marker text nowhere |
| 53 | Same mail again after a fixed signal (key known) | `SkippedKnown`, stays |
| 54 | Positive control: same setup without the throwing signal | normal tier and move |

- **J6 (secure.1 of AP-04a: about 1,000 nested MIME parts overflow the stack).** `ClassificationInputReader` parses with
  `MimeParserDefaults.Create()` (`Infrastructure\Mime`, already on this branch), in `ReadMessageAsync` and
  `ReadTopReceivedAsync`. AP-04a does the same for `SyncEngine` and `MailContentReader`.

| # | Case | Expected |
|---|---|---|
| 55 | Generated EML with 1,023 nested `multipart/mixed`, and with nested `message/rfc822`: `ReadMessageAsync`, `ReadTopReceivedAsync` | no process end; `Parsed` true, `TopReceived` and From as in the top-level headers |

## Result case 33 (planner, 2026-11-01; Dennis chose the accounts: Konto 2 and the Google account)
Mails between these two accounts only, sent with `_scratch\case33-probe` over the real SMTP servers (XOAUTH2); the app itself
cannot send yet (AP-05). Each receiver ran on its own copy that knows only this account, so the other account counts as an unknown
sender. Retention 0, deleting impossible in these probes.

| Step | Google as receiver | Outlook.com as receiver |
|---|---|---|
| SMTP login and send (from the other account) | ok, `2.0.0` | ok, `2.0.0` |
| Arrival | Inbox | **Junk** (the provider's own filter) |
| `RecordOnly` | entry `Recorded`, no command | entry `NoAction` (Junk + `LikelyLegit` stays), no command |
| `Act`, app folders | `Screener` and `Quarantine` created | `Screener` and `Quarantine` created |
| `Act`, move | 2 mails Inbox → Screener (`LikelyLegit`) | 2 mails Junk → Inbox (`TrustedContact`, sender as a contact in the copy: rescue, PLAN AP-07 #1) |
| Key after the move | same key | same key |
| Next pass | back in the Inbox by a server MOVE: `SkippedKnown`, no command | in the Inbox with a new UID: 2 × `SkippedKnown`, no command |

**Result: the top `Received` survives a MOVE on both providers.** Not tested: a move to Screener or Quarantine on Outlook.com; a
move to Junk or Quarantine on Gmail; moving back in the webmail itself (the probe uses a server MOVE, the same IMAP operation);
Dennis confirmed on 2026-11-01 that mails and folders appear in the web and in the Outlook app. `Act` on real accounts still needs AP-06 (empty address book) and D8.
