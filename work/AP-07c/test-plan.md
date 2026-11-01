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

## author.1 (Decisions fix.2, G1-G14) - ungelaufen

| Fall | Test |
|---|---|
| 34 | `ClassificationPassFix2Tests.Folder_without_sync_state_is_skipped_then_baselined_then_classified`, `Only_the_folder_without_sync_state_is_skipped` |
| 35 | `Uid_validity_reset_with_partial_reload_classifies_nothing` |
| 36 | Core `MessageKeyTests` (m:/h: mit Received, gleiche/andere Received, null = leer); Whitespace im Pass: `Received_with_different_whitespace_gives_the_same_key` |
| 37 | `Campaign_with_a_reused_message_id_and_different_received...` |
| 38 | `Mail_moved_back_by_hand_with_the_same_received_is_skipped`, `Mails_without_received_are_still_recognised_by_message_id` |
| 39 | `Quarantine_mail_with_a_reused_message_id_but_another_received_is_never_deleted` (Positivkontrolle: Kopie mit der entschiedenen Received wird geloescht) |
| 40 | `ClassificationMessageReaderTests` (roher Header mit Encoded Word, Kontakt nicht Trusted, Positivkontrolle echtes `dmarc=pass` = Trusted, Received roh), Pass: `Forged_dmarc_pass_in_an_encoded_word_does_not_rescue_a_junk_mail` (mit echter Trusted-Mail daneben) |
| 41 | `ClassificationMessageReaderTests` (normal mit Normalisierung, Garbage, kein From, kein Received, `IOException` propagiert, `ReadAsync`-Wrapper) |
| 42 | `Senderless_*` (zwei From / leere Gruppe; Inbox Act = Enqueued/Screener, RecordOnly = Recorded, Junk = NoAction, TargetMissing, Garbage bleibt ReadFailed) |
| 43 | `Due_mail_with_a_pending_delete...`, `Only_the_newly_due_mail...`, `Due_mail_with_a_failed_delete...` (Fake `FailedStatusQueue`) |
| 44 | `Rejected_delete_is_counted_as_rejected...` (+ Positivkontrolle) |
| 45 | Core `ClassificationRoutingTests.Route_with_an_undefined_tier_throws` |
| 46 | `More_than_10000_links_are_capped...` (+ Kontrolle unter der Grenze) |

Angepasst: `ClassificationHarness` (Sync-State fuer Inbox/Junk standardmaessig, `KeyAsync` liest Received aus der EML, `MarkSyncedAsync`,
`withSyncState`), `ClassificationTestEml` (`received`-Parameter), `MessageKeyTests` (Signatur `For(message, topReceived)`),
Fall 22 (Test `Unreadable_mails_...`: Mails ohne brauchbares From sind nach G7 nicht mehr `ReadFailed`, sondern Screener; `ReadFailed` = 5).
`Garbage_bytes_give_null` unveraendert.

Fragen an den Planer:
1. **Whitespace-Normalisierung (Fall 36):** nicht auf `MessageKey.For` getestet (es bekommt den schon normalisierten Wert), nur ueber die Pass.
   Falls `For` selbst normalisieren soll, bitte sagen.
2. Hash-Eingabe ohne Received: getestet als `Message-ID + "\n" + ""` bzw. `from\ndate\nsubject\n` (null = leer, gleicher Schluessel).
3. `ReadMessageAsync` bei Garbage: `TopReceived` null oder leer akzeptiert, `Input` null.
4. Fall 42 "leere Gruppe" als `From: undisclosed-recipients:;`; Erwartung `Override` exakt "Message has no single usable sender", Score 0.
5. Fall 43 "Befehl fehlgeschlagen": mit einem Dekorator getestet, der alle Befehle als `Failed` listet (kein echter Executor).
6. G12/G13 (Subscribe, Provisioner-Verhalten) und G14 (Probe) sind ohne Fake-IMAP nicht unit-testbar; `TierCounts` mit allen vier Stufen
   und `ContactCount` ohne eigene Adresse in der Tabelle sind nicht zusaetzlich getestet (Fall 30 prueft `TryGetValue` je Stufe).

## author.2 (Decisions fix.3, H1-H8) - ungelaufen
| Fall | Test |
|---|---|
| 47 / 42 | `ClassificationPassFix2Tests.Senderless_*` jetzt auf Junk (Inbox Act `Enqueued`/`MoveToJunk`, RecordOnly `Recorded`, Junk `NoAction`, ohne Junk-Ordner `TargetMissing`); Fall 22 (`Unreadable_mails_...`): Mails ohne brauchbares From -> Junk-Befehl [14,17], Screener [18] |
| H2 | `Senderless_mails_with_the_same_message_id_and_different_received_are_two_decisions` (+ Kontrolle gleiche Received = ein Eintrag) |
| 48 | Core `ClassificationRoutingTests` (`RouteWithoutSender`: Inbox -> MoveToJunk, Junk -> None, andere Rollen -> ArgumentOutOfRangeException) |
| 49 | `ClassificationMessageReaderTests` (`ReadTopReceivedAsync`: gleicher Wert wie `ReadMessageAsync().TopReceived` bei normal und gefaltet, ohne Received null, Garbage null, ohne From, nur oberstes Received, IOException propagiert) |
| 50 | `Quarantine_mail_with_a_corrupt_body_but_readable_headers_is_deleted_when_due` (Kontrolle intakt + 4 Beschädigungen: Multipart ohne Boundary, offenes Multipart, ungültiges Base64, unbekannter Charset mit ungültigem QP) und `Quarantine_mail_with_an_unreadable_file_is_never_deleted` |
| 51 | Core `NonAsciiSeparatorTests` (0xA0, 0x85, 0x80, 0x9F spalten nicht, Status bleibt Failed; ASCII Leerzeichen/Tab trennen weiter) |

Fragen an den Planer:
1. **Signaturen angenommen:** `ClassificationRouting.RouteWithoutSender(FolderRole)` (statisch, Rückgabe `ClassificationAction`) und
   `ClassificationInputReader.ReadTopReceivedAsync(Stream, CancellationToken ct = default)` -> `Task<string?>`.
2. **H2, Ausnahme beim Bau des Inputs oder aus `Classify`:** nicht herstellbar (Signale werden im Pass erzeugt); nur der Teil "Schlüssel
   enthält das Received" ist getestet. Gibt es eine konkrete Mail, die das auslöst?
3. **Fall 50:** die Beschädigungen sind Annahmen (Multipart ohne/mit offener Boundary, ungültiges Base64, unbekannter Charset mit kaputtem
   Quoted-Printable). Liest MimeKit sie ohnehin ohne Ausnahme, beweist der Test nur "Schlüssel stimmt".
4. **Fall 51:** der Test geht über den Klassifizierer, weil `AuthenticationResultsTokenizer` nicht öffentlich ist; ein direkter
   Tokenizer-Test bräuchte `InternalsVisibleTo`.

## author.3 (Decisions fix.4, J1-J6) - ungelaufen
| Fall | Test |
|---|---|
| 52 | `ClassificationPassEvaluationTests`: `Throwing_signal_gives_evaluation_failed_and_moves_nothing` (Inbox, Kontakt-Mail, Junk; Queue per `ListAsync` leer, Marker angehoben, `EvaluationFailed` = 3, `Classified`/`ReadFailed` 0), `Exception_text_of_the_signal_is_in_no_entry_and_not_in_the_report`, `..._in_record_only_mode...`, `Senderless_mail_still_goes_to_junk_while_a_signal_throws` |
| 53 | `Same_mails_again_after_the_signal_is_fixed_are_known_and_stay` |
| 54 | `Same_setup_without_the_throwing_signal_classifies_and_moves`, `Explicit_default_signals_behave_like_the_default` |
| 55 | `NestedMimeReaderTests` (eigene Klasse; 1023 und 5000 Ebenen `multipart/mixed` und `message/rfc822`, `ReadMessageAsync` und `ReadTopReceivedAsync`, Kontrolle flache Verschachtelung) |
| - | Core: `ClassificationOutcome.EvaluationFailed == 7` |

Harness: `CreatePass(..., signals:)`; Fake `ThrowingSignal` (Marker `marker_signal_failure_text`).

Fragen an den Planer:
1. Angenommen: `ClassificationPassReport.EvaluationFailed` (int), Enum-Wert `EvaluationFailed = 7`, Parameter heißt `signals`.
2. Fall 52: Der Eintrag einer fehlgeschlagenen Auswertung hat `PlannedAction None`; `Score`, `AuthStatus`, `Override` werden nicht geprüft (nicht im Vertrag).
3. Fall 55: 5000 Ebenen zusätzlich zu 1023; ein Absturz des Testprozesses wäre das Fehlersignal (nicht als einzelner Testfehler fangbar).
