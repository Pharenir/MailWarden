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

## verify.2 (2026-10-31)
- Result: PASS (S3, full suite via watchdog, exit 0)
- Build `dotnet build MailWarden.sln -warnaserror`: 0 warnings, 0 errors
- Tests: 290 total, 290 passed, 0 failed (verify.1: 272)
- Merge test/ap-07a-classifier-core into feat: clean, no conflict
- Test branch commits: c928d32 (cases 3a/21a/22a/28a/30a), db1ad80 (C17/C19)
- New/changed tests (db1ad80): `Parse_spf_domain_is_taken_after_the_last_at_sign` +2 rows (C17 `x@attacker.test"@victim.test"` -> attacker.test; C19 `"Bounce@Example.TEST"` -> example.test), new `Parse_dkim_domain_from_quoted_header_i` (C19 -> victim.test)
- Failures: none
- Test counter in TODO.md not reset (outside tester scope; planner to do). durations.jsonl updated.

## verify.3 (2026-10-31)

Ergebnis: **PASS**
- Merge `test/ap-07a-classifier-core` (cb200d5) in `feat/ap-07a-classifier-core`: sauber, kein Konflikt (+3 Tests 3b/3c/22b, test-plan.md).
- Build `dotnet build MailWarden.sln -warnaserror`: 0 Warnungen, 0 Fehler.
- Stufe S3 (volle Suite, Wächter): 296 gesamt, 296 bestanden, 0 fehlgeschlagen, 0 übersprungen (verify.2: 290, +6). Wächter: ok, 3,1 s.
- Fehlschläge: keine.

## verify.4 (2026-10-31)
- Merge test/ap-07a-classifier-core (4c0717b, 3d/3e) in feat-Branch: sauber, Build -warnaserror 0/0.
- S3 (volle Suite, Waechter): 299/299 bestanden (verify.3: 296, +3 neue Tests), 3,1 s, Ergebnis ok.
- Keine Fehlschlaege.

## verify.5 – integration with AP-01 (2026-10-31)

Ergebnis: **PASS**
- Stand: Merge-Commit 7a10099 (feat/ap-01-skeleton-storage in feat/ap-07a-classifier-core); Solution mit Core.Tests und Infrastructure.Tests.
- Stufe S3 (volle Suite, Waechter, Auswahl voll): 399 gesamt, 399 bestanden, 0 fehlgeschlagen, 0 uebersprungen.
  - MailWarden.Core.Tests: 299/299 (1,05 s)
  - MailWarden.Infrastructure.Tests: 100/100 (2,74 s)
- Waechter: ok, Exit 0, 6,2 s gesamt, weiche Grenze nicht ueberschritten, kein Abbruch.
- Fehlschlaege: keine. durations.jsonl fortgeschrieben.
