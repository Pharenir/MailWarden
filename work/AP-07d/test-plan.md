# AP-07d test plan (author.0, written from the contract only; not run)

| Case | Tests (file) |
|---|---|
| 1 | `ClassifyingSyncCycleTests`: Normal_cycle_runs_sync_then_classify_then_summary_and_returns_the_sync_report (smoke), Progress_and_cancellation_token_are_handed_to_sync, Summary_carries_the_counts_of_the_pass_report |
| 2 | `ClassifyingSyncCycleTests`: Trigger_runs_once_only_when_moves_or_retention_deletes_were_enqueued (6 rows: 2/0, 0/1, 0/0, rejected 3, held 5, 2+1) |
| 3 | `ClassifyingSyncCycleTests`: Sync_failure_skips_classify_..., Asynchronously_failing_sync_... |
| 4 | `ClassifyingSyncCycleTests`: Classify_failure_writes_one_line_with_the_type_name_..., Asynchronously_failing_classify_..., Cancellation_during_classify_propagates_without_a_log_line |
| 5 | `ClassifyingSyncCycleTests`: Summary_handler_failure_is_logged_once_... |
| 6 | `ClassifyingSyncCycleTests`: Failed_evaluations_and_held_moves_write_one_line_with_the_counts_only (counts 7 and 13 instead of 2 and 3, so the digits are not found by accident), Either_count_alone_writes_a_line, Clean_pass_writes_no_line, Read_failed_count_is_part_of_the_pass_line, Held_moves_and_failing_summary_write_two_lines_and_never_trigger |
| 7 | Core: `ClassificationPolicyBrakeTests` (default 100, range 1..1000, equality, enum value 8). Infrastructure: `ClassificationPolicyMaxMovesFileTests` (valid 1/2/100/250/1000; 0, -1, 1001, huge; wrong types string, fractional, null, bool, array, object; duplicate; wrong case; missing file/key) |
| 8 | `ClassificationPassBrakeTests`: Exactly_the_limit_... (limit 1 and 3, positive control), One_more_than_the_limit_... (smoke; queue via `Queue.ListAsync`, 4x BrakeHeld, planned action, marker), Moves_to_different_targets_... (sum over targets, plus control at the limit), Mails_without_a_planned_move_do_not_count |
| 9 | `ClassificationPassBrakeTests`: Inbox_within_the_limit_moves_while_junk_above_it_is_held, Inbox_above_the_limit_is_held_while_junk_within_it_moves |
| 10 | `ClassificationPassBrakeTests`: Record_only_with_200_mails_..., Suspended_act_holds_nothing |
| 11 | `ClassificationPassBrakeTests`: Same_mail_again_after_a_hold_is_skipped_as_known, Held_mails_are_not_decided_again_even_with_a_higher_limit |
| 12 | `MailWardenRuntimeClassificationFilesTests`: Missing_files_open_with_defaults (smoke), Valid_files_open_..., Invalid_file_fails_the_open_... (4 rows, lock released), Second_open_works_after_the_invalid_file_was_removed (2 rows), File_that_becomes_invalid_after_start_... |
| 13 | NOT WRITTEN, waiting for the seam (question 1) |
| 14 | `ClassificationBannerTests`: Failed_evaluations_show_a_banner_with_the_count (smoke), Banner_follows_the_last_summary_..., Counts_of_several_accounts_are_added, Newer_summary_of_the_same_account_replaces_..., Summary_of_an_unknown_account_is_ignored, Read_failed_alone_shows_no_banner_... |
| 15 | `ClassificationBannerTests`: Suspended_act_shows_a_fixed_waiting_text_..., Waiting_text_and_problem_text_are_different_texts |
| 16 | `ClassificationBannerTests`: Summary_event_is_marshalled_through_the_dispatcher, Exception_in_the_summary_handler_... (2 variants) |
| D2 retention | `ClassificationPassBrakeTests`: Retention_deletes_are_not_held_by_the_brake |

Support changes: `TestSupport/RecordingCriticalLog.cs` (new), `ClassificationHarness.ActWithLimit` (new), `FakeMailboxController` (event `ClassificationPassCompleted`, `RaiseClassification`, `RaiseClassificationOnPoolAsync`), `tests/test-map.json` (Composition entries also select `feature:classifier`). The existing `ClassificationHarness` policies (`new(mode, 0)`) stay valid through the default third parameter; no existing test constructs `ClassificationPassReport`.

## Readings I chose where the contract is silent (please confirm or correct)
- The brake counts planned moves per source folder over all targets together (Inbox: Screener + Quarantine moves add up). Test: Moves_to_different_targets_*.
- Suspended `Act` (empty address book) is effective `RecordOnly`: no `BrakeHeld`, outcome `Recorded`.
- A held entry keeps `Mode = Act` and `Tier`; `MovesEnqueued` stays 0 for held mails.
- Banner: the text contains the summed `EvaluationFailed` and the summed `BrakeHeld` as decimal numbers (the tests find "7", "4", "12" in the text); it contains neither the account name nor `ReadFailed`; the waiting text has no digit. If the real text names a count in another form, the assertions on digits are the part to adjust.
- `ClassifyingSyncCycle` writes the pass line for `EvaluationFailed`/`BrakeHeld` only; `ReadFailed` is tested as "count appears in that line" (not as a trigger for a line).
- Held moves plus failing summary handler give two log lines (one per cause).

## Questions to the planner
1. Case 13: which seam? (Needed also for "not raised after dispose" of the runtime event, D3; no test for that exists yet.)
2. D1.5 / D4: should `ReadFailed > 0` alone write a log line? The contract text ("only its count in the log line of the pass when above 0") can be read both ways; the tests assume no line for `ReadFailed` alone is not asserted either way (only "count is in the line when a line exists").
3. D1.3: is the order trigger vs. summary fixed? Not asserted.
4. Does a held mail count in `ClassificationPassReport.Classified`? Not asserted for the held case.
