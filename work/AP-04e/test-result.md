# AP-04e test result (verify.1)

**PASS**

## Report
```
Stufe:        S3 (S2 zuerst; angehoben, weil Auswahl Category=feature:ui 1347 von 2964 Tests = 45 % > Schwelle 40 %)
Auswahl nach: Feature-Tags (S2), voll (S3)
Ergebnis:     S2: 1347 von 1347 gruen (Core 70, Presentation 629, Infrastructure 648); S3: 2964 von 2964 gruen (Core 536, Presentation 629, Infrastructure 1799)
Fehlschlaege: keine
Nicht gelaufen: nichts (keine als slow markierten Tests ausgeschlossen)
Laufzeit:     S2 24,4 s, S3 27,6 s (keine Historie, keine Gesamtgrenze)
Abbruch:      -
Auffaellig:   keine
```
Test counts are non-zero for every project. Build with `-warnaserror` before the run: 0 warnings, 0 errors.
"Alle Tests gruen" is allowed: S3 green on this branch.

## Added in this step
- `tests\MailWarden.Infrastructure.Tests\Mime\ReadingDocumentGoldenTests.cs` (case 12, E8): Theory with the 18 golden SHA-256 values
  from `golden-main.md` (Light and Dark, Build with allowRemoteImages false/true and BuildFromPlainText; mails plain,
  html-without-colors, html-with-colors, headers without Cc and Date). All 18 equal: Light and Dark are byte-identical to `main`.
- Same file: unknown `ReadingTheme` (99) throws `ArgumentOutOfRangeException` in `Build`, `BuildFromPlainText`, `PageBackground`.
- `NightshadeWindowBackgroundBrush`: extra XAML keys were tolerated by the existing XAML tests (all green); no change needed.

## Findings
None. No test had to be corrected; no CSS format assumption (cases 9/10) failed.

## Not covered
Cases 23-29 (manual, Dennis), E12-E15 (window chrome, runs in no test), E16 (layering, covered by the build).
