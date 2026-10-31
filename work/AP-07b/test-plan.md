# AP-07b Testplan (author.0, gegen requirement.md)

## Fall-Nr. zu Testmethode

Dateien: `CA` = `tests\MailWarden.Core.Tests\Classification\CompAuthAndVerdictTokenTests.cs`,
`AG` = `...\AuthGatedContentSignalTests.cs`, `SF` = `tests\MailWarden.Infrastructure.Tests\Classification\ClassifierSettingsFileTests.cs`.

| # | Test(s) |
|---|---|
| 1 | CA `Parse_reads_compauth_pass_and_ignores_the_reason_property`, `Dmarc_fail_with_compauth_pass_is_unknown_with_zero_point_finding`, `Compauth_pass_with_dmarc_spf_and_dkim_fail_adds_no_points`, `Dmarc_fail_with_compauth_pass_and_aligned_spf_pass_is_unknown` |
| 2 | CA `Dmarc_fail_with_compauth_fail_stays_failed_with_forty_points`, `Dmarc_fail_without_compauth_pass_stays_failed` |
| 3 | CA `Contact_in_the_compauth_case_is_likely_legit_with_unverified_override`, `Contact_with_dmarc_fail_and_compauth_fail_is_not_trusted` |
| 4 | CA `Dmarc_pass_with_compauth_pass_is_authenticated_as_before`, `Dmarc_pass_with_compauth_fail_stays_authenticated` |
| 5 | CA `Parse_first_compauth_result_counts_pass_before_fail`, `..._fail_before_pass`, `Two_compauth_results_use_the_first_for_the_status` |
| 6 | CA `Dmarc_bestguesspass_with_aligned_spf_pass_is_authenticated_via_spf`, `Parse_maps_new_verdict_tokens_for_every_method` |
| 7 | CA `Parse_dkim_timeout_is_a_temp_error_with_domain`, `Dkim_timeout_alone_is_unknown_without_authentication_finding`, `Parse_maps_new_verdict_tokens_for_every_method` |
| 8 | AG `Authenticated_sender_gets_no_link_domain_mismatch_finding` |
| 9 | AG `Unknown_or_failed_sender_keeps_the_link_domain_mismatch_finding`, `Compauth_case_sender_keeps_the_link_domain_mismatch_finding` |
| 10 | AG `Authenticated_sender_gets_no_domain_not_in_body_finding`, `Unknown_or_failed_sender_keeps_the_domain_not_in_body_finding` |
| 11 | AG `Authenticated_sender_with_weak_random_label_gets_no_random_domain_finding`, `Unknown_or_failed_sender_with_weak_random_label_keeps_fifteen_points` |
| 12 | AG `Authenticated_sender_with_strong_random_label_keeps_thirty_five_points` |
| 13 | SF `Missing_file_yields_the_default_settings` |
| 14 | SF `Single_threshold_key_overrides_only_that_value`, `Certain_threshold_key_overrides_only_that_value`, `Empty_object_keeps_all_defaults` |
| 15 | SF `Given_tld_list_replaces_the_default_list_completely`, `Given_freemail_list_replaces_the_default_list_completely`, `All_keys_can_be_given_together` |
| 16 | SF `Unknown_key_is_rejected_and_named`, `Keys_are_case_sensitive` |
| 17 | SF `String_instead_of_number_is_rejected_and_names_the_key`, `Fractional_number_for_a_threshold_is_rejected`, `String_instead_of_list_is_rejected_and_names_the_key`, `Number_as_list_entry_...`, `Null_list_entry_...` |
| 18 | SF `Inverted_thresholds_fail_validation_with_inner_argument_exception`, `Values_that_fail_validation_are_rejected_with_inner_argument_exception` |
| 19 | SF `Invalid_json_is_rejected_with_file_name_only`, `Root_that_is_not_an_object_is_rejected_with_file_name_only`, `Error_message_names_the_actual_file_name_of_the_given_path` |
| 20 | SF `Comments_and_trailing_commas_are_allowed` |
| 21 | CA `Obvious_spam_with_failing_authentication_stays_certain_spam`; AG `Obvious_spam_with_failing_authentication_is_still_certain_spam`, `Obvious_spam_if_it_were_authenticated_loses_only_the_gated_content_points`; bestehend `ClassifierSmokeTests.Default_classifier_rates_obvious_spam_as_certain_spam` |

Zusaetzlich (ueber den Vertrag hinaus, aus E1-E4 abgeleitet): Verdict-Mapping unveraenderter Tokens, compauth nur aus oberstem Header, compauth in Kommentar/Wert zaehlt nicht, compauth allein authentifiziert nicht, suspicious-tld/reply-to/display-name bleiben auf authentifizierten Mails aktiv.

## Angepasste AP-07a-Tests

Geprueft: alle Tests unter `tests\MailWarden.Core.Tests\Classification\`. Kein bestehender Test erwartet Punkte von
`link-domain-mismatch`, `domain-not-in-body` oder schwachem `random-domain` auf authentifizierten Mails (diese Tests
nutzen keine Header, `AllFail` oder Authentifizierung ohne Links/mit passendem Body), und keiner erwartet
`bestguesspass`/`timeout` als Unknown. Daher kein Verhaltens-Update noetig.

| Datei / Test | Aenderung | Altes / neues Sollverhalten |
|---|---|---|
| `AuthenticationResultsParserTests.Parse_outlook_example_without_authserv_id_and_ignoring_compauth` | Nur umbenannt in `Parse_outlook_example_without_authserv_id` | Name behauptete "ignoring compauth"; seit E1 wird compauth gelesen. Assertions unveraendert; compauth wird in `CompAuthAndVerdictTokenTests.Parse_outlook_example_reads_compauth_pass` geprueft |

Hinweis: `AuthenticationResultsParserTests.Parse_ignores_unknown_methods` enthaelt `compauth=fail reason=000` in der Liste
"unbekannter" Methoden. Die Assertions (Spf/Dkim/Dmarc) bleiben gueltig; compauth ist nun bekannt, der Test prueft es nicht.

## Offene Unklarheiten der Anforderung
- Fall 1: Wortlaut der Begruendung nicht festgelegt; Test verlangt "DMARC" plus eines von "composite", "compauth", "provider".
- Fall 3: Override-Text wird als Teilstring "Sender is a contact but authentication could not be verified" (case-insensitive) geprueft.
- E4: Verhalten bei leerer Liste (`"suspiciousTlds": []`), `null` als Schwellenwert, doppelten Keys, UTF-8-BOM und ungueltigem Pfad (null) ist nicht festgelegt -> nicht getestet.
- E4: "Fractional number" (30.5) fuer int-Schwelle wird als falscher Werttyp erwartet (InvalidDataException mit Key).
