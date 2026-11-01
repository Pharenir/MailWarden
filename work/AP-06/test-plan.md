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

## fix.2 (author.1): cases 20-26

| Case | Test | Notes |
|---|---|---|
| 20 | tests\MailWarden.Core.Tests\Classification\AllowListAndAlignmentTests.cs | Authenticated -> TrustedContact; no header, softfail+dmarc=none, dmarc=fail+compauth=pass, neutral -> LikelyLegit with override naming the allow list; failed as before; allow equals contact for Unknown |
| 21 | same file | evil.com.pl via dkim and spf not authenticated; same domain, child, parent (From a@mail.firma.com.pl), DMARC pass authenticated and TrustedContact; sibling sub-domain not |
| 22 | tests\MailWarden.Infrastructure.Tests\Classification\ContactHarvesterOriginTests.cs | other From, two From addresses, unusable From, auto-replied/auto-generated, multipart/report skipped; own address (any case, other account), Auto-Submitted: no, multipart/mixed read |
| 23 | ...\ContactSenderDirectoryHardeningTests.cs | Kelvin sign and trailing NBSP: no Allowed, Blocked matches; ASCII case controls |
| 24 | same file | quoted local part, Punycode vs U-form both ways, plus/dot variants exact, `*` rejected (4 forms) with plain-domain control |
| 25 | ...\ContactImportCsvTests.cs | unterminated quote in row 2 of 5 -> 1 Rejected, 4 Added; control with closed quote |
| 26 | ...\ContactBookTests.cs | trimmed address; `Hans <a@x.test>`, no domain, no `@`, empty -> ArgumentException |

Changed existing tests: `SpamClassifierTests.Allow_list_with_unknown_authentication_is_trusted_contact` renamed to
`Allow_list_with_unknown_authentication_is_at_most_likely_legit` (R1: expects LikelyLegit). No existing AP-07a test on sibling
sub-domains for DKIM/SPF was found (`DomainNamesTests` only tests `AreAligned`, which R2 does not change); the sibling cases are new.
`ClassificationHarness.AddSentAsync` got optional `from`, `extraHeader`, `contentType`.

Assumptions: the override text for the non-authenticated allow case contains "allow list"; an `*` anywhere in a list entry is rejected;
`dkim=pass header.d=com.pl` (public suffix as parent) is not tested because "real parent" is ambiguous there.

## fix.3 (author.2): cases 27-31 and the planner's later decisions

| Case | Test | Notes |
|---|---|---|
| 24 (changed) | ContactSenderDirectoryHardeningTests | quoted local part hits a Block entry (existing test) and never an Allow entry (new), unquoted Allow entry is the control |
| 27 | ContactSenderDirectoryHardeningTests | look-alikes U+FF45, U+3002, U+FF41 (placed in the domain of the address entry), mathematical letters: Allowed -> None, Blocked -> Blocked; controls ASCII and real Punycode sender |
| 28 | same | `sub.xn--mller-kva.example`, `ab--cd.xn--mller-kva.example`, the parent itself blocked; other domain None |
| 29 | Core.Tests AllowListAndAlignmentTests | DMARC pass for evil.com.pl not authenticated; same, child, parent-header.from authenticated |
| 30 | ContactBookTests | `RemoveContactAsync` with spaces and other case removes, the other contact stays |
| 31 | ContactHarvesterOriginTests | X-Autoreply, X-Autorespond, Precedence auto_reply/bulk/junk/BULK, Content-Class calendarmessage, text/calendar METHOD=REPLY skipped; Precedence list and METHOD=REQUEST read |
| decisions | ContactHarvesterOriginTests, ContactImportCsvTests, ContactBookTests | `Auto-Submitted: no (x)` and `nobody` skipped; other account's address is own; `"a"b` row Rejected (rest read), quote closing in a later line is a multi-line field; null address -> ArgumentException |
