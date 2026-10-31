# AP-03b – Testplan (author.0, nicht ausgeführt)

Branch `test/ap-03b-sync-robust-fast`. Alle Pfade unter `tests\MailWarden.Infrastructure.Tests\`. Tests nur gegen
`requirement.md` geschrieben; Läufe stehen aus (Builder parallel).

## Fall → Test

| Fall | Test (Datei) |
|---|---|
| 1 | `SyncBatchTests.Missing_uids_are_fetched_in_ascending_batches_of_fifty_without_single_fetches`, `Default_options_use_batches_of_fifty`, `Configured_batch_size_is_honoured` (Sync\SyncBatchTests.cs) |
| 2 | `SyncBatchTests.Fetch_batch_size_outside_1_to_500_throws`, `Max_reconnects_outside_0_to_10_throws`, `Defaults_are_fifty_and_three_and_boundaries_are_valid`, `Positional_constructor_stays_source_compatible` |
| 3 | `SyncBatchTests.Uids_the_server_returns_nothing_for_are_skipped_without_error_and_retried_next_pass` |
| 4 | `SyncBatchTests.Eml_write_failure_for_one_message_of_a_batch_does_not_abort_the_batch` |
| 5 | `SyncBatchTests.Batch_protocol_error_falls_back_to_single_fetches_for_every_uid_of_the_batch`, `Fallback_fetches_only_the_uids_the_failed_batch_did_not_deliver` |
| 6 | `SyncBatchTests.Batch_protocol_error_with_one_failing_single_stores_the_others_and_reports_one_failure` |
| 7 | `SyncProgressTests.Reports_follow_folder_start_candidates_batches_and_folder_end_in_order`, `Failing_folder_reports_start_and_end_and_the_next_folder_reports_too`, `Progress_record_has_no_string_member` (Sync\SyncProgressTests.cs) |
| 8 | `SyncProgressTests.Null_progress_works_like_the_overload_without_progress` |
| 9 | `SyncReconnectTests.Network_failure_in_the_second_folder_reconnects_and_syncs_the_third_on_the_new_transport`, `Network_failure_during_a_batch_fails_the_folder_and_reconnects` (Sync\SyncReconnectTests.cs) |
| 10 | `SyncReconnectTests.Too_many_lost_connections_abort_the_pass_with_a_network_error_and_keep_earlier_results`, `Zero_allowed_reconnects_abort_on_the_first_lost_connection` |
| 11 | `SyncReconnectTests.Protocol_failure_with_a_connected_transport_does_not_reconnect`, `Protocol_failure_after_which_the_session_is_gone_still_reconnects` |
| 12 | `SyncReconnectTests.Authentication_failure_inside_a_folder_aborts_the_pass`, `Network_failure_while_reading_the_folder_list_aborts_without_reconnect` |
| 13 | `SyncReconnectTests.Factory_failure_on_reconnect_aborts_the_pass_with_that_exception` |
| 14 | `FolderRoleResolverTests.Google_important_folder_without_flags_is_hidden_without_role`, `Important_by_name_is_visible_for_Microsoft`, `Other_folders_named_important_stay_visible_for_Google` (Accounts\FolderRoleResolverTests.cs, `feature:accounts`; the resolver tests live in Infrastructure.Tests, not Core.Tests) |
| 15 | Positive control, no new test. Existing AP-02a tests for the folder-list result stay unchanged (`Accounts\MailKitFolderFlagsTests`, `FolderRoleResolverTests`). There is no test class named `ConnectionProbe` in `tests\` (the probe needs a real server); B6 is only covered indirectly by build + these tests. |
| 16 | `SyncBatchTests.Uid_zero_in_the_window_search_fails_that_folder_and_stores_nothing` |
| 17 | Existing sync suite; changes see below. |
| 18 | planner (real accounts) |

Additionally (assumption, not literally in the table): `SyncBatchTests.No_batch_call_is_made_when_nothing_is_missing`
(B2: no candidates, no `FetchMessagesAsync` call).

## Fake extensions (TestSupport\FakeMailServer.cs, new RecordingProgress.cs)
- `FakeMailTransport.IsConnected`, `FetchMessagesAsync` (iterates UIDs as passed, per UID: `OnFetch` hook, cancellation
  check, `FetchFailures` throw after the earlier UIDs were delivered, `NullFetchUids`/missing = skipped, then callback).
- Server: `BatchCalls` (folder + UIDs as passed), `SingleFetches`, `BatchFailures` (per folder, thrown at batch start),
  `ConnectFailuresByAttempt` (n-th connect), `NetworkFailureDropsConnection` (default true: Network failure =>
  `IsConnected` false and further calls throw Network), `AnyFailureDropsConnection`, `WindowSearchExtraUids`
  (e.g. UID 0 only in the windowed SEARCH). `Fetches` now also contains batched UIDs. Operations get
  `FetchBatch:<folder>:<count>`.
- Transport: `OpenedFolders` per instance, `DisposeCalls` as before.
- `RecordingProgress`: synchronous `IProgress<SyncProgress>` (`Progress<T>` would post asynchronously).

## Changed existing tests (category c = fake/contract demands it)
| Test | Change | Reason |
|---|---|---|
| `SyncFolderTests.Transport_contract_has_no_write_operation` | expected method list + `FetchMessagesAsync`, `get_IsConnected` | c: new interface members (read-only) |
| `SyncFolderTests.Engine_uses_only_read_calls_and_opens_each_folder_once_per_run` | regex accepts `FetchBatch:` | c: new fake operation |
| `SyncErrorTests.Network_error_aborts_the_sync_and_leaves_rows_and_files_consistent` -> `Network_error_in_a_folder_fails_only_that_folder_and_leaves_rows_and_files_consistent` | expects folder error, Junk synced, 2 transports each disposed once | contract change B4 (R9) |
| `SyncErrorTests.Network_and_authentication_failures_on_open_abort_before_the_next_folder` -> `Authentication_failures_on_open_abort_before_the_next_folder` | `Network` InlineData removed | contract change B4; Network on open now covered in `SyncReconnectTests` |
| `SyncResilienceTests.Network_error_for_one_uid_still_aborts_the_sync` -> `Network_error_for_one_uid_fails_only_that_folder` | expects folder Error instead of exception | contract change B4 |

Unchanged on purpose and expected green: cancellation test (`OnFetch` runs per UID in the batch before delivery), all
`Fetches`-based assertions (batch records requested UIDs there), `Connect_failures_propagate_unchanged`,
`AuthenticationFailedException_during_a_folder_aborts_the_sync`.

## Open questions / assumptions about the contract
1. Progress end report: tests assume a folder-end report with the final Processed/Candidates (so 60/60 appears twice
   after the last batch; likewise 3/3). Candidates-known report is assumed also for folders with 0 candidates only
   where not asserted.
2. Batch with a transient mid-batch Protocol failure: fallback fetches only undelivered UIDs singly (test 5b).
3. No `FetchMessagesAsync` call with an empty candidate list.
4. Case 10: after the 3rd lost connection (MaxReconnects 2) the engine aborts without opening a 4th transport
   (3 transports in total, each disposed once).
5. Network on `OpenFolderAsync` counts as "inside a folder" (folder error + reconnect), only connect and folder list abort.
6. FolderSyncResult/`FolderNumber` counts all synced (non-hidden) folders in list order; `Role` in progress = stored folder role.
7. ConnectionProbe: no existing unit test exists (case 15 reference can only be the folder-flag/resolver tests).
