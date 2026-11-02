# AP-04e test plan (author.0, written against requirement.md, not run)

Status: tests written by reading only. Nothing compiled or run yet (the builder's types are missing on this branch).
All tests carry `Category=feature:ui`; per-test timeout `TestTimeouts.PerTestMilliseconds` as in the neighbours.
Mapping: `tests\test-map.json` already covers `src/MailWarden.Presentation/**` and `src/MailWarden.App/**` (feature:ui) and
`src/MailWarden.Infrastructure/Mime/**`; no entry added. `tools\make_nightshade_theme.py` is not mapped (not runtime).

## Cases -> tests
| Case | File | Tests |
|---|---|---|
| 1, 2, 3, 4, 5 | `Presentation.Tests\UiSettingsFileTests.cs` | `Nightshade_is_the_default_theme`, `Missing_file_gives_nightshade`, `File_without_the_theme_key_gives_nightshade`, `A_stored_theme_stays_as_stored`, `Nightshade_round_trips_and_is_written_as_nightshade`, `Every_theme_is_written_with_its_spelling`, `Unknown_theme_values_are_rejected_naming_the_key_but_not_the_value`; extended `Each_theme_value_is_read` and the invalid-content theory |
| 6, 7, 8 | `Presentation.Tests\AppThemesTests.cs` | enum values, `ChoiceOrder` (order, once each), `DisplayName` (Nightshade, filled and distinct, unknown values throw), `UiText.ThemeNightshade` (by reflection), `ToReadingTheme` (Nightshade both ways, other themes follow the window) |
| 9, 10 | `Infrastructure.Tests\Mime\NightshadeReadingDocumentTests.cs` | plain text (Build and BuildFromPlainText), html without colours: page, link rule, selection rule, mark rule, neutral content theory |
| 11 | same | trigger theory (AP-04d cases 5, 7, 14), approval does not move the style, reduced-to-text mail, paint-nothing values stay dark |
| 12 | same | Light and Dark markers kept, no palette colour in Light or Dark, `#8ab4f8` and the mark rule stay, Light equals the call without theme |
| 13 | same | CSP, blocked count, `WasReduced` equal across the three themes in all 4 approval combinations; remote images stay blocked |
| 14 | same | hostile mails: body and meta identical across the themes, one style element, none of the attacker colours; page style depends only on yes/no of own colours |
| 15 | same | `PageBackground(Nightshade)` = (0x23,0x1E,0x33); Light and Dark unchanged; enum values |
| 16, 17, 18 | `Presentation.Tests\ReadingPaneThemeTests.cs` (appended) | Dark -> Nightshade (new document, no read, no controller call, state, subject, change event), plain text, selection, approval kept, blocked without approval, own-colour mail, next mail, every direction (6) and approval in 3 directions, round trip, same theme twice (no build, no event), starting theme, Nightshade-Light-Nightshade, race Dark rebuild vs Nightshade |
| 19, 20, 21 | `Infrastructure.Tests\Mime\NightshadeThemeXamlTests.cs` | XML read of `src\MailWarden.App\Themes\NightshadeTheme.xaml` (repo root found via `MailWarden.sln`): only dictionary `Dark`, no duplicate key, every `ResourceKey` earlier, 18 required keys with colour (aliases followed, case-insensitive, ARGB `#FFxxxxxx` accepted), alias count, reading pane equals XAML keys, `PageBackground` equals XAML, contrast from the style and from the XAML |
| 22 | `Presentation.Tests\ShellViewModelTests.cs` (appended) | Nightshade chosen: event and save; default start; stored themes applied |

## Changes to existing tests
- `UiSettingsFileTests.Defaults_are_side_by_side_system_280_and_420` renamed to `..._nightshade_...`, expects `AppTheme.Nightshade` (E2).
- Other tests reviewed: `ShellViewModelTests` (Dark/Light assignments and `UiSettings.Default.Theme` read dynamically), `WindowSettingsTests` (explicit themes), `ReadOnSelectTests`, `ShellHarness` and `FakeUiSettingsStore` (use `UiSettings.Default`) do not depend on `System` as default. No other change needed.
- Light/Dark tests (`ReadingDocumentThemeTests`, `ReadingPaneThemeTests` old part) untouched.
  Note: the old helper `IsDark(reading)` in `ReadingPaneThemeTests` also matches the Nightshade page (dark scheme, no light marker); the new tests use `IsPlainDark` (needs `#202020`) and `IsNightshade` (needs `#231e33`).

## Not covered (by design)
Cases 23-29 (manual). E12-E15 (window chrome) have no automated test: the window runs in no test. E16 (layering) is checked by the build (project references); no test.
Case 12 byte-equality: no golden text of the current `main` output is held by the tests (reading `src` was forbidden). The tests check the
markers (`color-scheme:dark`, `background:#202020`, light marker, `#8ab4f8`), equality to the call without theme for Light, and absence of palette colours.
Suggestion for the first run: capture the Light and Dark documents of a few mails from `main` once and add them as fixed text.

## Assumptions about the CSS format (to confirm at first run)
Page style in the form of the existing markers: `html,body{background:#...;color:#...;}` without spaces (a rule whose selector contains `body`),
link rule: one rule with both `a:link` and `a:visited` in the selector, selection rule: selector contains `::selection`, veil: `html::after`,
`rgba(0,0,0,.25)`, `pointer-events:none`, mark rule: selector contains `mark a`. Colours compared without case.
