# AP-03a – Testplan (author.0, written against `requirement.md`, without reading `src\`)

Project: `tests\MailWarden.Infrastructure.Tests\`. Traits: `feature:sync` (Sync, Mime), `feature:storage` (Store/Schema),
`smoke` on three tests. Fake: `TestSupport\FakeMailServer.cs` (server state + `FakeMailTransport` + `FakeMailTransportFactory`),
`TestSupport\SyncHarness.cs` (engine + real in-memory SQLite + real `FileEmlStore` in a temp dir + `FixedTimeProvider`),
`TestSupport\SyncTestEml.cs` (artificial EML strings; no fixture files).

| Case | Test method(s) |
|---|---|
| 1 | `SyncFolderTests.First_sync_stores_visible_folders_with_roles_and_skips_hidden_ones`, `Hidden_folders_are_never_opened_on_the_server`, `Role_comes_from_the_folder_role_resolver_of_the_account_provider` |
| 2 | `SyncFolderTests.Folder_that_vanished_on_the_server_is_removed_with_rows_files_and_state` |
| 3 | `SyncFolderTests.Folder_that_becomes_hidden_is_removed_like_a_vanished_one`, `Folder_that_gets_the_All_flag_is_removed` |
| 4 | `SyncMessageTests.First_sync_stores_three_messages_with_files_search_entries_and_state` (smoke), `Row_fields_are_mapped_from_headers_fetch_result_and_file_path`, `Message_with_attachment_sets_has_attachments`, `From_uses_the_first_mailbox_and_missing_headers_map_to_empty_strings`, `Preview_is_cut_to_200_chars_while_the_search_index_holds_the_full_body`, `Multiple_folders_keep_their_own_uid_sequences`, `Non_ascii_content_is_stored_byte_identical_and_searchable`; `SyncFolderTests.First_sync_stores_the_server_uid_validity` |
| 5 | `SyncMessageTests.Initial_window_limits_the_first_sync_and_older_mail_is_never_fetched_later`, `Initial_window_search_uses_today_minus_the_configured_days`, `Message_on_the_first_day_of_the_window_is_included`, `Zero_initial_days_syncs_even_very_old_mail` |
| 6 | `SyncMessageTests.Second_sync_fetches_only_the_new_uid`, `Second_sync_without_changes_fetches_nothing_and_reports_zero` |
| 7 | `SyncChangeTests.Changed_server_flags_update_the_rows_and_leave_the_search_index_alone` (Theory condstore true/false), `One_seen_flag_added_reports_exactly_one_update` |
| 8 | `SyncChangeTests.With_condstore_the_stored_mod_seq_is_passed_to_the_flag_fetch` |
| 9 | `SyncChangeTests.Without_condstore_all_flags_are_fetched`, `Stored_mod_seq_zero_forces_a_full_flag_fetch_even_if_the_server_has_one_now`, `Server_mod_seq_zero_forces_a_full_flag_fetch_even_if_one_is_stored` |
| 10 | `SyncChangeTests.Message_deleted_on_the_server_is_removed_with_its_file_and_search_entry`, `Deletion_and_flag_change_in_one_run_are_counted_separately`, `Expunged_message_that_was_never_stored_is_not_counted_as_deleted` |
| 11 | `SyncChangeTests.Changed_uid_validity_wipes_the_folder_and_fetches_everything_again`, `Sync_after_the_reset_does_not_reset_again`, `Folder_stored_before_any_sync_with_validity_zero_is_not_reset`, `Reset_of_one_folder_keeps_the_other_folder_intact` |
| 12 | `SyncMessageTests.Fetch_returning_null_skips_that_uid_without_error` |
| 13 | `SyncMessageTests.Message_without_date_header_uses_the_internal_date` |
| 14 | `SyncMessageTests.Html_only_message_yields_plain_text_without_tags_or_script_content` |
| 15 | `SyncMessageTests.Unparseable_bytes_are_stored_with_empty_header_fields_and_the_file_is_kept`, `Binary_noise_is_stored_and_does_not_abort_the_folder` |
| 16 | `SyncErrorTests.Protocol_error_in_one_folder_is_reported_and_the_next_folder_is_still_synced`, `Failure_in_the_middle_of_a_folder_keeps_stored_rows_and_old_state_and_resumes_next_time`, `Unexpected_exception_in_a_folder_is_recorded_with_type_and_message`, `Transport_is_disposed_exactly_once_after_success_and_after_a_folder_error` |
| 17 | `SyncErrorTests.Network_error_aborts_the_sync_and_leaves_rows_and_files_consistent`, `Network_and_authentication_failures_on_open_abort_before_the_next_folder` (Theory), `Connect_failures_propagate_unchanged`, `AuthenticationFailedException_during_a_folder_aborts_the_sync` |
| 18 | `SyncErrorTests.Cancellation_after_two_of_four_messages_then_a_new_run_completes_without_duplicates`, `Already_cancelled_token_throws_OperationCanceledException_and_stores_nothing`, `OperationCanceledException_from_the_transport_propagates` |
| 19 | `SyncFolderTests.Inbox_is_synced_first_then_junk_then_the_rest_by_name_ignoring_case`, `Report_lists_the_role_of_every_synced_folder` |
| 20 | `SyncFolderTests.Transport_contract_has_no_write_operation` (reflection on `IMailTransport`), `Engine_uses_only_read_calls_and_opens_each_folder_once_per_run`, `Sync_leaves_the_server_untouched_and_does_not_set_seen` |
| 21 | `SyncFolderTests.Unknown_account_id_throws_ArgumentException` |
| 22 | `SchemaV2Tests.V1_database_migrates_in_place_and_keeps_its_rows`, `Migrated_database_stores_sync_state_for_existing_folders_and_cascades`, `Reopening_a_migrated_file_keeps_version_two_and_the_data`, `Seeded_file_is_version_one_before_the_migration`; also `Current_schema_version_is_two`, `Fresh_database_has_version_two_and_the_sync_state_table` (smoke), `Sync_state_table_has_the_contract_columns` |
| 23 | `SqliteFolderSyncStateStoreTests` (10 tests: save/get, null timestamp, overwrite, unknown get/save, delete, independence, cascade folder, cascade account, large values) |
| 24 | `SqliteMessageStoreSyncApiTests` (8 tests) |
| 25 | `MimeBodyTextTests` (14 test cases incl. Theory) |
| 26 | `SyncFolderTests.Negative_initial_sync_days_throw_ArgumentOutOfRangeException`, `Zero_and_default_initial_sync_days_are_valid` |
| 27 | `SyncReportTests.Full_pass_over_two_folders_with_mixed_changes_reports_exactly_what_changed`, `Run_without_changes_reports_only_zeros` |
| 28 | manual (Dennis, SyncProbe) – not automated, requires real accounts |

Smoke (`Category=smoke`): `SyncMessageTests.First_sync_stores_three_messages_...`, `SyncReportTests.Smoke_first_sync_...`,
`SchemaV2Tests.Fresh_database_has_version_two_and_the_sync_state_table`.

## Not automated / gaps
- Case 28 and `MailKitTransportFactory` (real servers, exception mapping, flag mapping, EXAMINE, CONDSTORE announcement): manual per contract.
- Rule R11 (crash between EML write and row): no crash injection available; covered indirectly by "every row has its file" after network error and cancellation (cases 17, 18).
- Rule R9 for exceptions outside a folder (e.g. `Protocol` thrown by `ListFoldersAsync`): not specified in the requirement, no test.
- `UpdateFlagsAsync` on an unknown folder: behaviour not specified, not tested.
- `FolderSyncState.HighestModSeq` above `long.MaxValue`: SQLite stores signed 64 bit; only values up to `long.MaxValue` are tested.

## v1 database for case 22
Built without product code: `SchemaV2Tests.SchemaV1Sql` is a re-derivation of schema v1 from `work\AP-01\requirement.md` (including D3/D4
and an FTS5 table with the four columns). Differences to the real migration SQL (index names, FTS options) are not detectable from the
requirement; the test only exercises the v1 -> v2 step (new table, old rows incl. FTS entry intact, schema_version = 2 with one row).
If verify shows that the real v1 differs, adjust the DDL in the test (not the product).

## Assumptions
- A1 `MessageId` = MimeKit `MessageId` without angle brackets.
- A2 The offset of the Date header is not asserted, only the instant.
- A3 IMAP SINCE is day-granular (the fake compares `InternalDate.Date >= since.Date`); `TimeProvider.LocalTimeZone` is UTC in tests.
- A4 A folder aborts at the failing UID; later UIDs are fetched in the next run (case 16 resume test).
- A5 `SyncReport.Folders` lists only synced folders (not removed or hidden ones), in sync order.
- A6 Folder upsert/removal happens before any folder is synced (case 17 theory expects the not-yet-synced folder to be stored or not irrelevant; assertions avoid depending on it).
- A7 Cut and collapse order of `MimeBodyText` (collapse, trim, then cut) is not asserted beyond "length <= maxChars".
