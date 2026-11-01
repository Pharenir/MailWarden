# AP-04a – Testergebnis (verify.6, nach fix.6)

```
Stufe:        S3
Auswahl nach: voll
Ergebnis:     1732 von 1732 Tests grün (Core 430, Infrastructure 958, Presentation 344)
Fehlschläge:  –
Nicht gelaufen: Fälle 42-48 (manuell, Dennis)
Laufzeit:     14,6 s
Abbruch:      –
Auffällig:    –
```

Stand: `feat/ap-04a-ui-read` nach Merge von `test/ap-04a-ui-read`, Build `-warnaserror` grün. Ergebnis: **PASS**.

## Verlauf
Lauf 1: 1730 grün, 2 rot, beide Einordnung (a), eigener Testfehler.
- `ShellViewModelTests.Read_model_interface_has_only_the_five_read_members` (Fall 41): erwartete die fünf alten Lese-Member; `GetFolderRoleAsync` ist ein weiteres reines Lese-Member (Fall 103). Erwartung auf sechs Lese-Member erweitert.
- `MailHtmlSanitizerControlTests.Embedded_images_stay_with_the_default` (Fall 97): erwartete `data:image` in einem CSS-`url()` im Default. D3 entfernt jedes nicht-http(s)-`url()` ohnehin; die Zeile war falsch. Entfernt, die Bilder (`cid`/`data:` in `img`) und die Zählung bleiben geprüft.
Lauf 2: alles grün.
