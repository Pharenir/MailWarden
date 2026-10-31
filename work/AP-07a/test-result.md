# AP-07a test result

## verify.1 (2026-10-31)

**Result: PASS** (stage S3, full suite via watchdog, branch `feat/ap-07a-classifier-core` after merge of `test/ap-07a-classifier-core`)

- Build `dotnet build MailWarden.sln -warnaserror`: 0 warnings, 0 errors.
- Full suite: 272 total, 272 passed, 0 failed, 0 skipped; 3.3 s (watchdog ok, soft limit not exceeded). Count is non-zero.
- Failures: none.

## New tests (commit 1955957 on test/ap-07a-classifier-core)
- Case 10a / C9: `Middle_tier_random_label_gives_fifteen_points` (label `wrtplkjo`, S = 0.619 asserted first, then `random-domain` +15, reason contains label).
- C5: `Duplicate_signal_ids_differing_only_in_case_are_rejected`.
- C2: `Parse_maps_verdict_words` row `hardfail` -> Fail.
- C8: existing tests already match the confirmed readings (contact + Unknown -> LikelyLegit; freemail Reply-To with freemail From -> +10).
- `work/AP-07a/test-plan.md` updated.

## Notes
- Test counter in `TODO.md` not reset (outside tester write scope; planner to handle). `.test-logs/durations.jsonl` updated.
- Unit tests of AP-01 storage not run (not in this solution state).
