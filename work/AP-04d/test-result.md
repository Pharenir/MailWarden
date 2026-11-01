# AP-04d test result (tester, branch `test/ap-04d-reading-theme`)

```
Stufe:        S3 (sanitizer and shell constructor changed; run before merge)
Auswahl nach: voll
Ergebnis:     2489 von 2489 Tests gruen (Core 536, Infrastructure 1573, Presentation 380)
Fehlschlaege: keine
Nicht gelaufen: nichts ausgelassen; Fall 13 ist manuell (Dennis)
Laufzeit:     15 s (Waechter), weiche Grenze nicht ueberschritten
Abbruch:      -
Auffaellig:   siehe unten
```

Build `dotnet build MailWarden.sln -warnaserror`: 0 Warnungen, 0 Fehler.

## New tests
- `ReadingDocumentThemeTests` (Infrastructure): 59 test cases.
- `ReadingPaneThemeTests` (Presentation): 24 test cases.
- Adapted: `ShellHarness` and `ReadingPaneBuildGuardTests.BlockingBuilder` (documentBuilder now takes `ReadingTheme`); not changed because of a red test.

## Findings
No red test. Observations (no defect against the requirement, for the reviewer):
1. The text-only path of the sanitizer is reached by a large number of `style` attributes (20 000 of `margin:0` is enough); input size alone (3 MB of `<p>`) or 300 attributes on one tag does not. In that path `HasOwnColors = true`, as required.
2. When the sanitizer drops style data because of a limit (300 attributes on one tag, 100 000 declarations in one `style`), `HasOwnColors` is `false` for what is left (the colour is gone with it). Consistent with D4 (decision on the sanitized HTML), but "unclear counts as own colours" could be read to cover it. Not asserted.
3. Case 5a, `<style>` removal and the "blocked url / removed img still counts" decision are asserted as written in the requirement.
