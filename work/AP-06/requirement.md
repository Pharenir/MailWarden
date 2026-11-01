---
id: AP-06
slug: address-book
title: Address book - contacts from sent mail, allow and block lists, vCard and CSV import (no UI)
test-scope: S3
test-filter: "Category=feature:contacts|Category=feature:classifier|Category=feature:storage|Category=smoke"
base: chore/ap-06-04b-requirements (builder and tester)
status: ready (2026-11-01)
---

# AP-06 - address book

## Goal
The address book fills itself, so that the filter can really decide: `TrustedContact` needs contacts, and `Act` rests while the book is empty.
Recipients of sent mail become contacts, allow and block lists exist, and vCard and CSV files can be imported. Runs in parallel with AP-04b.

## Context
- `Contact`, `ContactSource`, `IContactStore`, `SqliteContactStore`, table `contacts` (unique address, case-insensitive).
- `ContactSenderDirectory` (`Infrastructure\Classification`): `GetListMembership` returns `None` until this AP. `ISenderDirectory` doc: address exact and
  case-insensitive; domain lists match the sender's organisational domain. `DomainNames` in Core.
- `ClassificationPass` builds the directory at the start of a pass. `ClassifyingSyncCycle` is the account cycle (`Infrastructure\Composition`).
- Parsing: always `MimeParserDefaults.Create()`. Real data has 992 sent mails with about 679 MB, so read **headers only** (`HeaderList.LoadAsync`).
- Security rule of the project: no account names or addresses of Dennis in the repo, not in notes or messages either; only `Konto 1`, `Konto 2`,
  `Konto 3`. Contacts stay in the local database and never go to logs.

## Non-goals
- No UI: lists and import are used through `IContactBook` and a probe; screens come with AP-08. No contact sync with providers (AP-10).
- `Act` is not switched on anywhere. No change to `MailKit*`, `SyncEngine`, `MailWarden.Presentation` or `MailWarden.App` (AP-04b owns these).
- No new package.

## Contract (binding)

### D1 - schema v6 (only this AP changes the schema)
- `contact_harvest_marker(folder_id PRIMARY KEY REFERENCES folders ON DELETE CASCADE, uid_validity, harvested_up_to_uid)`.
- `sender_list(entry TEXT NOT NULL COLLATE NOCASE, is_domain INTEGER NOT NULL, kind INTEGER NOT NULL, PRIMARY KEY (entry, is_domain))`. `kind`: 1 allowed, 2 blocked. An
  entry has one kind; setting it again replaces it.
- A database from v1-v5 migrates without data loss.

### D2 - stores (`MailWarden.Core.Classification`, implementation in `Infrastructure\Storage`)
```csharp
public sealed record SenderListEntry(string Entry, bool IsDomain, ListMembership Kind);
public interface ISenderListStore
{
    Task<IReadOnlyList<SenderListEntry>> ListAsync(CancellationToken ct = default);
    /// Address: must hold '@' and a domain. Domain: no '@', at least one dot. Trimmed; otherwise ArgumentException. Kind None → ArgumentException.
    Task SetAsync(SenderListEntry entry, CancellationToken ct = default);
    /// Unknown entry = no-op.
    Task RemoveAsync(string entry, bool isDomain, CancellationToken ct = default);
}
```
`IContactStore` gets `Task<Contact> AddIfMissingAsync(Contact contact, CancellationToken ct = default)`: an existing address stays unchanged, whatever its source, so a harvest never
overwrites a manual or imported name, and `Task RemoveAsync(string emailAddress, CancellationToken ct = default)`.

### D3 - directory
`ContactSenderDirectory(IEnumerable<string> contactAddresses, IEnumerable<string> ownAddresses, IEnumerable<SenderListEntry> lists)`, the old constructor stays with empty lists.
`GetListMembership`: an exact address entry wins over a domain entry; a domain entry matches the sender's organisational domain as computed by `DomainNames`; among
them Blocked wins over Allowed. Blank or malformed addresses give `None`. `ClassificationPass` gets `ISenderListStore` as a constructor parameter after `IContactStore`. Remember the rules of
`SpamClassifier`: Blocked beats everything, Allowed beats everything except failed authentication.

### D4 - harvest (`MailWarden.Infrastructure.Classification`)
```csharp
public sealed record ContactHarvestReport(int MailsRead, int ContactsAdded, int MailsSkipped);
public sealed class ContactHarvester
{
    public ContactHarvester(IAccountStore accounts, IFolderStore folders, IFolderSyncStateStore syncStates, IMessageStore messages, IEmlStore emlStore,
        IContactStore contacts, IContactHarvestMarkerStore markers, TimeProvider timeProvider);
    public Task<ContactHarvestReport> RunAsync(Guid accountId, CancellationToken ct = default);
}
```
- For each folder of the account with role Sent and a completed sync (sync state present, as in G1 of AP-07c): all local UIDs above the marker, ascending. Without a marker or with another
  UIDVALIDITY: from the start, unlike the classifier: the **whole stock** counts here, because it is exactly what Dennis wrote.
- From the headers of each EML: all addresses of `To`, `Cc`, `Bcc`, groups included. Name when present (cleaned with `SafeDisplayText`, at most 200 characters).
- Not added: own account addresses (all accounts), blank or malformed addresses, addresses without a domain, and the role addresses `noreply`, `no-reply`, `donotreply`, `do-not-reply`,
  `mailer-daemon`, `postmaster` as local part (case-insensitive).
- `AddIfMissingAsync` with `ContactSource.AutoCollected`.
- A missing or unreadable EML: `MailsSkipped`, no exception, the marker moves on. Cancellation propagates; the marker is committed per block of at most 200 mails, so a long first run can continue. Exceptions from the
  stores propagate.
- **Spoofing note (document it):** only `Sent` counts, so an incoming mail cannot create a contact. A contact alone never trusts a mail: `TrustedContact` also needs passing authentication.
- `IContactHarvestMarkerStore` follows `IFolderSyncStateStore`.

### D5 - cycle
`ClassifyingSyncCycle` gets a function `Func<Guid, CancellationToken, Task<ContactHarvestReport>> harvest` before its `classify`. Order: sync, harvest, classify. An exception from harvest is handled like one from classify (log line with type name,
propagates). `AccountPipelines` composes it on the cycle's connection. `AccountCycleSteps` gets an optional `Harvest`. A contact collected in this cycle counts for this cycle's classification.

### D6 - import (`MailWarden.Infrastructure.Classification`)
```csharp
public sealed record ContactImportReport(int Read, int Added, int AlreadyKnown, int Rejected);
public static class ContactImportFile
{
    /// Reads at most 10 MB (larger → InvalidDataException) and at most 50,000 entries; never logs values.
    public static Task<ContactImportReport> ImportVCardAsync(Stream file, IContactStore contacts, CancellationToken ct = default);
    /// Header row required, column names case-insensitive: Email, E-Mail, E-Mail Address, Name, Display Name, First Name, Last Name. Separator , or ; detected from the header. Quotes per RFC 4180.
    public static Task<ContactImportReport> ImportCsvAsync(Stream file, IContactStore contacts, CancellationToken ct = default);
}
```
- UTF-8 (with or without BOM) and UTF-16 with BOM. Folded vCard lines (RFC 6350), several `EMAIL` lines per card become several contacts, name from `FN`. `ContactSource.Imported`.
- A malformed entry counts as `Rejected` and never stops the file; malformed address as in D4. A file without a usable header: `InvalidDataException` naming the problem, not the values. A file
  path is never taken from foreign data.

### D7 - `IContactBook` (facade for AP-08 and the probe)
Small facade in `Infrastructure\Classification` over the two stores with `AddContact`, `RemoveContact`, `Allow`, `Block`, `Unlist`. No further logic.

### D8 - probe (`_scratch\contact-probe`, builder writes, planner runs on a copy)
`ContactProbe <dataRoot> harvest` runs the harvester for all accounts on a copy and prints only counts per account number. It never prints addresses. Needs `probe-copy.marker`.

## Expected behaviour (temp SQLite, artificial EML with `sender_a@example.test`)
| # | Case | Expected |
|---|---|---|
| 1 | Migration v5 → v6 and from v1 with data | tables exist, old data intact, version 6 |
| 2 | List store: set, replace kind, remove, list, invalid entries, case | per D2 |
| 3 | `AddIfMissingAsync`: new; existing manual with other name and other case; `RemoveAsync` | added; unchanged; removed |
| 4 | Directory: address allowed; domain blocked; sub-domain of a blocked domain; address allowed + domain blocked; both kinds for a domain; malformed | per D3 |
| 5 | Pass with a blocked sender in the Inbox and an allowed sender with failed authentication, real store | `CertainSpam`; not `TrustedContact` |
| 6 | Harvest first run with 3 sent mails, `To`, `Cc`, `Bcc`, group, name | contacts added once each with name and `AutoCollected`; report counts |
| 7 | Second run; a new sent mail | only the new one read |
| 8 | Own addresses, `noreply@…`, `MAILER-DAEMON@…`, malformed, no domain | not added; positive control next to them |
| 9 | Incoming mail in the Inbox and Junk | never harvested |
| 10 | Folder without a sync state; UIDVALIDITY changed | skipped; from the start |
| 11 | EML missing, EML garbage | `MailsSkipped`, marker moves on, others handled |
| 12 | 500 mails, cancellation after the first block | marker of the first block committed; next run continues; no duplicates |
| 13 | Existing manual contact in a sent mail with another name | stays unchanged |
| 14 | Cycle: order sync, harvest, classify; harvest throws (marker text) | classify not called, log line with type, marker nowhere, propagates |
| 15 | Contact from this cycle's sent mail, incoming mail from that address with passing authentication in the same cycle | `TrustedContact` |
| 16 | vCard: folded line, 2 `EMAIL`, no `EMAIL`, `FN` with umlaut, UTF-16 BOM | per D6 |
| 17 | CSV: `,` and `;`, quotes with separator inside, Outlook-style header, no usable header, malformed address, over 10 MB | per D6 |
| 18 | Stored names with control and bidi characters | cleaned |
| 19 | Real data, planner, copy: harvest, then `stock` of `classification-probe` | counts plausible against the simulated 418; no names printed |

## Done when
Build `-warnaserror`; S3 green; review APPROVE; security without Critical or High (triggers: import, classifier actions). Documenter: `docs\classifier.md`, `README.md`, PLAN rows, TODO (conditions before `Act`).

## Decisions author.0 and implement.1 (planner, 2026-11-01)
Binding additions to the contract:
- Names: `SqliteSenderListStore(SqliteDatabase)`, `SqliteContactHarvestMarkerStore(SqliteDatabase)`, `ContactHarvestMarker(long FolderId, uint UidValidity, uint HarvestedUpToUid)`,
  `IContactHarvestMarkerStore` with `GetAsync(folderId, ct)`, `SaveAsync(marker, ct)`; both in `MailWarden.Core.Classification`.
- `IContactBook`: `AddContactAsync(address, name, ct)`, `RemoveContactAsync(address, ct)`, `AllowAsync(entry, isDomain, ct)`, `BlockAsync(entry, isDomain, ct)`, `UnlistAsync(entry, isDomain, ct)`.
  `AddContactAsync` cleans the name like the harvest.
- Unreadable EML: bytes without a single header count as `MailsSkipped`, as does `FormatException`. `MailsRead` also counts mails without recipients. A recipient without a name gets an empty name.
- `ContactHarvester` has no `TimeProvider`; unknown account id → `ArgumentException`. `RemoveAsync` of an unknown contact is a no-op.
- Import: entries over 50,000 and a vCard without `EMAIL` count as `Rejected`; `Read = Added + AlreadyKnown + Rejected`.
- **Changes D3:** a domain entry matches when the sender's domain equals the entry or is a subdomain of it; never upward through the organisational domain. Blocked wins over all levels.
- `SetAsync` rejects a domain entry that is itself a public suffix (`co.uk`, `com`) through the built-in list (`DomainNames.IsPublicSuffix`), with `ArgumentException`.
- Role addresses match the whole local part only: `person_a@noreply.example.test` is collected.
- Accepted limits, in `TODO.md`: temporarily unreadable mails are not harvested again; vCard 2.1 quoted-printable in `FN` is not decoded; a suffix outside the built-in list is not recognised.
