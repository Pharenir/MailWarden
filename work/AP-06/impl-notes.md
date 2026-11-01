STEP-RESULT: done (fix.3: T1-T5 done, see section "fix.3")

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

## fix.2 (R1-R8 of `requirement.md`)
| Rule | File | Change |
|---|---|---|
| R1 | `src\MailWarden.Core\Classification\SpamClassifier.cs` | allowed + `Authenticated`: `TrustedContact`, text "Sender is on the allow list and authenticated (...)"; allowed + `Unknown`: `LikelyLegit`, text "Sender is on the allow list but authentication could not be verified"; allowed + `Failed`: score tiers, no override, as before |
| R2 | `src\MailWarden.Core\Classification\AuthStatusEvaluator.cs`, `DomainNames.cs` | DKIM and SPF through the new internal `DomainNames.AreSameOrParentAndChild`; the DMARC path keeps the organisational comparison |
| R3 | `src\MailWarden.Infrastructure\Classification\ContactHarvester.cs` | `IsWrittenByUser`: exactly one From header with exactly one mailbox from the account addresses of all accounts; `Auto-Submitted` other than `no`; top Content-Type `multipart/report`. All count as `MailsSkipped` |
| R4 | `src\MailWarden.Core\Classification\AddressRules.cs`, `ContactSenderDirectory.cs` | new `AddressRules.IsExactSender`; an allow entry only applies when it is true |
| R5 | `AddressRules.cs`, `ContactSenderDirectory.cs`, `SqliteSenderListStore.cs` | `ToLookupForm` removes quotes around a fully quoted local part; new `AddressRules.NormalizeDomain` (trim, trailing dots, lower case, `IdnMapping.GetUnicode`) for senders and entries; `SetAsync` rejects `*`; plus and dot variants documented in the directory and in `ToLookupForm` |
| R6 | `src\MailWarden.Infrastructure\Classification\ContactImportFile.cs` | row reader per physical start; an unterminated quote gives one `Rejected`, reading goes on with the next line |
| R7 | `src\MailWarden.Infrastructure\Classification\ContactBook.cs` | `AddContactAsync` trims and checks with `AddressRules.IsWellFormedAddress`, otherwise `ArgumentException` |
| R8 | `AddressRules.cs`, `src\MailWarden.Infrastructure\Background\MailboxService.cs` | `U+FFFD` as an escape; comments name the harvest |

### Decisions where R1-R8 leave room
- **R2, public suffix as parent:** `AreSameOrParentAndChild` does not accept a public suffix (single label or built-in list) as the parent. Without this `header.d=test` would authenticate `a@example.test`, which the old comparison did not. A suffix outside the built-in list counts as a parent (`header.d=com.pl` for `a@firma.com.pl`); signing there takes control of that suffix.
- **R2, child of a shared domain:** a child passes as R2 demands, so From `a@shared.test` with `header.d=user.shared.test` is authenticated when `shared.test` hands out sub-domains to strangers and is not in the built-in list. Only relevant for a contact or allow entry directly on such a domain.
- **R3, from whom:** the account addresses of all accounts count as own, not only the account of the folder. A mail without From, with two From headers or with two mailboxes in From is skipped. An `Auto-Submitted` value with a comment (`no (x)`) counts as not `no`, the safe side.
- **R4, spelling hides no block entry:** a sender that is not exact skips only allow entries. An address entry Allowed plus a domain entry Blocked gives Blocked for `a@blocked.example.test.`, not `None`.
- **R4 with R5:** "normalised" in R4 is `DomainNames.Normalize` (trim, trailing dots, lower case), before the Punycode step. So both `a@xn--...` and the U-form with lower-case letters can be allowed. A U-form with an upper-case non-ASCII letter (`a@MÜLLER.test`) cannot be allowed, because R4 only forgives ASCII case; Blocked still matches it.
- **R4 with R5, quoted senders:** a quoted local part is not well-formed, so `"friend"@x.test` matches a block entry but never an allow entry. Case 24 is met for block entries; against an allow entry the result is `None`.
- **R5:** empty quotes (`""@x.test`) stay as they are, so the domain entries still see a local part. A domain that `IdnMapping` refuses stays in its lower-case form, for sender and entry alike. `*` is rejected for address and domain entries.
- **R6, what counts as unterminated:** a text that ends inside quotes, or a closing quote that is not followed by a separator or line end (spaces and tabs before them are allowed). A quote that happens to close properly on a later line stays a legal multi-line field, because RFC 4180 allows it, so that row is one entry over several lines. Before fix.2 `"a"b` was read as `ab`; now its line is `Rejected`.
- **R7:** a null address also gives `ArgumentException`, not `ArgumentNullException`, because it is the same rule.

### What turns red by design
R1 and R2 change expectations of AP-07a; R3 needs a From with an account address in every harvest fixture; case 19 runs again. `PLAN.md` #6 not changed, the requirement gives it to the documenter.

### Tested / not tested (fix.2)
- Build: `dotnet build MailWarden.sln -warnaserror`: 0 warnings, 0 errors, test projects compiled but not run. `tests\` not read and not changed. `_scratch\contact-probe` needed no change; R3 shows up in its `mails skipped` count. It was built, not started.
- Own control, no proof: `_scratch\contact-selfcheck` (not in the repo, artificial data, prints labels and counts only), green, with cases 20-26 and the coordinator's precisions (one-label and listed parent, sibling, two or unusable From). IDN behaviour observed on this machine only.
- Not exercised: the pass and the cycle at runtime with R1, the migration from v1 with data, case 19 (planner runs it again). No tester run. Tests of AP-07a about R1 and R2 are expected to be red.

## fix.3 (T1-T5 of `requirement.md`; they change R2 and R5)
| Rule | File | Change |
|---|---|---|
| T1 | `src\MailWarden.Core\Classification\AddressRules.cs` | `IsExactSender` also needs `IsStableLabel` for every label: `NormalizeDomain` leaves it unchanged, or it starts with `xn--` |
| T2 | `src\MailWarden.Infrastructure\Classification\ContactSenderDirectory.cs` | `GetDomainMembership` normalises each level on its own |
| T3 | `src\MailWarden.Core\Classification\AuthStatusEvaluator.cs` | the DMARC path uses `DomainNames.AreSameOrParentAndChild`; private `IsAligned` removed, it had no other caller |
| T4 | `src\MailWarden.Infrastructure\Classification\ContactBook.cs` | `RemoveContactAsync` trims; null gives `ArgumentNullException` as in the store |
| T5 | `src\MailWarden.Infrastructure\Classification\ContactHarvester.cs` | `IsAutomaticMarker` knows the further marks; all count as `MailsSkipped` |

### Decisions where T1-T5 leave room
- **T2, look-alike dots:** the walk also splits at U+3002, U+FF0E and U+FF61. When `IdnMapping` refuses a whole domain these dots are still in it, and `ab--cd\u3002xn--mller-kva.example` would hide the blocked parent otherwise.
- **T3:** a DMARC pass for an unrelated domain falls through to DKIM and SPF as before, so an aligned DKIM pass can still authenticate. The result is `Unknown` when nothing else passes. A DMARC pass without `header.from` stays `Authenticated`, as in C22.
- **T3, sibling:** `news.firma.test` with `header.from=mail.firma.test` is no longer authenticated on the DMARC path. A provider's relaxed DMARC can pass exactly there, so real mail can drop to `Unknown`. The planner's count before `Act` (`TODO.md`) can show how many.
- **T3, left alone:** `DomainNames.AreAligned` and `TryGetOrganizationalDomain` keep their code; removing them is no part of T3.
- **T5:** `X-Autoreply` and `X-Autorespond` count by presence, whatever the value. `Precedence` compares the keyword before a `;`, case-insensitively. `method` on `text/calendar` is compared case-insensitively. `Content-Class` by substring, case-insensitively. Only the top headers are read, so a `text/calendar` part inside a multipart mail does not count, as T5 says "top".

### Tested / not tested (fix.3)
- Build: `dotnet build MailWarden.sln -warnaserror`: 0 warnings, 0 errors, test projects compiled but not run.
- Own control, no proof: `_scratch\contact-selfcheck` (not in the repo, artificial data, prints labels and counts only), green, with cases 27-31. IDN behaviour observed on this machine only.
- Not exercised: pass and cycle at runtime with T3. No tester run. Tests of AP-07a and of fix.2 about DMARC with a sibling domain, and harvest fixtures with the new markers, can turn red. `tests\` not read and not changed. `_scratch\contact-probe` needed no change.
