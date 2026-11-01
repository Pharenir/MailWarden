# AP-04a – Testergebnis (verify.1)

```
Stufe:        S3
Auswahl nach: voll
Ergebnis:     1349 von 1349 Tests grün (Core 393, Infrastructure 793, Presentation 163)
Fehlschläge:  –
Nicht gelaufen: Fälle 42-48 (manuell, Dennis)
Laufzeit:     8,2 s (keine Historie für diese Suite)
Abbruch:      –
Auffällig:    Very_deep_nesting (5000 Ebenen, offen und geschlossen) lief durch, Testhost stabil.
```

Stand: `feat/ap-04a-ui-read` nach Merge von `test/ap-04a-ui-read` (Commit 9164d29), Build `-warnaserror` grün.

## Erster Lauf (vor Korrektur): 1334 grün, 15 rot, alle Einordnung (a) eigener Testfehler
- 12x Sanitizer-Farbtests (`Dangerous_inline_style_...`, `Harmless_inline_style_stays`, `Remote_css_url_...`): Farbe wurde zu `rgba(255, 0, 0, 1)` normalisiert, Test suchte "red". Fix: `IsRed` akzeptiert alle Schreibweisen.
- `ReadingDocumentBuilderTests.Html_with_images_blocked_...`: Test erwartete das Attribut `id` am `<p>`; der Sanitizer entfernt `id` (kein Vertragsbestandteil). Fix: Element statt id geprüft.
- `ReadingPaneViewModelTests.Cancellation_...`: Test warf eine OCE ohne Token des Aufrufers; Entscheidung des Planers gilt nur für den Token des Aufrufers. Fix: Token wird im Fake abgebrochen, erwartet wird propagierte OCE, Zustand nicht Error, kein Log.
Zweiter Lauf: alles grün, keine weiteren Versuche nötig.

## Erneute Prüfung der Planer-Festlegungen
Tests passen zu allen Punkten (MailContentException nur bei nicht lesbarem Stream, UiSettingsFile-Pfad = Datei, Setter ohne Await, Abbruch, background-image, tiefe Verschachtelung, StatusText/Banner nur grob).
