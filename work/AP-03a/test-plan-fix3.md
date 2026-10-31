# AP-03a fix.3 – test plan (author.0, tests written, not yet run)

Contract: `requirement.md`, "Decisions fix.3". Written against the contract; no builder code read.

## New cases
| Case | Test | File |
|---|---|---|
| 35 | `Incomplete_search_result_is_completed_by_the_next_run` | tests\MailWarden.Infrastructure.Tests\Sync\SyncMissedUidTests.cs |
| 36 | `Null_fetch_results_are_completed_by_the_next_run` | same |
| 37 | `Window_floor_is_set_below_the_first_window_uid_even_when_that_uid_fails` | same |
| 38 | `Window_without_any_message_sets_the_floor_to_the_highest_server_uid`, `Empty_folder_with_a_window_has_floor_zero` (empty folder -> 0) | same |
| 39 | `V2_database_migrates_to_v3_renames_the_column_and_resets_the_value`, `Reopening_a_v3_file_keeps_the_saved_floor`, `Next_sync_after_the_migration_fetches_every_missing_uid` | tests\...\Storage\SchemaV3Tests.cs (tags feature:storage + feature:sync) |
| 40 | `Second_run_without_changes_makes_no_fetch_calls` | SyncMissedUidTests.cs |

Test helper: `FakeMailFolder.SearchHiddenUids` (SEARCH omits these UIDs; new, in TestSupport\FakeMailServer.cs). Null FETCH on the first run only uses the existing `NullFetchUids` (cleared before the second run). TestSupport change triggers FULL per test-map.

## Changed tests (category c: contract change fix.3, not a defect)
| Case | Test | Change |
|---|---|---|
| 4 | `SyncMessageTests.First_sync_stores_three_messages_...` | state `SkippedUpToUid` 0 instead of highest UID |
| 5 | `SyncMessageTests.Initial_window_limits_...` | floor = UID of the old message (min window UID - 1) |
| 6 | `SyncMessageTests.Second_sync_fetches_only_the_new_uid` | state stays 0 |
| 12 | `SyncMessageTests.Fetch_returning_null_skips_that_uid_without_error` | extended: second run fetches and stores exactly that UID |
| 16/17/others | `SyncErrorTests` (3 asserts), `SyncChangeTests` (UIDVALIDITY reset), `SyncReportTests` (2), `SyncMessageTests` multiple folders | `HighestSyncedUid` renamed `SkippedUpToUid`, expected value 0 (InitialSyncDays 0, no high-water mark any more) |
| 29 | `SyncResilienceTests` (all state asserts incl. case 30 EML failure); two tests renamed (`..._keeps_the_skipped_floor_at_zero`, `Several_failures_are_counted_and_do_not_change_the_state`) | F1 state rule removed: state stays 0, retry fetches only the failed UID |
| 22 | `SchemaV2Tests` (file name kept) | version 3 asserted (4 places, `Current_schema_version_is_three`, renames of two tests), column `skipped_up_to_uid`; store tests renamed property |
| store | `SqliteFolderSyncStateStoreTests`, `SchemaV2Tests` | property rename only |

## Notes
- test-map.json maps by directory; new files fall under existing patterns, no change.
- Case 39 seeds a v2 file from SQL written in the test (tables per AP-03a schema; `last_synced_at` as ISO "O" text). If the real v2 column types differ, the seed needs adjusting.
