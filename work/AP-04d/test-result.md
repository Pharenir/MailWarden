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

## verify.2 (fix.1 cases 14, 14a, 15, 16; branch merged with `feat/ap-04d-reading-theme` 501caf7)
```
Stufe:        S3
Auswahl nach: voll
Ergebnis:     2508 von 2508 Tests gruen (Core 536, Infrastructure 1590, Presentation 382)
Fehlschlaege: keine
Laufzeit:     15 s, keine weiche Grenze ueberschritten
```
Build `-warnaserror`: 0 Warnungen, 0 Fehler.

New: Infrastructure +17 (case 14: 10 declarations incl. `border-image-source`, `border-image-slice`, upper case `FILTER : invert(1)`; case 14a; case 15: 5 declarations incl. `border-color`, `border-radius`; `mark a` rule in the dark style only). Presentation +2 (case 16: document stays, state `Ready`, no notice, no exception, log holds `InvalidOperationException` and not the exception text, next mail and next theme changes use the reported theme; failure while loading images still ends in `Error`).

Changed tests: none. No earlier test expected the old behaviour (failed theme rebuild ending in `Error`, an F1 declaration being dark); `Failing_builder_during_a_theme_change_does_not_throw` only asserts that nothing is thrown and stays valid.

## verify.3 (list-style/content/box-shadow values; merged with `feat/ap-04d-reading-theme`)
S3 green: 2516 of 2516 (Core 536, Infrastructure 1598, Presentation 382), 15.6 s, build `-warnaserror` clean, no red test, no changed test.
New (+8, Infrastructure): dark for `list-style:none`, `list-style:disc`, `list-style-image:none`, `content:normal`; light for `list-style-image:url(...)`, `list-style:square url(...)`, `content:url(...)` and the accepted no-op `box-shadow:none`.

## verify.4 (G1-G3, cases 4 new, 17-20; merged with `feat/ap-04d-reading-theme`)
S3 green: 2523 of 2523 (Core 536, Infrastructure 1604, Presentation 383), 15.1 s, build `-warnaserror` clean, no red test.
Veil marker: a rule in the `<style>` of the page holding `html::after`, `rgba(0,0,0,.25)`, `pointer-events:none` (text in the body does not count).

Changed tests (contract change G1/G3):
- Infrastructure `Html_with_own_colours_gives_the_same_document_in_both_themes` replaced by `Html_with_own_colours_gets_the_veil_in_the_dark_theme_and_nothing_in_the_light_theme` (light = call without theme, dark = light sheet plus veil, same body).
- Infrastructure `A_hostile_style_attribute_keeps_the_light_document_unchanged_...`: the dark document is no longer equal to the light one; now light stays byte-identical, dark is light sheet plus veil with the same body.
- Infrastructure helpers: `IsLight` = plain light theme (no veil), new `IsVeiledLight`; `IsDark` now also requires no veil. Cases 4, 5, 5 (approval), 7, 14 and the text-only test now assert the veil (case 17).
- Presentation `Mail_with_own_colours_gives_the_same_html_in_both_themes_and_no_document_change` replaced by `Mail_with_own_colours_gets_the_veil_on_a_theme_change_without_reading_again` (case 20: `DocumentHtml` changes and is announced, no read, back to light is byte-identical).
New: case 18 (no veil for plain text and HTML without colours), case 19 (light never has a veil, 4 inputs), `The_mail_cannot_influence_the_veil` (`html::after` in text or in a removed `<style>` changes nothing of the page style), `Mail_with_own_colours_keeps_the_image_approval_on_a_theme_change`. The CSP test (case 9, includes a mail with colours) stays unchanged and green.
