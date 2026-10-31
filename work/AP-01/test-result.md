# AP-01 test result (verify.1)

Overall: PASS

```
Stufe:        S2 (Filter deckt die gesamte Suite ab, daher faktisch S3; zur Kontrolle zusaetzlich ungefilterter Vollauf)
Auswahl nach: Feature-Tags (feature:storage + smoke) / voll
Ergebnis:     72 von 72 ausgewaehlten Tests gruen (Suite gesamt: 72)
Fehlschlaege: keine
Nicht gelaufen: nichts (keine als slow markierten Tests vorhanden)
Laufzeit:     2,6 s gefiltert, 1,4 s voll (keine Historie)
Abbruch:      -
Auffaellig:   nichts
```

## Build
`dotnet build MailWarden.sln -warnaserror`: 0 Warnungen, 0 Fehler.

## Fixes in test code (category a)
- FileEmlStoreTests.cs line 64: xUnit2029, `Assert.Empty(filtered)` replaced by `Assert.DoesNotContain(files, predicate)`.

## Failures
none

## verify.2 (2026-10-31)

Overall: PASS

```
Stufe:        S3 (Schema/zentrale Bausteine geaendert)
Auswahl nach: voll
Ergebnis:     100 von 100 Tests gruen (Suite gesamt: 100)
Fehlschlaege: -
Nicht gelaufen: -
Laufzeit:     3.5 s (Waechter), 1.7 s Testlauf (keine belastbare Historie, Dauer klar unter Mindestwert)
Abbruch:      -
Auffaellig:   weiche Grenze nicht ueberschritten; Build -warnaserror 0 Warnungen / 0 Fehler
```

New tests (test branch, commits 843dff5 and 07608a9, merged into feat branch):
- D15 / case 11a: `SqliteMessageStoreTests.Upsert_of_existing_message_with_other_account_throws_and_keeps_stored_row`
- D16 / case 22: `SqliteAccountStoreTests.Delete_removes_full_text_entries_of_the_deleted_messages_only`
- D16 / case 23: `SqliteFolderStoreTests.Delete_removes_full_text_entries_of_the_deleted_folder_only`
- D14 / case 27: two new theory rows in `DataPathsTests.Resolve_rejects_trailing_dot_space_and_device_names` (`COM` + U+00B9, `CON .eml`)

Test count: 100 (5 new test cases; earlier recorded S3 run: 72, the task expected > 81; I did not reconcile the difference).
Classification of failures: none. `.test-logs\durations.jsonl` updated.
