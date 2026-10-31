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
- random-domain tier +15 (score 0.5 to 0.8) is not tested: no label in the requirement is specified in that band.
- Rule 5 Override wording: only the substring "authenticated" is asserted; rule 4/6/3 substrings "allow list",
  "could not be verified", "block list" (case-insensitive).
- Domain edge cases not tested because unspecified: `GetDomain("user@.")`, `GetRegistrableLabel("localhost")`,
  freemail match for subdomains of freemail domains, ctor null arguments.
- Case 25 "ReplyTo freemail while From freemail" is not specified explicitly; read from "otherwise +10" (tested).
- Display-name tokens such as "Version 2.0" (digit TLD) are assumed not domain-like per "letter-only TLD".
- Test-map: `TestSupport` changes trigger FULL per the existing map entry; new project is not in the solution yet
  (planner adds it at verify).
