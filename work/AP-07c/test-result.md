# AP-07c - Testergebnis (verify.4, nach fix.4)

Build `dotnet build MailWarden.sln -warnaserror` auf `feat/ap-07c-classifier-pipeline` (mit Test-Branch): 0 Fehler.

```
Stufe:        S3
Auswahl nach: voll
Ergebnis:     1276 von 1276 Tests grün (Suite gesamt: 1276) - Core.Tests 430 von 430, Infrastructure.Tests 846 von 846
Fehlschläge:  -
Nicht gelaufen: nichts (Fälle 31-33 sind bewusst nicht automatisiert)
Laufzeit:     6,8 s (Historie: verify.3 7,4 s bei 1259 Tests)
Abbruch:      -
Auffällig:    keine weiche Grenze überschritten; Fall 55 (1023 und 5000 Ebenen) ohne Stapelüberlauf
```

## Inhalt
- fix.4: Fälle 52-54 (werfendes Signal über `signals`: `EvaluationFailed`, nichts verschoben, kein Ausnahmetext, bekannter Schlüssel, Positivkontrolle), Fall 55 (verschachtelte MIME-Teile, `NestedMimeReaderTests`).
- Fälle 1-30 (author.0), 34-46 (fix.2), 47-51 (fix.3) automatisiert; 31-33 bewusst nicht.
- fix.3: Mails ohne brauchbaren Absender gehen aus der Inbox nach Junk (Fälle 22, 42, 47), `RouteWithoutSender` (48), `ReadTopReceivedAsync` (49),
  Retention mit beschädigtem Body-Part bei lesbaren Headern (50), nicht-ASCII-Trenner im Authentication-Results-Wert (51, über den Klassifizierer).
- Fall 50: festgehalten wird nur, dass der Schlüssel stimmt und die Mail bei Fälligkeit gelöscht wird (eine fehlende EML wird nie gelöscht).
  Ob der frühere Vollparse an diesen Fixtures scheiterte, ist nicht Teil des Vertrags.
- Der Ausnahmezweig von H2 hat keinen Auslöser und wird nicht direkt getestet (Prüfung im Review).

## Offen (nicht automatisierbar)
Fälle 32 und 33 (echte Daten, echter Server); `Act` bleibt auf echten Konten aus, bis Fall 33 zeigt, dass das oberste `Received` ein MOVE übersteht.
