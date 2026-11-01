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
