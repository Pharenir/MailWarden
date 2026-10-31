# AP-02a test plan

Project: `tests\MailWarden.Infrastructure.Tests\Accounts\`, tag `feature:accounts` (smoke case additionally `smoke`).
Status: written against `requirement.md`, not yet compiled or run (author.0).

## Case to test mapping
| # | Class.Test |
|---|---|
| 1 | DpapiCredentialStoreTests.Write_then_read_returns_identical_bytes_and_file_does_not_contain_plain_secret |
| 2 | DpapiCredentialStoreTests.Overwrite_returns_new_value_and_leaves_no_tmp_file |
| 3 | DpapiCredentialStoreTests.Read_of_missing_key_returns_null_and_delete_is_noop (+ Delete_removes_existing_key) |
| 4 | DpapiCredentialStoreTests.Invalid_keys_are_rejected_on_every_method (theory: six keys incl. null); Key_of_128_valid_characters_is_accepted (boundary) |
| 5 | DpapiCredentialStoreTests.ListKeys_returns_prefix_matches_sorted |
| 6 | DpapiCredentialStoreTests.Corrupt_file_throws_CredentialStoreException_without_bytes_in_message |
| 7 | DpapiCredentialStoreTests.Second_instance_on_same_directory_reads_what_first_wrote |
| 8 | FolderRoleResolverTests.Microsoft_list_from_AP00 (12 rows) |
| 9 | FolderRoleResolverTests.Google_list_from_AP00 (12 rows) |
| 10 | FolderRoleResolverTests.Server_outbox_is_never_the_Outbox_role |
| 11 | FolderRoleResolverTests.App_owned_folders_are_recognised_only_at_top_level |
| 12 | FolderRoleResolverTests.Name_inbox_is_case_insensitive_without_flags; Role_names_without_flags_stay_None_on_google |
| 13 | FolderRoleResolverTests.Empty_name_throws_ArgumentException |
| 14 | ProviderProfilesTests.DetectFromAddress_* (known domains, unknown null, invalid) |
| 15 | ProviderProfilesTests.For_GenericImap_throws_NotSupportedException; Microsoft_/Google_profile_has_contract_endpoints |
| 16 | MailKitFolderFlagsTests (combined, HasChildren, None, each listed attribute) |
| 17 | AuthErrorMappingTests.Msal_codes_map_to_reasons; Google_errors_map_to_reasons |
| 18 | AuthConfigurationTests (file only, env only, env overrides file, missing file, Require* x2, malformed JSON) |
| 19 | AccessTokenTests.ToString_does_not_contain_token_value |
| 20 | AccountServiceTests.AddAccount_stores_account_with_username_as_name_and_address |
| 21 | AccountServiceTests.AddAccount_again_with_different_case_returns_existing_account |
| 22 | AccountServiceTests.AddAccount_for_GenericImap_throws_NotSupported; ..._for_provider_without_token_provider_... |
| 23 | AccountServiceTests.Constructor_rejects_two_providers_for_same_MailProvider |
| 24 | AccountServiceTests.GetAccessToken_for_unknown_id_...; GetAccessToken_propagates_InteractionRequired_unchanged (+ happy path) |
| 25 | AccountServiceTests.Reauthenticate_with_other_account_throws_AccessDenied_and_signs_out_the_new_grant (+ same-account control) |
| 26 | AccountServiceTests.RemoveAccount_signs_out_and_deletes_account; RemoveAccount_with_unknown_id_is_noop_without_sign_out |
| 27 | AccountsSmokeTests.Add_get_token_remove_works_end_to_end_and_temp_root_is_removable |

Not covered automatically: MicrosoftTokenProvider, GoogleTokenProvider, ConnectionProbe (need browser/servers; cases 28-30).
The Microsoft/Google provider-specific name rule tests in 8/9 also assert that rule 5 does not apply to Google.

## Manual cases (Dennis, with the Builder's AuthProbe under `_scratch\auth-probe\`)
- 28: Start AuthProbe with a data root containing `auth-config.json`. Add both hotmail.de accounts and the googlemail
  account (browser consent), run ConnectionProbe. Expect ImapOk and SmtpOk true; folder classification matches the
  case 8/9 tables (Outlook: Archive, hidden Notes/Outbox/Scheduled/Synchronisierungsprobleme; Gmail: All Mail hidden,
  `Junk-E-Mail` visible with role None, Spam = Junk).
- 29: Run again. Expect no browser window; token from cache. `<root>\credentials\` contains only `*.bin` files, no
  readable JSON.
- 30: Cancel the browser login, then deny consent in a second run. Expect AuthenticationFailedException with
  UserCancelled / AccessDenied, readable one-sentence message, probe exits cleanly. Also try Google with the app in
  "Testing" status and a non-test user (403 -> AccessDenied).

## Positive controls
- Case 1: fails if the store writes plain text (byte-sequence search plus string search in file).
- Case 9: `Junk-E-Mail` row asserts None/visible; fails if the role is derived from the name.
- Case 25: asserts `other@example.test` in `SignedOutUsernames`; fails if the mismatched grant is not signed out.
  Control: matching re-auth must not sign out.

## Ambiguities / assumptions
- Case 4: key `null` expects `ArgumentNullException` on Read/Write/Delete; ListKeys with an invalid prefix is not
  tested (contract defines key rules only for keys). Async methods are assumed to throw inside the returned task
  (tests await them); a synchronous throw would also surface as a failure inside the lambda, accepted either way.
- Case 5/smoke: `ListKeysAsync(string.Empty)` is assumed to list all keys (empty prefix); smoke test uses it.
- Case 6: "message without the bytes" is checked via hex and base64 of the garbage plus the plain secret text.
- Case 8: `Archive/Steuer` -> None visible; `Synchronisierungsprobleme` itself -> Hidden (contract lists it and its children).
- Case 9: `[Gmail]` is tested with NonExistent alone and NonExistent|NoSelect.
- Case 10: also uppercase `OUTBOX` (contract says names for rules 3-5; case sensitivity of rule 5 not defined, only the
  NotEqual-Outbox property is asserted, so either result passes).
- Case 11: `Archive/Quarantine` on Google added by analogy (top-level only).
- Case 12: `Trash` without flags on Google -> None added by analogy; Google `Archive`/`Notes` -> None visible (rule 5 is Microsoft only).
- Case 13: `null` name also expected as ArgumentException (any subclass).
- Case 14: `windowslive.com` added from the contract text; empty address -> ArgumentException assumed.
- Case 18: malformed JSON is checked with `Load`; "missing key not value" is asserted by key name only.
- Case 20: `FakeTokenProvider` usernames queue; fake sign-in returns the last queued username repeatedly.
- Case 24: unknown id check assumes `ArgumentException` (subclass allowed).
- Case 25a (A3): fake signs in the address of a second stored account -> AccessDenied, no SignOut, both accounts unchanged.
- A1: ListKeys empty prefix = all; null -> ArgumentNullException; `../`, `a/b`, `a:b`, 129 chars -> ArgumentException
  (tests in DpapiCredentialStoreTests). A7: `OUTBOX` already covered by case 10.
- Case 25: "account unchanged" is checked via `GetAsync` equality.
- Case 27: the DPAPI store is exercised alongside (writes/deletes a key) but is not wired into AccountService
  (contract has no such dependency).
- Case 22a (A12): Google account stored, Microsoft fake signs in the same address -> AccessDenied, SignOut on the Microsoft fake only, store unchanged. 22b: same-provider duplicate -> existing account, no SignOut.
- Case 25c (A19): fake signs in another, not stored address and its SignOut throws AuthenticationFailedException -> still AccessDenied (not the SignOut exception), SignOut attempted, account unchanged.
- Case 25b (A12): Reauthenticate with an address stored only under another provider -> AccessDenied and SignOut of the new grant (assumed reading of "same provider"). 25a stays valid under A12 (same provider, no SignOut).
- Case 26a (A11): SignOut throws AuthenticationFailedException(Configuration) -> no exception, account gone. Extra: InvalidOperationException from SignOut propagates.
- Helper: `FakeTokenProvider.SignOutException` added (records the username, then throws).
- A9, A10, A13-A16: no new tests (need failing store, interactive flow or IMAP).
