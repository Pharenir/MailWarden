STEP-RESULT: done (implement.1, including the coordinator's precisions and decisions of 2026-11-01)

# AP-06 – Implementation notes (Builder, implement.1)

Branch `feat/ap-06-address-book`. No package added. Schema v6. Nothing under `tests\` read or changed. `MailKit*`, `SyncEngine`,
`MailWarden.Presentation` and `MailWarden.App` not touched. `requirement.md` and `PLAN.md` not changed (`PLAN.md` has no row table
for AP-06 yet; the requirement gives the rows to the documenter).

## Files
| File | Change |
|---|---|
| `src\MailWarden.Core\Classification\SenderListEntry.cs`, `ISenderListStore.cs` | new (D2) |
| `src\MailWarden.Core\Classification\ContactHarvestMarker.cs`, `IContactHarvestMarkerStore.cs` | new (D4, precision: only `GetAsync` and `SaveAsync`) |
| `src\MailWarden.Core\Classification\AddressRules.cs` | new: the one rule for malformed addresses and domains, and the lookup form of a sender |
| `src\MailWarden.Core\Classification\ISenderDirectory.cs` | doc comment only: domain rule of the precision |
| `src\MailWarden.Core\Storage\IContactStore.cs` | `AddIfMissingAsync`, `RemoveAsync` (D2) |
| `src\MailWarden.Infrastructure\Storage\SchemaMigrations.cs`, `SqliteDatabase.cs` | version 6 appended, `CurrentSchemaVersion = 6` (D1) |
| `src\MailWarden.Infrastructure\Storage\SqliteSenderListStore.cs`, `SqliteContactHarvestMarkerStore.cs` | new (D2, D4) |
| `src\MailWarden.Infrastructure\Storage\SqliteContactStore.cs` | the two new methods (D2) |
| `src\MailWarden.Infrastructure\Classification\ContactSenderDirectory.cs` | third constructor parameter, `GetListMembership` (D3 with the precision) |
| `src\MailWarden.Infrastructure\Classification\ClassificationPass.cs` | constructor parameter `ISenderListStore senderLists` after `IContactStore contacts` (D3) |
| `src\MailWarden.Infrastructure\Classification\ContactHarvester.cs`, `ContactHarvestReport.cs`, `ContactNames.cs` | new (D4); `ContactNames` is internal and shared with the import |
| `src\MailWarden.Infrastructure\Classification\ContactImportFile.cs`, `ContactImportReport.cs` | new (D6) |
| `src\MailWarden.Infrastructure\Classification\IContactBook.cs`, `ContactBook.cs` | new (D7) |
| `src\MailWarden.Infrastructure\Composition\ClassifyingSyncCycle.cs` | parameter `harvest` before `classify`, order sync, harvest, classify, both steps through `RunLoggedAsync` (D5) |
| `src\MailWarden.Infrastructure\Composition\AccountCycleSteps.cs` | last member `Harvest = null` (D5) |
| `src\MailWarden.Infrastructure\Composition\AccountPipelines.cs`, `MailWardenRuntime.cs` | harvester on the cycle's connection, `HarvestAccountAsync`, wiring (D5) |
| `_scratch\contact-probe\` (not in the repo) | D8; built, not started; also copied to `_scratch\contact-probe` of the main folder |

## Names for the tester
- `SqliteSenderListStore(SqliteDatabase)`, `SqliteContactHarvestMarkerStore(SqliteDatabase)`.
- `ContactHarvestMarker(long FolderId, uint UidValidity, uint HarvestedUpToUid)`; `IContactHarvestMarkerStore.GetAsync(long folderId, ct)`,
  `SaveAsync(ContactHarvestMarker marker, ct)`; both in `MailWarden.Core.Classification`.
- `ContactBook(IContactStore contacts, ISenderListStore senderLists) : IContactBook` with `AddContactAsync(string address, string name, ct)`,
  `RemoveContactAsync(string address, ct)`, `AllowAsync(string entry, bool isDomain, ct)`, `BlockAsync(string entry, bool isDomain, ct)`,
  `UnlistAsync(string entry, bool isDomain, ct)`.
- `AccountCycleSteps(Sync, IdleWatchers, Classify = null, ExecuteCommands = null, Harvest = null)`.
- `AddressRules.IsWellFormedAddress(string?)`, `IsWellFormedDomain(string?)`, `ToLookupForm(string?)`.
- `ContactHarvester(accounts, folders, syncStates, messages, emlStore, contacts, markers)`, without the `TimeProvider` of D4 (decision 1).
- `DomainNames.IsPublicSuffix(string)`: new public member (decision 2).

## What compiles differently now
Expected to break existing tests until they are adapted: the constructors of `ClassificationPass` and `ClassifyingSyncCycle`, every fake
of `IContactStore`, and any assertion on schema version 5.

## Decisions where the contract leaves room
- **Malformed address** (D2, D4, D6, one rule, `AddressRules.IsWellFormedAddress`): exactly one `@` with text on both sides, no empty domain
  label, one label is enough (`user@localhost` passes), at most 254 characters, no whitespace, no characters that `SafeDisplayText` would
  change, no U+FFFD, none of `<>()[]\,;:"`. So quoted local parts cannot be collected, imported or listed by address.
- **Malformed domain entry**: additionally no empty label, so `example.test.` and `.test` are rejected, at most 253 characters.
- **Directory lookup is more tolerant on purpose** (`AddressRules.ToLookupForm`): trimmed, last `@`, trailing dot of the domain removed. A
  quoted local part or `x@blocked.example.test.` must not carry a sender past the block list. `@domain`, `x@`, blank and null give `None`.
- **Domain walk** (precision): all levels of the sender's domain are looked up. If any applying entry is Blocked the result is Blocked, also
  when a more specific level says Allowed (D3: "among them Blocked wins").
- **`MailsRead`**: mails whose headers were read, with or without recipients. **`MailsSkipped`** is disjoint: `EmlPath` null, EML missing, any
  read failure of the EML, `FormatException`, bytes without one header. Only the EML part is caught; the database stores propagate.
- **One malformed address in a header**: the parser gives up on the whole header value, so `ContactHarvester.AddRecipientsOfBrokenList` tries each
  piece between `,`, `:`, `;` outside quotes alone. Observed in the self-control, without this the positive control next to a malformed entry
  was lost.
- **Added or known** is decided against an in-memory set from `IContactStore.ListAsync`, because `AddIfMissingAsync` does not say whether it added.
  The set compares with `OrdinalIgnoreCase`, like the directory, the column `NOCASE` only knows ASCII letters.
- **Import**: the file is parsed completely before the first contact is stored, so an `InvalidDataException` stores nothing. 10 MB means
  10 * 1024 * 1024 bytes. `Read` is one per `EMAIL` line, one for a card without `EMAIL`, one per CSV row; blank lines are no entries.
  An address that repeats in the file is `AlreadyKnown`. A card without `END` and a row that ends inside quotes are one `Rejected` each.
  Own and role addresses are not filtered in the import (D6 only names "malformed as in D4"). Name order in CSV: Display Name, Name, First + Last.
- **Name**: `SafeDisplayText.Shorten`; a cut name is 199 characters plus "…", so it never exceeds 200.
- **Cycle**: the harvest report is dropped. `MailsSkipped` does not reach the log (D5 only asks for the exception line).

## Spoofing note (D4, also in the class remarks of `ContactHarvester`)
Only `Sent` counts, so an incoming mail cannot create a contact. A contact alone never trusts a mail: `TrustedContact` also needs passing
authentication. The documenter takes this to `docs\classifier.md`.

## Deviations
- D3 as written (organisational domain) is replaced by the coordinator's precision: sender domain equals the entry or is a sub-domain of it.
- D8 prints the account numbers in storage order (rowid), the same numbering as `classification-probe`.
- D4: the constructor has no `TimeProvider` (decision 1). A harvest stores no time.
- D2: `SetAsync` also rejects a domain entry that is itself a public suffix (decision 2). `IsPublicSuffix` knows single labels and the built-in
  list of `DomainNames`; a suffix outside that list passes. The constructor of `ContactSenderDirectory` does not check this: it takes what the store holds.
- D7: `AddContactAsync` cleans the name with `ContactNames.Clean` (decision 3), the one rule beyond the facade.
- No deviation otherwise.

## Tested / not tested
- Build: `src\MailWarden.Infrastructure` and `src\MailWarden.Presentation` with `-warnaserror`: 0 errors, 0 warnings. `_scratch\contact-probe` builds. `src\MailWarden.App` and the test projects not built.
- Own control, no proof: `_scratch\contact-selfcheck` (not in the repo, artificial data, temp folder below `_scratch`, prints labels only), green.
  Covered: cases 1 (v5 to v6 only), 2, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 16, 17, parts of 18 (bidi in a name, length).
- Not exercised: cases 5, 14, 15 (pass and cycle at runtime), migration from v1 with data, EML with a locked file, case 19 (planner). The probe was not started. No tester run.

## Questions
None open. Decided by the coordinator on 2026-11-01: the `TimeProvider` is dropped, public suffixes are rejected, manual names are cleaned, and the
coordinator changes `_scratch\classification-probe\Program.cs` in the main folder (it needs `new SqliteSenderListStore(database),` after line 454).

## Known limits
- vCard 2.1 `ENCODING=QUOTED-PRINTABLE` is not decoded; such an `FN` is stored as its encoded text, cleaned. The address is unaffected.
- As in the contract, the marker moves on past a mail that was only temporarily unreadable, and a mail that appears locally with a UID below the marker is not read.
- The coordinator records the temporarily unreadable mails and vCard 2.1 quoted-printable in `TODO.md`; both stay as they are.
