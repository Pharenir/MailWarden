# AP-01 test plan

Project: `tests\MailWarden.Infrastructure.Tests` (net9.0-windows, xUnit v2 2.9.3, Microsoft.NET.Test.Sdk 18.11.0,
xunit.runner.visualstudio 4.0.1). All classes carry `[Trait("Category", "feature:storage")]`, the smoke class
additionally `[Trait("Category", "smoke")]`. Real SQLite (in-memory / temp file), no mocks. Written before the
implementation, not yet compiled or run.

## Case to test mapping

| Case | Test(s) |
|---|---|
| 1 | `SqliteDatabaseTests.Fresh_database_has_current_version_and_all_tables`, `Fresh_database_has_exactly_one_schema_version_row` |
| 2 | `SqliteDatabaseTests.Second_initialisation_of_existing_file_keeps_version_and_data` |
| 3 | `SqliteDatabaseTests.Newer_schema_version_throws_SchemaVersionException_with_both_numbers` |
| 4 | `SqliteDatabaseTests.Connection_before_initialisation_throws_InvalidOperationException` |
| 5 | `SqliteAccountStoreTests.Upsert_then_get_returns_equal_record`, `Upsert_with_same_id_updates_display_name_without_adding_a_row`, `List_is_ordered_by_display_name` |
| 6 | `SqliteAccountStoreTests.Upsert_with_same_email_and_different_id_throws_StorageException` (+ `..._in_different_case_...`) |
| 7 | `SqliteFolderStoreTests.Upsert_returns_copy_with_positive_id`, `Upsert_with_same_account_and_name_keeps_id_and_updates_values`, `Same_name_under_second_account_gets_a_different_id` |
| 8 | `SqliteFolderStoreTests.Upsert_for_unknown_account_throws_StorageException` |
| 9 | `SqliteMessageStoreTests.Upsert_returns_positive_id_and_get_returns_equal_record`, `Date_keeps_its_original_offset_and_message_id_stays_null` (positive control: asserts `Date.Offset`), `All_fields_round_trip_...` |
| 10 | `SqliteMessageStoreTests.Upsert_with_same_key_updates_values_and_search_index` (+ body-text keep/replace tests) |
| 11 | `SqliteMessageStoreTests.Upsert_for_unknown_folder_throws_StorageException` |
| 12 | `SqliteMessageStoreTests.List_by_role_returns_inbox_messages_of_all_accounts_newest_first_with_id_tie_break` |
| 13 | `SqliteMessageStoreTests.List_by_role_and_account_returns_only_that_accounts_messages` |
| 14 | `SqliteMessageStoreTests.List_folder_id_wins_over_role` |
| 15 | `SqliteMessageStoreTests.List_applies_limit_and_offset_on_the_newest_first_order` |
| 16 | `SqliteMessageStoreTests.List_with_default_query_returns_all_messages_newest_first` |
| 17 | `SqliteMessageSearchTests.Search_finds_message_by_subject_word_case_insensitively`, `Search_finds_message_by_word_that_only_occurs_in_body_text` (positive control), `Search_finds_message_by_from_address_and_from_name`, `Search_for_unknown_word_returns_empty` |
| 18 | `SqliteMessageSearchTests.Search_with_blank_text_throws_ArgumentException` (theory: "", " ", "  ", tab/newline) |
| 19 | `SqliteMessageSearchTests.Search_with_fts_syntax_characters_does_not_throw` (theory, 9 inputs incl. `"foo" OR bar*` and `(`), `Search_treats_or_operator_as_plain_word` |
| 20 | `SqliteMessageStoreTests.Delete_removes_message_and_its_search_entries`, `Delete_of_unknown_id_is_a_no_op` |
| 21 | `SqliteMessageStoreTests.Delete_by_uids_removes_exactly_the_given_uids_of_that_folder`, `Delete_by_uids_with_empty_collection_is_a_no_op` |
| 22 | `SqliteAccountStoreTests.Delete_cascades_to_folders_and_messages_and_leaves_other_accounts_untouched` |
| 23 | `SqliteFolderStoreTests.Delete_removes_its_messages_and_keeps_the_account` |
| 24 | `FileEmlStoreTests.Relative_path_uses_lower_case_guid_and_forward_slashes` |
| 25 | `FileEmlStoreTests.Write_creates_directories_and_content_is_read_back_identically`, `Second_write_overwrites_with_shorter_content_and_leaves_no_tmp_file`, `Empty_content_round_trips` |
| 26 | `FileEmlStoreTests.Open_read_of_missing_path_throws_EmlNotFoundException_with_relative_path`, `Exists_is_false_and_delete_is_a_no_op_for_missing_path` |
| 27 | `DataPathsTests.Resolve_rejects_parent_segment_at_start`, `Resolve_rejects_rooted_path`, `Resolve_rejects_parent_segment_in_the_middle`, `Resolve_maps_relative_path_below_mail_root_with_os_separators` |
| 28 | `DataPathsTests.Properties_are_derived_from_the_root` |
| 29 | `StorageSmokeTests.File_database_and_eml_store_work_end_to_end_and_release_all_handles` |
| 30 | manual (Dennis) |

Extra tests beyond the table (from the contract text): folder role stored explicitly and not derived from the name
(AP-00 finding), foreign keys pragma on, FolderStore list ordering/get/delete no-op, search limit and ordering,
bodyText null keeps / non-null replaces indexed text, account delete of unknown id.

## Per-test timeout (30 s)

xUnit v2 (2.9.3) with `[Fact(Timeout = TestTimeouts.PerTestMilliseconds)]` / `[Theory(Timeout = ...)]`,
constant `TestSupport\TestTimeouts.cs` = 30000. Limits: v2 timeouts only work for async tests that actually yield; a
purely synchronous hang (SQLite calls in Microsoft.Data.Sqlite are synchronous internally) cannot be interrupted, so
the watchdog idle limit (90 s) is the backstop. Pure synchronous tests (`DataPathsTests`, `Relative_path_...`) use a
plain `[Fact]`. xUnit v3 was not chosen: it needs an Exe output type and the MTP runner, while the project's
`dotnet test ... --logger "console;verbosity=normal"` setup targets VSTest. Parallelism: xUnit default (one collection
per class); all tests use isolated in-memory databases or unique temp directories, so parallel runs are safe.

## Requirement ambiguities / notes

- Case 5, 6: row 5 has the expected text in the "Case" column; read as given. Case 6 "StorageException": tests use
  `ThrowsAnyAsync<StorageException>` (derived types allowed). (category c, mild)
- Case 18: `ThrowsAnyAsync<ArgumentException>` (subclasses such as ArgumentNullException allowed); null text is not
  specified and not tested.
- Case 19: contract does not state whether tokens are combined with AND or OR. `Search_treats_or_operator_as_plain_word`
  assumes implicit AND (FTS5 default) and a literal "OR" token; an implementation joining with OR would fail it
  (category c).
- Case 17: tokenisation of `sender_a@example.test` depends on the FTS5 tokenizer; the test searches the full address
  as a quoted phrase (matches under the default unicode61 tokenizer).
- Case 12 tie-break: dates with identical instant and offset are used.
- Case 3: the test builds the newer-version file itself with a raw `SqliteConnection`
  (`schema_version(version INTEGER NOT NULL)` row = Current+1), no other tables.
- Case 4: `Connection` is a property; the test accesses it in a lambda.
- `FileEmlStore` path validation: the requirement defines rejection only on `DataPaths.ResolveMailPath`; whether
  `WriteAsync`/`OpenReadAsync`/`Exists`/`DeleteAsync` reject `../` paths themselves is not specified, so not tested
  (category c; relevant for security review).
- `ListAsync` with `Limit <= 0`/negative `Offset`, `SearchAsync` with `null`, `DeleteByUidsAsync` for an unknown folder
  and `DataPaths(null/"")` are unspecified and not tested.
- `test-map.json` uses `@feature:storage` entries as instructed; the real xUnit trait is `Category=feature:storage`
  (project configuration), so the planner must translate `@feature:x` to `Category=feature:x`.
- Test project references `Microsoft.Data.Sqlite` types (`SqliteConnection`) transitively through Infrastructure
  (contract exposes `SqliteDatabase.Connection`); no extra package reference added.
