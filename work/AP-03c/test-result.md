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
