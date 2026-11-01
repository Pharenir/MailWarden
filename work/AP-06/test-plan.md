# AP-06 test plan (author.0, written from `requirement.md` only, nothing run yet)

Tags: all new tests `feature:contacts`; storage tests also `feature:storage`, classifier tests also `feature:classifier`,
cycle tests also `feature:sync`. Smoke: one per file group (marked `smoke`). All data artificial.

## Case to test

| Case | Test file (tests\MailWarden.Infrastructure.Tests\...) | Notes |
|---|---|---|
| 1 | Storage\SchemaV6Tests.cs | fresh v6 tables/keys, marker cascade, NOCASE key, v5 and v1 migration with data, v4 file + AddIfMissing. `SchemaV5Tests` now uses `CurrentSchemaVersion` instead of 5 (the "newer version" test uses current+1) and exposes its SQL constants as `internal` |
| 2 | Storage\SqliteSenderListStoreTests.cs | set, replace, remove, no-op, case, trim, invalid entries, kind None, boundary positives |
| 3 | Storage\SqliteContactStoreAddIfMissingTests.cs | new, existing (all sources, other case/name), remove, re-add |
| 4 | Classification\ContactSenderDirectoryListTests.cs | address, domain, sub-domain, lookalikes, multi-label suffix, address vs domain both ways, both kinds, malformed (+control), old constructor |
| 5 | Classification\ClassificationPassListTests.cs | blocked address/domain -> CertainSpam (also beats contact; Act enqueues move), allowed + failed auth not TrustedContact; positive controls for each |
| 6 | Classification\ContactHarvesterTests.cs | To/Cc/Bcc/group/name, report, per-header theory, marker content |
| 7 | Classification\ContactHarvesterTests.cs | second run empty, new mail only |
| 8 | Classification\ContactHarvesterRulesTests.cs | own addresses (2 accounts), 10 role-address variants, 5 malformed variants, each with a control recipient |
| 9 | Classification\ContactHarvesterRulesTests.cs | Inbox/Junk/Quarantine/Screener/Archive/Trash never read, no marker; control: Sent read; incoming sender no contact |
| 10 | Classification\ContactHarvesterRulesTests.cs | no sync state skipped (then read once state exists); other UIDVALIDITY restarts; same validity continues; no marker = whole stock |
| 11 | Classification\ContactHarvesterTests.cs | missing file, garbage, no EmlPath -> skipped, marker moves, store exception propagates |
| 12 | Classification\ContactHarvesterRulesTests.cs | 500 mails, cancel at the 201st add; marker 200..201; resume; 500 distinct contacts; pre-cancelled token |
| 13 | Classification\ContactHarvesterTests.cs | manual and imported contact unchanged |
| 14 | Composition\ClassifyingSyncCycleHarvestTests.cs | order, args, harvest throws (type only, classify not called, propagates), async, cancel = no log, timeout-like = log, sync failure skips harvest. Existing `ClassifyingSyncCycleTests` rig got a no-op harvest parameter |
| 15 | Composition\ClassifyingSyncCycleHarvestTests.cs | real harvester + real pass in one cycle -> TrustedContact; control without harvest not trusted |
| 16 | Classification\ContactImportVCardTests.cs | folded lines, 2 EMAIL, no EMAIL, umlaut UTF-8 +/- BOM, UTF-16 LE/BE, LF endings, malformed, known, duplicate, no FN, empty, >10 MB (+control), cancel |
| 17 | Classification\ContactImportCsvTests.cs | `,` and `;`, header variants/case, quotes, doubled quotes, line break in quotes, Outlook header, Display Name priority, no usable header (message has no values), malformed, known, BOM/UTF-16, >10 MB (+control), cancel |
| 18 | harvest (ContactHarvesterRulesTests), vCard, CSV | control/bidi removed, harvest name <= 200 chars |
| 19 | not automated | planner, real data |

Positive controls: case 8 (control recipient in each mail), 9 (Sent folder read), 10 (sync state present / same validity),
5 (pass and no-block variants), 17 and 16 (below 10 MB), 14/15 (no-harvest variant).

## Questions and assumptions (the contract is silent)

1. Names of types not in the requirement, used by the tests (adapt in `ClassificationHarness`/tests if the builder names differ):
   `SqliteSenderListStore(SqliteDatabase)`, `SqliteContactHarvestMarkerStore(SqliteDatabase)`,
   `ContactHarvestMarker(long FolderId, uint UidValidity, uint HarvestedUpToUid)`, marker store `GetAsync(long folderId)` / `SaveAsync(marker)`
   (like `IFolderSyncStateStore`). The marker types are assumed in `MailWarden.Core.Classification`, `Core.Sync` or `Core.Storage`
   (all imported by the tests that use them).
2. `IContactBook`: member signatures not given -> not tested (D7 says no further logic).
3. Report meaning: `MailsRead` = EMLs read successfully (also without recipients), `MailsSkipped` = missing/unreadable, `ContactsAdded` = new rows only.
   Import report: `Read = Added + AlreadyKnown + Rejected` (asserted where no card lacks EMAIL).
4. "EML garbage" is tested with binary bytes without any header; MimeKit might read that as a header-less mail (then `MailsRead`, not skipped). If so, the contract needs a statement on what counts as unreadable.
5. `ContactStore.RemoveAsync` of an unknown address is assumed a no-op (as for the sender list).
6. Role addresses are matched on the whole local part only; `person_a@noreply.example.test` is collected.
7. Cancellation test of case 12 cancels on the 201st `AddIfMissingAsync`; expects marker 200..201 (block of at most 200 committed).
8. Not tested (not specified): behaviour above 50,000 import entries, a vCard without EMAIL counted as Rejected or not, name for nameless recipients, unknown account in the harvester,
   `AccountPipelines`/`AccountCycleSteps.Harvest` composition (needs the real runtime; planner may add to the probe), the probe (D8).
9. Domain list entry for a sub-domain (e.g. `mail.blocked.test`) is not tested: D3 only says "organisational domain".
10. Domain wins/loses between lists: duplicates with both kinds are fed straight to the directory constructor (the store itself keeps one kind).
11. Compilation not checked in author.0 (no builder code on this branch).
