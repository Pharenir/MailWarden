# AP-03b test result (S3, verify.1)

Commit under test: 84844a4 (feat/ap-03b-sync-robust-fast incl. test/ap-03b-sync-robust-fast). Build -warnaserror green.
Command: full suite through the watchdog (CLAUDE.md), duration about 5 s.

## Totals (final run: PASS)
| Project | Total | Passed | Failed |
|---|---|---|---|
| MailWarden.Core.Tests | 360 | 360 | 0 |
| MailWarden.Infrastructure.Tests | 439 | 439 | 0 |

## First run (70c2886): 5 failures, all test bugs (category: test error, not implementation)
SyncReconnectTests: Factory_failure_on_reconnect..., Network_failure_in_the_second_folder..., Zero_allowed_reconnects...,
Authentication_failure_inside_a_folder..., Too_many_lost_connections...
Cause: my tests assumed folder sync order = server list order; the engine syncs ordered by full name
(INBOX, Junk, Sent). Fix (attempt 1 of 3) on test branch: folder roles renamed Middle=Junk / Last=Sent, case 10 folders
INBOX/Junk/Sent/Trash/Zeta. Merged with --no-ff, rerun green. No implementation failures.

## Notes
- Case 18 (real accounts) remains with the planner. Case 15: no ConnectionProbe unit test exists.
- `.test-logs\durations.jsonl` not written (worktree has no history; S3 counter reset is up to the coordinator).
