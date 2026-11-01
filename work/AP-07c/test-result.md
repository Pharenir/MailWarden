# AP-07c - Testergebnis (verify.1)

Build `dotnet build MailWarden.sln -warnaserror` auf `feat/ap-07c-classifier-pipeline` (mit Test-Branch): 0 Fehler, 0 Warnungen.
Zwei Kompilierfehler im ersten Stand (`MailKind` Zugreifbarkeit, fehlendes `using MailWarden.Core.Sync`) und danach drei weitere
(Literal in `MessageKeyTests`, xUnit1026, xUnit2031) im Test-Code behoben.

```
Stufe:        S3
Auswahl nach: voll
Ergebnis:     1182 von 1183 Tests grün (Suite gesamt: 1183) - Core.Tests 404 von 404, Infrastructure.Tests 778 von 779
Fehlschläge:  ClassificationInputReaderTests.Garbage_bytes_give_null - Reader wirft FormatException statt null
Nicht gelaufen: nichts (Fälle 31-33 sind bewusst nicht automatisiert)
Laufzeit:     6,4 s (Historie: S3 ca. 4-5 s bei 964 Tests)
Abbruch:      -
Auffällig:    keine weiche Grenze überschritten; Historie nicht fortgeschrieben (Lauf rot)
```

## Fehlschlag
- **Test:** `MailWarden.Infrastructure.Tests.Classification.ClassificationInputReaderTests.Garbage_bytes_give_null`
- **Erwartet:** `ClassificationInputReader.ReadAsync` gibt `null` für Bytes, die keine Nachricht sind (D4: "Null when no single usable From address").
- **Tatsächlich:** `System.FormatException : Failed to parse message headers.` aus `MimeParser.ParseMessageAsync`
  (`ClassificationInputReader.cs:36`).
- **Einordnung:** (c) Anforderung unklar, möglicherweise (b). D4 nennt nur "kein brauchbares From -> null"; ob nicht parsbare Bytes
  eine Ausnahme sein dürfen, steht nicht da. Die Pass-Tests (Fall 22, Garbage-EML -> `ReadFailed`) sind grün, die Pass fängt die
  Ausnahme also ab. Frage an den Planer: soll der Reader selbst `null` liefern (Test bleibt) oder darf er werfen (Test auf
  `FormatException` ändern bzw. entfernen)?
- **Reproduktion:** `dotnet test tests\MailWarden.Infrastructure.Tests --filter "FullyQualifiedName~Garbage_bytes_give_null"`

## Schärfungen nach "Decisions author.0"
- `MovesEnqueued` und `RetentionDeletesEnqueued` zählen Mails: exakte Asserts (Fall 13, 25: 3 bzw. Mailzahl, Fall 30: 5, Retention: 1 bzw. 2).
- Leere/Whitespace-Message-ID = fehlend: neuer Theory-Test in `MessageKeyTests`.
- Log-Einträge bleiben beim Löschen eines Ordners: Test erweitert (`Deleting_a_folder_removes_its_marker_but_keeps_log_entries...`).
