# AP-03c test plan (tester, step author.0)

Status: tests written against `requirement.md`, **not built, not run** (implementation was built in parallel; no build was possible without it).
Branch `test/ap-03c-command-queue`. All tests: `[Trait("Category","feature:sync")]`, storage-related also `feature:storage`, `[Fact/Theory(Timeout = TestTimeouts.PerTestMilliseconds)]`.

## Case -> test
| # | Test (file) |
|---|---|
| 1 | `CommandEnqueueTests.SetFlags_is_stored_pending_with_ascending_uids_and_changes_nothing_else` (+ duplicates, 1000 boundary, allowed flags, Move/MoveToTrash/DeletePermanently stored fields) |
| 2 | `CommandEnqueueTests`: Empty_uid_list / Uid_zero / More_than_1000 (all four kinds), Unknown_source_folder, Never_synced_folder (UidValidity 0), SetFlags overlap / none / Deleted / undefined bit, Move same / unknown / other account, MoveToTrash from Trash / without Trash (InvalidOperation), EmptyFolder on Inbox/Archive/None |
| 3 | `CommandEnqueueTests.EmptyFolder_takes_exactly_the_uids_stored_now...` (Junk and Trash), `EmptyFolder_returns_null...`; executor side: `CommandExecutorTests.EmptyFolder_never_touches_a_message_that_arrived_on_the_server_after_enqueue` |
| 4 | `SchemaV4Tests` (fresh DB, v3 file with data, reopen, folder cascade as source and target, account cascade) |
| 5 | `CommandEnqueueTests`: List_returns_..._oldest_first, Discard_..., ListDue_... (null / past / exactly now / future / Failed), RecordFailure_*, Complete_*, ListDue other account |
| 6 | `CommandExecutorTests.SetFlags_*` (add; add+remove order; only locally stored UIDs) |
| 7 | `CommandExecutorTests.Move_moves_exactly_the_uids...` |
| 8 | `CommandExecutorTests.MoveToTrash_moves_to_the_trash_folder...` |
| 9 | `CommandExecutorTests.DeletePermanently_in_junk_or_trash...` (Theory Junk, Trash) |
| 10 | `CommandExecutorTests.DeletePermanently_elsewhere_moves_to_trash_then_expunges_only_the_mapped_trash_uids` (Theory Inbox, Archive, None; a pre-existing Trash message must survive) |
| 11 | `..._with_an_empty_copyuid_map...`, `..._with_a_partial_copyuid_map...`, `..._without_a_trash_folder_fails_permanently...` |
| 12 | `CommandExecutorTests.Uid_validity_mismatch_makes_no_write_call...` (Theory SetFlags, Move, MoveToTrash, DeleteJunk, DeleteInbox) + `..._does_not_stop_the_next_command` |
| 13 | `CommandExecutorTests.No_command_means_no_connection`, `Only_not_yet_due_or_failed_commands_mean_no_connection` (+ Max_attempts range theory for CommandOptions 1..20) |
| 14 | `CommandFailureTests.Protocol_or_other_failure_records_the_attempt_and_the_next_command_still_runs` |
| 15 | `CommandFailureTests.The_attempt_that_reaches_max_attempts_fails_the_command_for_good`, `With_max_attempts_one...` |
| 16 | `CommandFailureTests.Backoff_doubles_from_one_minute_and_is_capped_at_sixty_minutes` (1,2,4,8,16,32,60,60,60; not due 1 s before) |
| 17 | `CommandFailureTests.Network_failure_records_the_attempt_ends_the_run...`, `Connect_failure_with_network_error...` |
| 18 | `CommandFailureTests.Authentication_failure_and_cancellation_propagate...` (Theory: MailTransportException(Auth), AuthenticationFailedException, OperationCanceledException), `..._while_connecting...`, `A_cancelled_token...` |
| 19 | `CommandFailureTests.Storage_failure_in_the_local_delete...`, `..._local_flag_update...`, `A_failing_eml_delete_is_skipped...` |
| 20 | `CommandFailureTests.Commands_run_oldest_first_over_one_session` |
| 21 | `CommandFailureTests.Last_error_never_contains_the_exception_message` (5 variants incl. inner exception and Network), `Last_error_of_a_permanent_failure...` |
| 22 | `CommandEndToEndTests.A_sync_pass_never_opens_a_write_session...`, `The_read_only_transport_still_has_only_read_members`, `The_write_transport_is_a_separate_interface_without_a_plain_expunge_or_close`, `The_sync_engine_does_not_depend_on_the_command_queue` (+ existing AP-03a case 20 tests unchanged) |
| 23 | `CommandEndToEndTests`: moved message / MoveToTrash / DeletePermanently / EmptyFolder(Junk) / SetFlags -> executor -> real sync pass over the fake server (which really moves and expunges) |
| 24 | planner (real accounts), not covered |

Extra safety tests (E4/E5, not numbered): local store unchanged after failed AddFlags/Move/Expunge; no Expunge when Move fails; no Expunge when Select(Trash) fails and the retry (UIDs gone, empty map) deletes nothing more.

## Safety assertions
- Delete tests assert the exact call list (`Select`, `Expunge:<folder>:<uids>`), server contents afterwards (messages not in the command, late arrivals and pre-existing Trash messages survive) and `AssertExpungesOnly`.
- Case 12 asserts every recorded call is a `Select` (no write call).
- The fake `IMailCommandTransport` has no plain EXPUNGE/CLOSE by interface; reflection test pins the interface members.

## Support code added (TestSupport)
- `FakeCommandTransport.cs`: `CommandCall`, `FakeMailServer` partial (command log, `CommandFailure` hook, `OmitCopyUid`, `OmitCopyUidFor`), `FakeMailTransportFactory.ConnectForCommandsAsync` (counted), `FakeCommandTransport` (applies flags/move/expunge to the fake server, disposal tracked).
- `CommandHarness.cs` (`CommandHarness`, `CommandQueueFixture`), `FaultyMessageStore.cs`.
- Time: the existing `FixedTimeProvider` (settable `Now`), no FakeTimeProvider package available.

## Changed existing tests (category c: contract change, not a behaviour regression)
- `SchemaV2Tests` (4 assertions + test name `Current_schema_version_is_four`) and `SchemaV3Tests` (2 assertions): schema version 3 -> 4 (C2).
- `FakeMailServer.cs`: `FakeMailServer` and `FakeMailTransportFactory` made `partial`; new interface member `ConnectForCommandsAsync` lives in the new file (C3).
- `FaultyEmlStore`: additive `FailDeleteWhen` (default off).
- `tests/test-map.json`: entries for `src/MailWarden.{Core,Infrastructure}/Commands/**`.

## Contract questions / assumptions
1. `LastError` for exceptions: tests only require non-empty and no message text; they do not pin "type name" vs full name.
2. Network failure: tests require `Attempts` +1 and `NextAttemptAt` set (not the exact value). A command exhausting `MaxAttempts` by Network failure is not tested.
3. E2 "missing source/target folder in the local store" is not testable: FK cascade removes the command with the folder (tested in SchemaV4Tests). Please confirm the clause is only defensive.
4. Order of UIDs passed to `ExpungeAsync` after the COPYUID mapping is compared as a set (sorted); `AddFlags` before `RemoveFlags` is pinned.
5. DeletePermanently without Trash folder is tested as permanent failure (report Failed 1), nothing sent that moves/expunges.
6. S1/S2/S3 of the MailKit implementation (no EXPUNGE/CLOSE, UIDPLUS/MOVE capability checks, error mapping) cannot be tested without an IMAP server; no test covers the MailKit class. Reviewer/security review must check it from the code.
7. `CommandOptions(0)` / `(21)` may throw in the record or the executor constructor; the test accepts either.
8. Every executed command is assumed to `SelectFolderAsync` exactly once per non-trash step (case 20 compares the Select sequence).
