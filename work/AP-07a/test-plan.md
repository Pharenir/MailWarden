# AP-07a test plan (classifier core)

Project `tests\MailWarden.Core.Tests` (xUnit 2.9.3, net9.0, plain `[Fact]`/`[Theory]`, synchronous). All classes carry
`[Trait("Category", "feature:classifier")]`; the two smoke tests additionally `smoke`. Written against the contract only;
not yet compiled or run (step author.0). Helpers in `TestSupport\`: `FakeSenderDirectory`, `FixedPointsSignal`,
`AuthHeaders`, `TestInputs`.

## Case to test mapping
| Case | Tests |
|---|---|
| 1, 2 | `AuthenticationResultsParserTests.Parse_gmail_example`, `Parse_outlook_example_...` |
| 3 | `Parse_tolerates_case_spacing_comments_quotes_and_folding`, `Parse_accepts_missing_space_after_semicolon`, `Parse_uses_culture_invariant_comparison` |
| 4 | `Parse_keeps_several_dkim_results_in_header_order` (+ header.d vs header.i, SPF domain forms, verdict words) |
| 5 | `Parse_never_throws_for_malformed_content`, `Parse_garbage_yields_no_method_results`, `Parse_null_...` |
| 6 | `Parse_unknown_verdict_word_maps_to_unknown` |
| 7 | `DomainNamesTests.GetDomain_*` |
| 8 | `GetOrganizationalDomain_examples`, `Multi_label_public_suffixes_are_respected` (13 suffixes) |
| 9 | `AreAligned_*` |
| 10, 11, 12 | `RandomDomainScorerTests` (Random_labels / Plausible_labels / Short_labels) |
| 13, 18 | `SpamClassifierTests.Authenticated_plausible_sender_...`, `Missing_header_for_plausible_non_contact_...` |
| 14 | `Obvious_spam_is_certain_spam_with_all_expected_findings`, `Signals_are_returned_in_evaluation_order` |
| 15, 16, 17 | `Authenticated_contact_...`, `Contact_with_failed_authentication_...`, `Contact_without_header_is_capped_...` |
| 19, 20 | `Block_list_beats_...`, `Allow_list_with_unknown_...`, `Allow_list_with_failed_...` |
| 21, 22 | `Only_the_top_most_header_counts_*`, `Dkim_pass_for_a_non_aligned_domain_...`, `AuthStatus_is_derived_from_the_header` (23 rows) |
| 23 | `SignalTests.Freemail_sender_*` |
| 24, 25 | `SignalTests.Display_name_*`, `Freemail_reply_to_*`, `Aligned_reply_to_*`, `Foreign_non_freemail_reply_to_*` |
| 26 | `Default_thresholds_decide_the_tier` (-10..500, boundaries 29/30/69/70), `Custom_thresholds_are_used` |
| 27 | `ClassifierSettingsTests.Validate_rejects_...`, `Classifier_constructor_rejects_...` (10 invalid variants) |
| 28 | `Duplicate_signal_ids_are_rejected_by_the_constructor` |
| 29 | `Classify_null_...`, `Classify_from_address_without_at_sign_...` |
| 30 | `SignalReasonTests` (non-empty reasons over 8 scenarios, concrete-value checks per signal) |
| 31 | `ClassifierSmokeTests` (smoke + feature:classifier) |

Further: authentication points table (`Authentication_signal_points`, 13 rows incl. cap 50), domain-not-in-body, link
80 % rule and distinct counting, settings defaults, freemail/TLD case-insensitivity, signal order and null results.

## Positive controls
- Case 22 (alignment ignored must fail): `Dkim_pass_for_a_non_aligned_domain_is_not_authenticated`, plus scenario rows
  with non-aligned DKIM/SPF pass.
- Case 21 (index != 0 used must fail): `Only_the_top_most_header_counts_failed_over_passed`, reverse variant,
  `Lower_headers_are_ignored_when_the_top_most_is_inconclusive`.
- Case 16 (contacts bypass authentication must fail): `Contact_with_failed_authentication_is_not_trusted_...`, requires
  Failed status, tier LikelySpam, score 40, Override null.

## Ambiguities and assumptions
- Case 14: the requirement writes header `dmarc=fail spf=fail dkim=fail`. I used a well-formed header with ';'
  separators (`AuthHeaders.AllFail`); a literal space-separated form would have no semicolons and is not tested.
- Case 5: `Parse("none")` has no ';' and no '='. AuthServId is not asserted (could be "none"); only no exception and
  no method results.
- Case 3: "quoted strings are skipped" tested only for a quoted `reason="a;b=c.test"`, not for quoted domain values.
- Case 17 and rule 6: "at most LikelyLegit" is read as always LikelyLegit (lowest score tier), also for negative scores.
- Case 30: "contains the concrete value" is asserted loosely (any of domain, label, host or method name) for
  authentication, link and reply-to reasons because the exact wording is open; random-domain, suspicious-tld,
  domain-not-in-body, display-name check the label/domain/TLD strictly. Score value in the random-domain reason is not
  asserted.
- random-domain middle tier (C9): covered by case 10a, label `wrtplkjo` (S = 0.619, asserted in the test before classifying).
- Rule 5 Override wording: only the substring "authenticated" is asserted; rule 4/6/3 substrings "allow list",
  "could not be verified", "block list" (case-insensitive).
- Domain edge cases not tested because unspecified: `GetDomain("user@.")`, `GetRegistrableLabel("localhost")`,
  freemail match for subdomains of freemail domains, ctor null arguments.
- Case 25 "ReplyTo freemail while From freemail" is not specified explicitly; read from "otherwise +10" (tested).
- Display-name tokens such as "Version 2.0" (digit TLD) are assumed not domain-like per "letter-only TLD".
- Test-map: `TestSupport` changes trigger FULL per the existing map entry; new project is not in the solution yet
  (planner adds it at verify).

## verify.1 additions (2026-10-31)
- Case 10a: `Middle_tier_random_label_gives_fifteen_points` (C9).
- C5: `Duplicate_signal_ids_differing_only_in_case_are_rejected`.
- C2: `Parse_maps_verdict_words` row `hardfail` -> Fail.
- C8 confirmed: contact + Unknown always LikelyLegit and freemail Reply-To with freemail From +10 are already tested as written.

## fix.2 additions (C10-C14, 2026-10-31; written, not yet compiled or run)
- Case 3a (C10): `AuthenticationResultsParserTests.Parse_spf_domain_is_taken_after_the_last_at_sign` (4 rows; +C17 `x@attacker.test"@victim.test"`, +C19 quoted `"Bounce@Example.TEST"`),
  `Parse_method_name_inside_a_value_does_not_start_a_result`, `Parse_dkim_domain_from_quoted_header_i` (C19).
- Case 21a (C11): `SpamClassifierTests.Dmarc_fail_beats_aligned_spf_pass`, `Contact_with_dmarc_fail_and_aligned_spf_pass_is_not_trusted`.
- Case 22a (C12): `Dmarc_pass_for_a_foreign_domain_is_not_authenticated`.
- Case 28a (C13): `Signal_result_with_empty_reason_is_rejected` ("" and whitespace), `Signal_result_with_a_different_signal_id_is_rejected`
  (InvalidOperationException, message contains the signal id "stub").
- Case 30a (C14): `SignalReasonTests.Reasons_stay_short_for_very_long_sender_values` (1 000-char token in display name and
  Reply-To; all reasons <= 200 chars; display-name and reply-to findings must still exist).
- Changed existing rows in `AuthStatus_is_derived_from_the_header`: `dmarc=pass header.from=other.test` Authenticated -> Unknown (C12);
  `dmarc=fail ...; dkim=pass header.d=paypal.test` Authenticated -> Failed (C11). New rows: DMARC pass without header.from and
  with subdomain header.from -> Authenticated; DMARC fail + aligned SPF pass -> Failed; foreign DMARC pass + aligned DKIM pass ->
  Authenticated; foreign DMARC pass + SPF fail -> Failed.
- Assumptions: the 30a display-name token `<1000 a>.other.test` must count as domain-like (label length not limited by the
  contract); test name checks of exception message only for the signal id.

## fix.3 additions (C20-C22, 2026-10-31; written, not yet compiled or run)
- Case 3b (C20): `AuthenticationResultsParserTests.Parse_spf_domain_continues_across_whitespace_and_comments_before_at_word` (3 rows -> `attacker.test`).
- Case 3c (C21): `Parse_empty_value_followed_by_whitespace_does_not_swallow_the_next_pair` (Dmarc Fail `a.test`; Spf domain null). Prefixed with `spf=pass` so the empty value belongs to a real result.
- Case 22b (C22): `SpamClassifierTests.Dmarc_pass_with_empty_quoted_header_from_is_not_authenticated`; new row `dmarc=pass header.from=""` -> Unknown in `AuthStatus_is_derived_from_the_header`.
- Existing tests checked against C20-C22: no change needed. Row `mx.test; dmarc=pass` (absent header.from) stays Authenticated (C22 keeps C12 behaviour for absent). Existing C17/C19 rows unaffected by C20 (no whitespace before '@' word).
