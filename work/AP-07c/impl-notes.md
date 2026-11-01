STEP-RESULT: done (fix.3: H1–H7 done, see section "fix.3"; H8 is the documenter's, D8 is not part of this step)

# AP-07c – Implementation notes (Builder, implement.1)

Branch `feat/ap-07c-classifier-pipeline`. No package added. Nothing under `tests\` changed. `requirement.md` and
`PLAN.md` not changed (documenter fills PLAN). Not touched: `IMessageStore`, `MailboxService`, `SyncEngine`,
`MailWardenRuntime`, `src\MailWarden.Presentation`, `src\MailWarden.App`, `FolderLookup`.

The planner decisions of 2026-11-01 (sent during this step, in addition to `requirement.md`) are applied. The only one
that changed code already written: the policy file now rejects comments.

## Files
| File | Change |
|---|---|
| `src\MailWarden.Core\Classification\ClassificationMode.cs`, `ClassificationAction.cs`, `ClassificationOutcome.cs` | new enums, D1 |
| `src\MailWarden.Core\Classification\ClassificationPolicy.cs` | new, D1; extra const `MaxQuarantineRetentionDays` |
| `src\MailWarden.Core\Classification\ClassificationRouting.cs`, `MessageKey.cs`, `ClassificationLogEntry.cs` | new, D1 |
| `src\MailWarden.Core\Classification\IClassificationLog.cs`, `ClassificationMarker.cs` | new, D2 |
| `src\MailWarden.Core\Storage\IContactStore.cs` | new, D2 |
| `src\MailWarden.Core\Sync\IMailFolderProvisioner.cs` | new, D6 |
| `src\MailWarden.Core\Accounts\FolderRoleResolver.cs` | `ScreenerName`, `QuarantineName` private → public, so the provisioner uses the same names; behaviour unchanged |
| `src\MailWarden.Infrastructure\Storage\SchemaMigrations.cs`, `SqliteDatabase.cs` | schema v5 appended, `CurrentSchemaVersion` = 5 |
| `src\MailWarden.Infrastructure\Storage\SqliteClassificationLog.cs`, `SqliteContactStore.cs` | new, D2 |
| `src\MailWarden.Infrastructure\Classification\ClassificationPolicyFile.cs` | new, D3 |
| `src\MailWarden.Infrastructure\Classification\ClassificationInputReader.cs` | new, D4 |
| `src\MailWarden.Infrastructure\Classification\ContactSenderDirectory.cs` | new, D5 |
| `src\MailWarden.Infrastructure\Sync\MailKitFolderProvisioner.cs` | new, D6 |
| `src\MailWarden.Infrastructure\Classification\ClassificationPass.cs`, `ClassificationPassReport.cs` | new, D7 |
| `_scratch\classification-probe\` | D9. **Not in git:** `_scratch` is ignored. It lives only in this worktree, so it must be run or copied before the worktree is removed |

## Interface deviations and additions
Names, signatures, namespaces and constructors match D1–D7. Additions and readings where the contract is open:
- `ClassificationInputReader` is `public static partial class` (for the generated regex); callers see no difference.
- `ReadAsync` throws `FormatException` for a stream MimeKit cannot parse. D4 only names null for the From cases; the pass
  turns the exception into `ReadFailed`.
- `ClassificationPassReport.TierCounts` always holds all four tiers, also with 0.
- `ContactCount` also leaves out an own address that is stored as a contact row. This is the stricter reading of safety
  decision 3 and case 16: an own address in the address book cannot lift the suspension.
- `IsContact` is true for contacts and own addresses (case 10).
- Policy file: trailing commas are still allowed as in `ClassifierSettingsFile`; the planner decision only excludes
  comments. Range errors keep the `ArgumentException` of `Validate` as the inner exception.
- `SqliteContactStore.UpsertAsync`: the latest write wins for address spelling, name and source.
- `MessageKey`, `h:` form: the row's `Date` is what `SyncEngine` stored, i.e. the Date header or INTERNALDATE as fallback.
- Message row gone between the UID list and the load (`GetByUidAsync` null): skipped, not counted. Single caller, so this
  should not occur.
- No new UIDs above the marker: no commit, nothing written.

## Decisions with a safety reason
- `MailKitFolderProvisioner.cs:59-72`: a folder stored locally with a non-zero UIDVALIDITY is **not** overwritten, although
  D6 says "reads their UIDVALIDITY and stores them". The sync compares the stored value with the server to detect a reset
  (`SyncEngine` R3). Overwriting it would hide a reset while old rows still point at other messages, and retention deletes
  by UID. New rows and rows with 0 get the server value. The provisioner runs when only one of the two folders is missing,
  so this case is real.
- `MailKitFolderProvisioner.cs:96-108`: a server folder that does not resolve to the expected role (`\Noselect`, a
  special-use flag, or a lower-case `screener` on a case-insensitive server) raises `MailTransportException` (Protocol). The
  pass then stops with nothing stored and the mail stays. Reusing it would contradict `FolderRoleResolver` at the next sync.
- `ClassificationPass.cs:396-405`: retention requires `Enqueued` + `MoveToQuarantine` + stored time. Rejected retention
  commands are not counted and are found again by the next pass (the contract only specifies rejection for moves).

## Check of `MailKitFolderProvisioner` (code reading only, no server)
Source: MailKit tag **4.18.1**, because `4.18.2` (the package in use) has no tag on GitHub.
`Ich bin mir unsicher:` whether 4.18.2 differs in these paths. `QueueCreateCommand` is identical on `master`;
`QueueStatusCommand` differs only in how the capability is tested.

| Step in our code | Commands on the wire | Evidence (4.18.1) |
|---|---|---|
| `ImapSession.ConnectAsync` | connect, CAPABILITY, AUTHENTICATE, then session setup through NAMESPACE and LIST | existing code path, same as the read-only sync session |
| `ImapSession.ListPersonalFoldersAsync` (`subscribedOnly: false`, `StatusItems.None`) | `LIST "" "*"` | `ImapEngine.cs:4075-4085` |
| `CreateAsync(name, isMessageFolder: true)` on the namespace root | `CREATE "Screener"`, then `LIST "" "Screener"` | `ImapFolder.cs:827-847`, `794-803`, `875-884` |
| `StatusAsync(StatusItems.UidValidity)` | `STATUS "Screener" (UIDVALIDITY)`; `CheckState(false, false)`, so no open folder is needed and none is opened | `ImapFolder.cs:2124-2137`, `2264-2267` |
| reading the value | the untagged STATUS response sets `folder.UidValidity` | `ImapEngine.cs:2634-2639`, `ImapFolder.cs:6340-6345` |
| `DisconnectQuietlyAsync` | `LOGOUT` | `ImapClient.cs:1830-1833` |

- The class never calls `OpenAsync`, `ExpungeAsync`, `MoveToAsync`, `AddFlagsAsync` or `SubscribeAsync`. With no folder
  selected, no CLOSE and no expunge can occur.
- MailKit treats `ALREADYEXISTS` on CREATE as success (`ImapFolder.cs:850-858`), so a race with another device is harmless.
- The new folder is not subscribed (no SUBSCRIBE sent). The sync lists with `subscribedOnly: false`, so this app sees it.
  `Ich bin mir unsicher:` whether other clients show an unsubscribed folder; check in case 33.
- `Ich bin mir unsicher:` whether Outlook.com and Gmail return UIDVALIDITY in STATUS for an empty new folder. A value of 0
  is a Protocol failure with nothing stored. Check in case 33.

## Tested / not tested
**Tested by me (self-control, no proof, no replacement for a tester run):**
- `dotnet build src\MailWarden.Infrastructure\MailWarden.Infrastructure.csproj -warnaserror`: 0 errors, 0 warnings.
- `dotnet build MailWarden.sln -warnaserror`: 0 errors, 0 warnings.
- `_scratch\classification-probe`: builds with `-warnaserror`, **never started**.
- `_scratch\ap07c-selfcheck` (not in git): artificial EMLs, in-memory and temp SQLite, fake provisioner, real queue; all
  checks passed. It covers input reader cases, policy file errors, migration from a hand-downgraded v4, first pass,
  RecordOnly, Act by tier, rescue from Junk, duplicates, known keys, three `ReadFailed` kinds, suspension with only an own
  address, provisioner called once, retention, UIDVALIDITY reset, unknown account, pre-cancelled token, 2,500 mails in
  chunks and the account cascade.
- Existing schema tests, run singly: **10 fail because they expect version 4** (list below). This is expected and the
  test files are unchanged.

**Not tested:**
- `MailKitFolderProvisioner` was never run (no server, by order); only read and compiled.
- The probe in both modes; cases 32 and 33.
- Cases in the self-check are not a complete match of the table: case 24 (queue rejects), case 23 (no Junk folder), case
  29 (cancellation between two folders), the folder cascade of case 6 and v1 → v5 migration were not exercised.
- The rest of the existing suite was not run.

## Existing tests that hard-code schema version 4 (tester)
`tests\MailWarden.Infrastructure.Tests\Storage\`:
- `SchemaV2Tests`: `Current_schema_version_is_four`, `Fresh_database_has_current_version_and_the_sync_state_table`,
  `V1_database_migrates_in_place_and_keeps_its_rows`, `Reopening_a_migrated_file_keeps_current_version_and_the_data`
- `SchemaV3Tests`: `V2_database_migrates_to_v3_renames_the_column_and_resets_the_value`,
  `Reopening_a_v3_file_keeps_the_saved_floor`
- `SchemaV4Tests`: `Current_schema_version_is_four`, `Fresh_database_is_version_four_and_has_the_command_table`,
  `V3_database_migrates_to_v4_keeps_old_rows_and_gets_the_command_table`, `Reopening_a_v4_file_keeps_the_queued_commands`

## Questions to the planner
1. **Filter bypass through a repeated Message-ID** (`ClassificationPass.cs:206-212`, consequence of safety decision 4 and
   the `m:` key). The Message-ID is set by the sender. Two mails with the same ID: the first is decided, the second counts
   as `SkippedKnown` and stays in the Inbox unclassified, whatever it contains. Binding contract, so built as written. For
   the security review. A possible mitigation is to treat a key as known only when the sender also matches, but that
   needs a contract change.
2. **Filter bypass through `ReadFailed`** (`ClassificationInputReader.cs:37`, `ClassificationPass.cs:245`, `278-293`). A
   sender can force `ReadFailed` with two From headers or an unparsable From: the mail stays in the Inbox and is never
   filtered. Contract says no action, built as written. Should Inbox mail with no usable From go somewhere visible later
   (AP-08)?
3. **`ReadFailed` is final** (`ClassificationPass.cs:278-293`). The entry makes the key known, so a mail whose EML was
   briefly locked or missing is never classified again. It fails safe, since the mail stays. Is that intended?
4. **Retention repeats while a delete is waiting** (`ClassificationPass.cs:409-416`). Until the executor removes the local
   rows, every pass enqueues the same UIDs again. Harmless on the server (UIDs gone, UIDVALIDITY checked), but a failing
   delete (e.g. `NoTrashFolderError`) adds one more failing command per pass. A fix would read `IMailCommandQueue.ListAsync`
   and skip UIDs that are already queued. Not built: the contract does not include it and test fakes may not implement
   `ListAsync`. Decide before D8, because D8 starts the repeating.
5. **Retention goes through Trash.** `CommandExecutor` handles `DeletePermanently` outside Trash/Junk as a move to Trash
   plus expunge of the new UIDs. An account without a Trash folder fails permanently. Unchanged, for `docs\classifier.md`.
6. **UIDs below the marker** (`ClassificationPass.cs:189`). The sync can fetch a previously undelivered UID later. If that
   UID is below the marker it is never classified and stays where it is. Fails safe, for `docs\classifier.md`.
7. **The probe's `pass` in Act never executes the queue.** Case 33 ("Act moves test mails") also needs a command cycle,
   e.g. the existing `command-probe`, whose guard only allows Junk actions. It may need its own runner.

## fix.2 (decisions G1–G14 of `requirement.md`, including the planner's precision on G2)
Questions 1, 2 and 4 above are answered by G2, G7 and G6. Question 7 by G14. Questions 3, 5 and 6 are decided under "Not
fixed, on purpose".

### Check of secure H2 (G4): confirmed
`Header.Value` decodes encoded words: `Header.cs:484` in MimeKit tag 4.18.1 is
`textValue ??= Unfold (Rfc2047.DecodeText (Options, rawValue));`. `RawValue` is the value "before any decoding" (`Header.cs:463-470`).
There is no 4.18.2 tag on GitHub, so this was also checked by running against the package in use (4.18.2), with artificial data in
the self-check. The header was `mx.example.test; spf=none smtp.mailfrom==?utf-8?B?<base64 of ;dmarc=pass header.from=example.test>?=; dmarc=fail header.from=example.test`, the sender a contact:

| Value given to the classifier | AuthStatus | Tier |
|---|---|---|
| `Header.Value` (implement.1) | Authenticated | TrustedContact |
| raw value (fix.2) | Failed | LikelySpam |

So H2 was a real, exploitable defect of implement.1, not only a conditional one.

### Implementation per decision
| G | Status | Where | Note |
|---|---|---|---|
| G1 | done | `ClassificationPass.cs:205` | constructor parameter after `IFolderStore`; no state → folder skipped entirely. Retention does not look at the sync state: it is not in step 4 and its check is by key |
| G2 | done | `MessageKey.cs`, `ClassificationPass.cs:244-245`, `452-459` | `For` normalises itself; null, empty and whitespace give the same key. Pass and retention both use `TryReadContentAsync`, then `MessageKey.For(message, content.TopReceived)` |
| G3 | done | `ClassificationInputReader.cs`, `ClassificationMessage.cs` | `ReadAsync` is the thin wrapper |
| G4 | done, **reading to check** | `ClassificationInputReader.cs`, `GetRawValue` | see below |
| G5 | done | `ClassificationInputReader.cs` | also `Parsed` false for a parse with no header at all |
| G6 | done | `ClassificationPass.cs:435-443` | any command of the folder with the same UIDVALIDITY counts, see below |
| G7 | done | `ClassificationPass.cs`, `Decide`; `ClassificationRouting.RouteWithoutSender` | new public method, see below |
| G8 | done | `ClassificationPassReport.cs`, `ClassificationPass.cs:480` | last record member |
| G9 | done | `ClassificationRouting.cs` | explicit arms in both tables, undefined tier throws |
| G10 | done | class remarks and comment in `HandleFolderAsync` | |
| G11 | done | `ClassificationInputReader.cs`, `AddHost` and both loops | HTML and text |
| G12 | done | `MailKitFolderProvisioner.cs:143` | `SUBSCRIBE %F` (`ImapFolder.cs:1428-1433`, tag 4.18.1); only for a folder just created; failure ignored except cancellation; interface doc adjusted |
| G13 | no code | | already built so in implement.1; docs of `IMailFolderProvisioner` now state it |
| G14 | done, not started | `_scratch\classification-probe\Program.cs` | not in git, see below |

### Readings and additions where the decisions are open
- **G4/G3 "raw value": unfolded and trimmed.** The bytes are `Encoding.Latin1.GetString(header.RawValue)` as ordered, then
  `Header.Unfold`, which only removes CR, LF and the outer whitespace and decodes nothing. Without it the value would begin
  with a space and end in CRLF (checked on 4.18.2). Unfolded is what `Header.Value` delivered for ordinary headers, so ordinary
  values do not change. An empty Received value gives `TopReceived` null. Planner: if the literal byte-exact string is meant,
  it is one line in `GetRawValue`; `MessageKey.For` normalises either way.
- **D7 order changes for G2:** the EML is read before the known check, because the key needs it. A known mail now costs a parse.
- **Retention reads every due-candidate EML each pass** (only in Act with days > 0; UIDs already queued are skipped first).
  With a large Quarantine that is a full parse per mail per pass. Accepted for now; N2 (streaming) is already in the AP-08 list.
- **Retention requires `Parsed` true** as well as a readable EML: otherwise the key would be computed without a Received value.
- **G6:** a command of an older UIDVALIDITY does not block a UID, because it addresses another message. SetFlags commands do block,
  as "a command of the queue" says; a failed one blocks the delete until it is discarded (fails safe).
- **G7:** `ClassificationRouting.RouteWithoutSender` is an addition to D1, so the table stays in one place. Such a mail counts in
  neither `Classified` nor `ReadFailed`, but in `MovesEnqueued` when moved.
- **D7 kept against the wording of G7:** an exception from `Classify` still gives `ReadFailed`, although G7 says "only" EML and
  `Parsed` false. Inputs from the reader should not trigger it. See question F1.
- **Existing decisions:** keys from before fix.2 no longer match. No database with real decisions exists yet (case 33 has not
  run), so no migration was built. Any test copy from before fix.2 should be taken fresh.

### Probe (G14)
- `stock`, added: `{role} | no Received header`; `same Message-ID in several folders | groups/mails: share the top Received / differ in
  the top Received / some without Received`, over all folder roles, by account; `provider X | top Authentication-Results: none / no
  authserv-id / provider authserv-id / other authserv-id`, and the derived `provider X | without provider-stamped top
  Authentication-Results`. Outlook.com writes no authserv-id, so that form counts as stamped for Microsoft only.
- `Ich bin mir unsicher:` whether the Microsoft domain list in `IsProviderAuthServId` is complete. An "other authserv-id" count that looks
  large should be read with that in mind. A mail without a usable From is not in these AR counts.
- `execute <address>`: runs `CommandExecutor.ExecutePendingAsync` once. The guard counts pending commands only (Failed ones do not run)
  and fires before anything connects.
- `pass`: new constructor parameter, prints `RetentionDeletesRejected`.

### Tested / not tested (fix.2)
**Tested by me (self-control, no proof, no replacement for a tester run):**
- `dotnet build src\MailWarden.Infrastructure\MailWarden.Infrastructure.csproj -warnaserror`: 0 errors, 0 warnings.
- `_scratch\classification-probe` with `-warnaserror`: 0 errors, 0 warnings; **not started**. Its repeated-Message-ID SQL was run once in
  the self-check on artificial data.
- `_scratch\ap07c-selfcheck` (not in git, artificial data, real queue, fake provisioner): all checks passed, now including cases 34
  (first two steps), 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, and a Quarantine mail with a missing EML that is never deleted.

**Not tested:**
- `dotnet build MailWarden.sln` and every test: the tests do not compile against the new contract, which the coordinator expected.
- `MailKitFolderProvisioner` incl. SUBSCRIBE: never run; read and compiled only.
- Whether the top-most `Received` really stays the same across a MOVE at Outlook.com and Gmail. **G2 depends on it**: if it changes, a mail
  moved back by hand gets a new key and is decided again. Cases 32 and 33 show this.
- The probe in all three modes.

### Questions to the planner (fix.2)
- **F1** `ClassificationPass.cs`, `TryClassify`: keep `ReadFailed` for an exception from `Classify` (D7), or treat it like G7? Built as D7.
- **F2** `ClassificationInputReader.cs`, `GetRawValue`: is unfolded and trimmed acceptable as "raw value", or should it be byte-exact?
- **F3** Mail without any `Received` header keeps the sender-controlled key (`m:` + Message-ID + LF). A provider always stamps delivered mail,
  so this should not occur for fetched mail; `stock` now counts it. If the count is not 0, the campaign bypass stays open for these mails.

## fix.3 (decisions H1–H7 of `requirement.md`; H8 is the documenter's)
Questions F1 and F3 of fix.2 are answered by H2 and by "Decided after the measurement".

### Implementation per decision
| H | Status | Where | Note |
|---|---|---|---|
| H1 | done | `ClassificationRouting.RouteWithoutSender` | Inbox → `MoveToJunk`, Junk → `None`. Retention does not touch it, its action is not `MoveToQuarantine` |
| H2 | done | `ClassificationInputReader.cs:66-67`, `110`; `ClassificationPass.cs:341-346` | `ReadFailed` only for a missing or unreadable EML and `Parsed` false; fixed `Override` also for a failed input or `Classify` |
| H3 | done | `ClassificationInputReader.cs:85-92`, `143`; `ClassificationPass.cs:305`, `466` | `HeaderList.LoadAsync` on the file stream, no `MemoryStream`, no body. `GetTopReceived` is the one shared helper |
| H4 | done | `MailKitFolderProvisioner.cs:106-114` | role check, then SUBSCRIBE, only for a folder just created |
| H5 | done | `AuthenticationResultsTokenizer.cs`, `IsSeparator` | separators are 0x00–0x20 and 0x7F |
| H6 | done, not started | `_scratch\classification-probe\Program.cs` | not in git |
| H7 | done, not started | same | not in git |

### Readings and open points
- **H3, garbage and "no Received" look the same:** as case 49 orders, `ReadTopReceivedAsync` gives null for both, so retention cannot tell them apart.
  It treats only a missing or unreadable file as "never delete". An EML replaced by garbage is computed with a null Received: its key only
  matches a decided mail that had no Received itself. Row Message-ID and header hash must match as well, so this needs a damaged file on top of
  the no-Received case that the planner's measurement rule already covers.
- **H5 reaches the tokenizer only, as ordered.** `AuthenticationResultsParser.cs:252` (verdict) and `:276` (`domainPart.Trim()`) and
  `DomainNames.Normalize` still use .NET `Trim`, which strips 0xA0 and 0x85 at the **edges** of a value: `header.from=x.test` followed by 0xA0
  reads as `x.test`. Before H5 these bytes split the value at the same place, so this is no regression, and bytes inside a value stay. See F4.
- **H2 scope of the catch:** in `TryCreateInput` every exception except cancellation and out of memory counts as caused by the content, also an
  `IOException`, because the message is already in memory at that point.
- **H6:**
  - The marker check runs before anything else in `pass` and `execute`; `stock` does not need it, because it writes nothing.
  - "All commands are `Move`" is checked on pending commands. A `DeletePermanently` anywhere in the queue refuses, also a failed one.
  - A mail that is not stored, or whose folder has another UIDVALIDITY than the command, counts as "lacks the marker" and refuses.
  - The expected count must be greater than 0 and the marker not blank, otherwise usage is printed. Output is numbers only.
- **H7:**
  - Accounts are numbered by storing order in the database, label `account N (Provider)`. That is the only thing identifying them.
  - "Different Message-IDs sharing a top Received" compares Inbox and Junk mail with a Message-ID, per account. It prints the mails and the number of
    shared values. A mail without Message-ID is not in this count.
  - "Below all Received" means the first Authentication-Results stands after the last Received. A mail with no Received does not count here.
  - Reviewed all `Console.WriteLine` of the probe: labels come from enums and code, values are numbers; exceptions print the type name only.
- **Copy of the probe:** `K:\Projekte\Windows-Tools\MailWarden\_scratch\classification-probe` holds `Program.cs` and `ClassificationProbe.csproj`, without `bin` and `obj`.
  The references `..\..\src\...` resolve to the main folder there without a change. That copy **cannot compile until AP-07c is on the main folder's
  checkout**, because the main `src` does not hold these classes yet. It was not built there.

### Tested / not tested (fix.3)
**Tested by me (self-control, no proof, no replacement for a tester run):**
- `-warnaserror`, each 0 errors and 0 warnings: `MailWarden.Core`, `MailWarden.Infrastructure`, `MailWarden.sln`, `_scratch\classification-probe` (in the worktree).
- Run singly: Core tests with filter `AuthenticationResults|AuthStatus|SpamClassifier`, 146 of 146 passed, so H5 broke no existing parser test there.
- `_scratch\ap07c-selfcheck` (not in git, artificial data): all checks passed, including H1, cases 47 (Act and RecordOnly, from Inbox and Junk), 48, 49, 50 and 51
  (Latin-1 bytes also through the reader, ASCII space and tab as positive control).

**Not tested:**
- **The H2 path with a real exception:** no artificial input makes `CreateInput` or `Classify` throw, because the reader already filters such addresses. The branch is
  read and compiled only; a test needs a fake or a crafted fixture.
- `MailKitFolderProvisioner`, incl. the new order of SUBSCRIBE: never run.
- The probe in all three modes, incl. the checks of H6 and the counts of H7.
- The other tests. **Tests that still expect the Screener from G7 will fail until the tester has adapted them to H1.**
- Whether the top `Received` survives a MOVE: still open, `Act` stays off until case 33 shows it.

### Question to the planner (fix.3)
- **F4** `AuthenticationResultsParser.cs:252`, `:276`, `DomainNames.Normalize`: should `Trim` there also be limited to ASCII, in line with H5? Not done: outside the order, and
  it touches the domain rules of the classifier. Risk is low: the positions belong to the provider's own text.

## Note for the next patch script
The shell tool collapsed `\\` to `\` inside a heredoc: `'\\'` broke one Python script and `\\r` became a real CR in
another (repaired, scratch file only). Patch scripts with backslashes should be written with the file tool and use raw
strings.
