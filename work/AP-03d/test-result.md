# AP-03d test result

Verdict: PASS (S3, 601 of 601 green, 4.4 s, build -warnaserror green, no src change by the tester).

- First S3 run: 586/601. 15 failures were my own test bug: `Timeout` on synchronous tests (DataFolderLockTests, MailboxServiceOptionsTests)
  is unsupported by xUnit. Fixed by removing the Timeout there (attempt 1). A compile error (xUnit2012) was fixed before.
- Stability: Background tests re-run 3x via class filter, all green (see numbers in the coordinator reply).
- Cases 1-18 covered (see test-plan.md); case 19 open (planner, real accounts).
- Implementation failures: none.
- Not asserted (contract silent): watcher disposal after NotSupported; Stopped on idle accounts at StopAsync.

## verify.2 (fix.2, builder 7107bb0 + tests 524388b)
Verdict: PASS. Build -warnaserror green. S3 (watchdog): Core 360/360 and Infrastructure 604/604, 4.1 s.
Background tests re-run 3x via class filter: 42/42 each time, no flakiness. Cases 20 and 21 green. No implementation failures.
