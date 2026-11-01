# AP-07c - Testergebnis (verify.2, nach fix.2)

Build `dotnet build MailWarden.sln -warnaserror` auf `feat/ap-07c-classifier-pipeline` (mit Test-Branch): 0 Fehler.

```
Stufe:        S3
Auswahl nach: voll
Ergebnis:     1229 von 1229 Tests grün (Suite gesamt: 1229) - Core.Tests 415 von 415, Infrastructure.Tests 814 von 814
Fehlschläge:  -
Nicht gelaufen: nichts (Fälle 31-33 sind bewusst nicht automatisiert)
Laufzeit:     7,1 s (Historie: S3 ca. 4-5 s bei 964 Tests, bei 1229 Tests proportional plausibel)
Abbruch:      -
Auffällig:    keine weiche Grenze überschritten
```

## Verlauf in verify.2
Erster Lauf nach fix.2: 14 von 1229 rot, alle Testfehler (Einordnung a), keine Produktabweichung.
- `Normal_message_gives_parsed_input_and_normalised_top_received`: der Reader liefert den Rohwert (ungefaltet, getrimmt, Tab bleibt);
  die Whitespace-Normalisierung macht `MessageKey.For`. Test prüft jetzt nach Normalisierung (Entscheidung des Planers).
- 13 Retention-Tests (Löschen blieb aus): die gesetzten Quarantäne-Mails hatten keine EML-Datei. Nach fix.2 hängt der Schlüssel am
  Received der EML, eine Mail ohne lesbare EML wird von der Retention offenbar nicht gelöscht (sicher). Die Seeds legen jetzt eine EML an
  (realistisch: der Sync lädt jede Mail), außerdem bekommt die Quarantäne im Harness einen Sync-State.
- Offene Frage (c, nicht blockierend): Verhalten der Retention für eine Quarantäne-Mail ohne lesbare EML ist im Vertrag nicht
  festgelegt ("topReceived null" gilt laut G3, die Tests mit EML-loser Mail erwarten aber nun kein Löschen mehr). Der Test
  `Quarantine_mail_without_a_log_entry...` und die Matrix decken das sichere Verhalten (kein Löschen ohne Eintrag) ab.

## Neu in author.1 / verify.2
Fälle 34-46 (siehe `test-plan.md`), `MessageKeyTests` direkt auf Whitespace-Normalisierung von `topReceived` (3 Fälle plus Whitespace-only = kein Received).
