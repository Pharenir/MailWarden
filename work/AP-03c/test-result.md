# AP-03c test result (verify.1)

Verdict: PASS (S3, full suite).

- Build `dotnet build MailWarden.sln -warnaserror`: 0 warnings, 0 errors.
- S3 via watchdog: Core.Tests 360/360, Infrastructure.Tests 549/549 (909 total), 0 failed, about 5 s. Exit 0.
- Fixes in my own tests (no implementation failures):
  1. CS0246 `CommandRunReport` in `CommandEndToEndTests.cs`: missing `using MailWarden.Infrastructure.Commands`.
  2. Two SchemaV2Tests assertions read the schema version via a data reader (`reader.GetInt32(1)`) and still expected 3 (author.0 missed them); now 4, test names without "three". 1 attempt each.
- Cases 1-23 and 25 covered by the new tests (see test-plan.md); case 24 is the planner's real-account check.
- Not covered by automated tests: S1-S3 of the MailKit class (no IMAP server); review by reading.
- Implementation failures: none. `src\` untouched.

# verify.2 (fix.2, builder `db01d2c`)

Verdict: PASS (S3). Build `-warnaserror` 0/0. Core 360/360, Infrastructure 562/562 (922 total), exit 0, about 7 s.

- Existing tests adapted (category c): the four Enqueue calls pass `expectedUidValidity`; `CommandQueueFixture` seeds Inbox UIDs 1..9 (enqueue now requires locally stored UIDs), other tests seed their own; the fake `MoveAsync` returns `CopyUidMap` (new `CopyUidMapTransform` hook); the "UID not stored locally" executor test now removes the local row after enqueue.
- New cases 26-31 in `CommandHardeningTests` (26 ParamName `expectedUidValidity`; 27 ParamName `uids`, also UID only in another folder; 28 duplicate / foreign key / zero destination / validity 0 -> nothing expunged, Trash not selected, completed; 29 validity differs from Trash SELECT, plus positive control; 31 corrupt row skipped by ListDue, listed Failed with `UnreadableRowError`, discardable).
- Case 30 only partly expressible: the best-effort `-FLAGS \Deleted` after a failed EXPUNGE (F3) lives in `MailKitCommandTransport` and cannot be recorded by the fake without re-implementing it. Pinned instead: the executor records the original failure (no message text in LastError) and sends no further write call after the failing Expunge. F3 itself needs review by reading.
- Own test bug fixed (1 attempt): `flags.Count` assertion after the Inbox preseed.
- Implementation failures: none. `src\` untouched.
