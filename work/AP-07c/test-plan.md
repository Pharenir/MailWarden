# AP-07c – Testplan (author.0, aus dem Vertrag, ungelaufen)

Alle Tests tragen `feature:classifier` (Stores und Migration zusätzlich `feature:storage`). Smoke: `Each_tier_from_the_inbox_is_routed...`,
`Missing_file_yields_the_default_policy`, `Default_is_record_only...`, `Fresh_database_is_version_five...`,
`Entry_round_trips...`, `Empty_store_lists_nothing...`, `Directory_works_as_the_sender_directory...`.

## Fall -> Test
| Fall | Datei (Methoden) |
|---|---|
| 1 | Core `ClassificationRoutingTests` (Route-Tabelle 8 Kombinationen, 9 andere Rollen x 4 Stufen, Enum-Zahlen) |
| 2 | Core `MessageKeyTests` (Größe/Ordner/UID egal, Trim, "h:"-Form exakt, Adress-Groß/Klein, UTC-Instant, Kollision m/h) |
| 3 | Core `ClassificationPolicyTests` (Default, Validate 0/1/30/3650 ok, -1/3651/min/max Fehler); Infra `ClassificationPolicyFileTests` (fehlend, `{}`, teilweise, beide, Grenzen, unbekannter Schlüssel, Groß/Klein, doppelter Schlüssel, falscher Typ, ungültiger Wert ohne Wertnennung, ungültiges JSON, Wurzel kein Objekt) |
| 4 | `SchemaV5Tests` (Version 5, Tabellen/Schlüssel/Cascade, v4->v5 mit Daten inkl. `mail_commands`, v1->v5, Nutzung nach Migration, erneutes Öffnen, neuere Version 6 abgelehnt) |
| 5 | `SqliteClassificationLogTests` (Round trip alle Felder + Signale in Reihenfolge, null-Felder, alle Enum-Werte, unbekannt -> null, Duplikat behält ersten (auch im selben Commit), Schlüssel je Konto, Marker-Round-trip/Ersetzen, uint.MaxValue, Rollback, Abbruchtoken) |
| 6 | `SqliteClassificationLogTests` (Konto löschen -> Log+Marker weg, andere Konten bleiben; Ordner löschen -> Marker weg) |
| 7 | `SqliteContactStoreTests` (Upsert zweimal mit anderer Schreibweise = 1 Zeile, Name aktualisiert; weitere) |
| 8 | `ClassificationInputReaderTests` (From mit/ohne Name, Quoted, fehlt, zwei From, ohne Domain, Reply-To, drei Auth-Results in Reihenfolge, Garbage, Body-Limit 200000, Stream bleibt offen) |
| 9 | `ClassificationInputReaderTests` (HTML-Hosts klein + Duplikate, Tracking-Link mit Query, mailto/javascript/ftp ignoriert, kaputte URLs, nur `<a href>` bei HTML, Text-URLs, alternative mit HTML) |
| 10 | `ContactSenderDirectoryTests` |
| 11 | Safety: `First_pass_with_50_existing...`, `Mail_arriving_after_the_first_pass...` (Positivkontrolle), `First_pass_in_act_mode...`, `First_pass_over_an_empty_folder...` |
| 12 | Safety: `Record_only_stores_the_entries...`, `The_same_new_mails_in_act_mode...` (Positivkontrolle), `Record_only_does_not_provision...` |
| 13 | Routing: `Each_tier_from_the_inbox...` |
| 14 | Routing: `Trusted_contact_in_junk...`, `Inbox_and_junk_are_handled_in_the_same_pass`; `Other_folders_are_never_classified`, `Folder_with_uid_validity_zero_is_skipped` (Zusatz) |
| 15 | Safety: `Act_with_an_empty_address_book...`, `Act_with_one_contact...` (Positivkontrolle), `Mail_recorded_while_act_was_suspended...` |
| 16 | Safety: `Act_stays_suspended_when_the_only_known_addresses_are_the_accounts_own` (zwei Konten; Positivkontrolle = Fall 15) |
| 17 | Routing: `Missing_app_folder_is_provisioned_once...` (Screener/Quarantine/beide fehlen), `Second_pass...`, `Pass_with_both_app_folders_present...`, `Provisioner_gets_the_account...` |
| 18 | Routing: `Provisioner_failure_propagates...` |
| 19 | Decision: `Known_key_arriving_again...`, `Known_key_that_was_only_recorded...` |
| 20 | Decision: `Same_message_id_twice...`, `Copy_in_junk_with_the_same_message_id...`, `Copies_without_message_id...` |
| 21 | Decision: `Changed_uid_validity...`, `Matching_uid_validity...` (Positivkontrolle) |
| 22 | Decision: `Unreadable_mails_are_recorded_as_read_failed...` (fehlende Datei, EmlPath null, Garbage, kein From, kein Domain-Teil, IOException/UnauthorizedAccess mit Marker, dazu normale Mails), `...in_record_only_mode...`, `Read_failed_mail_is_not_retried...` |
| 23 | Routing: `Missing_junk_folder_gives_target_missing...`, `Screener_that_is_still_missing_after_provisioning...` |
| 24 | Routing: `Rejected_command_gives_enqueue_failed...` (ArgumentException/InvalidOperationException x Screener/Junk/Quarantine abgelehnt) |
| 25 | Routing: `Moves_are_split_into_commands_of_at_most_1000_uids...` (1000/1001/2500), `Retention_deletes_are_split...` (1500) |
| 26 | Safety: `Retention_zero_never_deletes...` + Positivkontrolle `The_same_ancient_quarantine_mail...` |
| 27 | Safety: `Retention_30_deletes_only_what_the_decision_time...` (Theorie, 14 Zeilen: 31 d, genau 30 d, 30 d minus 1 s, 29 d, Date-Header alt/jung, Zukunft, Outcome-/Action-Varianten), `Quarantine_mail_without_a_log_entry...`, `...mixed_quarantine`, `Retention_never_deletes_from_other_folders...` |
| 28 | Safety: `Retention_is_off_in_record_only_and_in_suspended_act` + Positivkontrolle `Retention_runs_in_effective_act...`, `Classification_moves_but_never_deletes...` |
| 29 | Decision: `Cancellation_between_folders...`, `Pre_cancelled_token...`, `Cancellation_while_reading_a_mail_is_not_recorded_as_read_failed` |
| 30 | Decision: `Report_counts_match...`, `Pass_without_new_mail_reports_zeros`, `Report_text_contains_no_subject_or_address`; Unbekanntes Konto: `Unknown_account_is_an_argument_exception...` |
| 31-33 | nicht automatisiert |

Sicherheitsfälle 11, 12, 15, 16, 26-28: jeweils `ListAsync` der echten Queue; Positivkontrollen siehe Tabelle. Fall 18/22/24: ein Marker-Text im Fake
(`marker_eml_failure_text`, `marker_queue_rejection_text`) darf in keiner Zelle von `classification_log`/`classification_marker` und nicht
in `report.ToString()` stehen (`DumpClassificationTablesAsync`).

## Angepasste Bestandstests (Version 4 -> `CurrentSchemaVersion`)
- `Storage\SchemaV2Tests.cs`: 6 Stellen `Assert.Equal(4, ...)` -> Konstante; Test `Current_schema_version_is_four` -> `..._is_at_least_two` (`>= 2`).
- `Storage\SchemaV3Tests.cs`: 2 Stellen -> Konstante.
- `Storage\SchemaV4Tests.cs`: 3 Stellen -> Konstante; `Current_schema_version_is_four` -> `..._is_at_least_four` (`>= 4`);
  `Fresh_database_is_version_four_and_has_the_command_table` -> `Fresh_database_is_current_version_and_has_the_command_table`.
- Die feste "5" steht nur in `SchemaV5Tests` (Vertrag D2).

## Test-Infrastruktur (neu, `TestSupport\`)
`ClassificationHarness` (echte Stores, echte `SqliteMailCommandQueue`, `FileEmlStore`, `FixedTimeProvider`, Konto mit Inbox 1000, Junk 2000,
Screener 3000, Quarantine 4000, ein Kontakt `sender_a@paypal.test`), `FakeFolderProvisioner`, `RejectingMoveQueue`, `ThrowingEmlStore`,
`ClassificationTestEml` (vier Fixtures, im Code erzeugt), `CommandAssert`. `test-map.json`: Eintraege fuer `SqliteClassificationLog*`,
`SqliteContactStore*` (classifier+storage) und zusaetzlich `MailKitFolderProvisioner*` (sync+classifier); `Infrastructure/Classification/**`
war schon vorhanden.

## Fixtures
Die EML-Fixtures liegen nicht als Dateien unter `tests\fixtures\classification\`, sondern werden in `ClassificationTestEml` erzeugt (kein csproj-Eingriff
nötig, keine Kopierregeln). Erwartete Stufen über echte Signale: Trusted (Kontakt, alle Auth pass, Score -20), Legit (Fremder, pass), LikelySpam
(SPF+DKIM fail für `paypal.test`, Score 40, Muster `SpamClassifierTests` 16), CertainSpam (`qzxkvb73.top`, alles fail, Score 110).

## Fragen / Annahmen für den Planer
1. **Fehlertyp der Policy-Datei:** getestet wird `InvalidDataException` wie bei `ClassifierSettingsFile` ("FormatException-style ... that class's pattern").
   Wird etwas anderes geworfen, ist das Test- oder Vertragsfrage.
2. **Zählung im Report:** `Classified` = Einträge mit Stufe (ohne `ReadFailed`, ohne `SkippedKnown`) - Fall 22 und 30 prüfen das exakt.
   `MovesEnqueued`/`RetentionDeletesEnqueued` sind im Vertrag nicht als "Befehle" oder "Mails" festgelegt; die Tests akzeptieren
   Anzahl Befehle **oder** Anzahl Mails (Fall 13 beides gleich) bzw. `>= 1` bei Retention.
3. **`mode`-Werte:** nur `"recordOnly"`/`"act"` getestet; Groß-/Kleinschreibung der Werte (z. B. `"Act"`) ist nicht festgelegt und nicht getestet.
   Kommentare/Trailing-Commas in der Policy-Datei sind ebenfalls nicht getestet.
4. **Leere/Whitespace-Message-ID:** Vertrag sagt nur "ohne Message-ID". Getestet ist `null`; `""`/Leerzeichen nicht.
5. **Ordner löschen:** Vertrag nennt Cascade nur für den Marker (`folder_id`); `classification_log.source_folder_id` hat laut D2 keinen
   Cascade. Getestet: Marker verschwindet; ob Log-Einträge bleiben, wird nicht behauptet.
6. **Leerer EML-Strom / Ausnahme beim Parsen:** der Vertrag nennt "Datei fehlt oder unlesbar, null-Eingabe". Getestet: fehlende Datei,
   EmlPath null, Garbage, kein From, From ohne Domain, `IOException` und `UnauthorizedAccessException` beim Öffnen -> `ReadFailed`.
   Andere Ausnahmetypen vom EML-Store und eine Ausnahme aus `Classify` (nicht erzwingbar, die Signale werden intern erzeugt) sind nicht abgedeckt.
7. **Konstruktoren der Stores:** `new SqliteClassificationLog(SqliteDatabase)` und `new SqliteContactStore(SqliteDatabase)` (wie die übrigen
   Sqlite-Stores) angenommen; `MessageKey` hasht UTF-8; Datumsform `date.UtcDateTime.ToString("O", Invariant)`.
8. **Vertragsstellen, die aus dem Wortlaut gelesen wurden:** Reader gibt bei `Authentication-Results` die Werte als eigene Header-Zeilen
   zurück (Test prüft nur enthaltene Marker in Reihenfolge); bei HTML zählen nur `<a href>`, nicht `img` und nicht Klartext-URLs;
   Body-Limit genau 200000 Zeichen bei längerem Klartext.
9. **Laufzeit:** Fall 25 (bis 2500 Mails) und Retention-1500 haben 120 s Einzeltest-Timeout statt 30 s (Masseneinfügen).
10. **Nicht getestet:** `AuthenticationFailedException` aus dem Provisioner (Konstruktor nicht aus Vertrag ableitbar; Verhalten wie `MailTransportException`),
    `ClassificationPolicy.Validate` für undefinierte `Mode`-Werte (nicht im Vertrag).
11. **Build nicht geprüft:** In diesem Schritt kein Build. Mögliche Reibungspunkte in verify.1: Namespace von `ClassificationPass*`/`ClassificationInputReader`
    (angenommen `MailWarden.Infrastructure.Classification`), Collection-Expression-Inferenz in `Assert.Equal([..], array)`.
