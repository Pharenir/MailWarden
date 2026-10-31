# AP-03a fix.3 - test result (verify.1)

- Branch fix/ap-03a-missed-uids after merge of test/ap-03a-missed-uids (91b9fa8: added missing usings in SyncMissedUidTests).
- Build `dotnet build MailWarden.sln -warnaserror`: 0 warnings, 0 errors.
- Stage S3 (full suite via watchdog, exit 0, 4.2 s, no hang): 360 + 401 tests, all passed, 0 failed, 0 skipped
  (project order in log: first project 360, second 401; the sum 761 includes the new fix.3 tests).
- New tests ran: 6 in SyncMissedUidTests (cases 35-38, 40), 3 in SchemaV3Tests (case 39).
- Failures: none. Verdict: PASS.
- Not tested: real accounts (planner re-runs SyncProbe and sync-check).
