# AP-03a – review.1 (reviewer, opus; recorded by the planner)

**Verdict: APPROVE** (diff `d39915b...feat/ap-03a-sync-down`, `src`, `PLAN.md`, `impl-notes.md`; `tests\` not read).
Build `-warnaserror` green. MailKit runtime behaviour (CONDSTORE with EXAMINE, SINCE via `DeliveredAfter`, BODY.PEEK)
not verified – case 28.

## Contract
R1–R11 kept; names/signatures exact; Core only records/interfaces/exception. EML before row, file before row on
delete; UIDVALIDITY stored only after purge; state only after complete folder; `UpdateFlagsAsync` one transaction;
migration v2 additive; transport messages without token/server text; read-only; transport disposed via `await using`.

## BLOCK
None.

## SHOULD
1. One permanently failing message blocks its folder forever (`SyncEngine.cs:250-256`, `:341-351`,
   `MailKitTransport.cs:278`) – e.g. Gmail "NO Some messages could not be FETCHed" or a non-FormatException from
   MimeKit. Contract-conform but harmful. → fix.2 F1.
2. Duplicate IMAP connect/folder listing vs. `ConnectionProbe.cs:101-124` (`MailKitTransportFactory.cs:38-57`,
   `MailKitTransport.cs:52-85`, second `Describe`), already diverging (namespace root). → deferred to AP-03b.
3. UIDVALIDITY 0 not rejected (`MailKitTransport.cs:104-108`) → R3 could never detect a reset. → fix.2 F3.

## NIT
- `SyncEngine.cs:307-311` `await using` directly after the null check. → F6
- `SyncEngine.cs:346` `LoadAsync(..., persistent: true, ...)`. → F6
- `MailKitTransport.cs:166-213` two round trips per message → AP-03b.
- Without CONDSTORE every run fetches all flags (`FETCH 1:*`) → AP-03b schedule.
- `SyncEngine.cs:302-305` transient FETCH null + new HighestSyncedUid → never fetched again → AP-03b.
- `SyncEngine.cs:70-75` XML doc of StorageException incomplete. → F6
- Factory does not check `token.Username == account.EmailAddress` (harmless).

## Builder questions
- Q1: add `title` to skipped elements, not the whole `<head>`. → F5
- Q2: local-only roles excluded from R1 when the outbox AP comes. → TODO AP-03b
- Q3: `IEmlStore.DeleteFolderAsync` + cleanup → TODO AP-03b
- Q4: reconnect after `ImapProtocolException` → AP-03b requirement line.
