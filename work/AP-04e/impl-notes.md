# AP-04e – implementation notes (implement.1)

STEP-RESULT: implemented in full. `dotnet build MailWarden.sln -warnaserror` from the worktree: 0 warnings, 0 errors. The GUI
was never started by the builder. No test stage was run in this step. Later rounds: "Tested / untested" below and `requirement.md`.

Branch `feat/ap-04e-nightshade-theme`. No new package, no csproj change.

## Deviations from the contract
None in the signatures. Three additions the contract leaves open:

- `ReadingDocumentBuilder.Build`, `BuildFromPlainText` and `PageBackground` now throw `ArgumentOutOfRangeException` for an
  unknown `ReadingTheme`. Before, an unknown value silently became Light. A third value is what makes a silent fallback
  dangerous, and the project rule is fail fast. The view model only ever passes values from `AppThemes.ToReadingTheme`.
- `NightshadeTheme.xaml` holds one key the table does not name: `NightshadeWindowBackgroundBrush` (`#1A1626`). WinUI has no key
  for the window colour, and the decision says the title bar reads the palette, not a third copy.
- `AppThemes.ToReadingTheme` does not throw for an unknown `AppTheme` (E4 "otherwise as today"), `DisplayName` does (contract).

## Additions to the role table of the spike
All in `tools\make_nightshade_theme.py`; none is in the spike, so none was seen on screen.

- `ControlElevationBorderBrush` → `#3E355C`, `TextControlElevationBorderBrush` → `#463C66`: originals are white-alpha gradients,
  and `inputBorder` of the palette would otherwise appear nowhere on the search box.
- `TextControlElevationBorderFocusedBrush` → `#FF8A3D`: the original draws the **accent colour of Windows** under a focused
  input. Solid means the whole border of the focused search box is orange, not only its lower edge.
- `ListViewItemForegroundSelected*` and `TreeViewItemForegroundSelected*` → `#FFFFFF` (the original pressed state in the tree is
  `textMuted`, under 4:1 on `#4A3B7A`).
- `ToolTipBackground` → `#231E33`: its original aliases a base outside the table.

## Non-SolidColorBrush originals
`AcrylicInAppFillColorDefaultBrush` and `AcrylicBackgroundFillColorDefaultBrush` (AcrylicBrush) and the gradients above can take a
`SolidColorBrush`: the keys are only used as `{ThemeResource}` on properties of type `Brush`. Seen at run time in the spike for
the acrylic ones (`FlyoutPresenter background=#FF231E33`); only reasoned for the gradients.

Left as they are, on purpose: `AccentControlElevationBorderBrush`, `CircleElevationBorderBrush` (neutral alpha), the focus stroke
(white, it is the keyboard cue), and aliases to `Color` keys (`ToggleSwitchFillOffPointerOver` and similar: white alpha over our
fill, which still reads). `ComboBoxItemBackgroundSelected` stays `#30294A` as in the spike, not the selection colour.

## How the start avoids another theme
`MainWindow` calls `WindowThemeChrome.PrepareResources` **before** `InitializeComponent`. The first idea, no bounce at the start, was
wrong: the planner measured text outside a control staying white until the first change of the theme (fix.2). So the first `Apply`
with the palette bounces too, also on a tree that is not live yet; measured in the spike, where `Apply` runs before `Content = root`.

## The bounce and the reading pane
As in the decision, no guard flag: `ApplyTheme` calls `themeChrome.Apply` and then `ApplyReadingTheme`, which asks
`AppThemes.ToReadingTheme(ViewModel.Theme, RootGrid.ActualTheme == ElementTheme.Dark)`. The events of the bounce come later, by
then `ActualTheme` is the target and `ApplyThemeAsync` does nothing. Measured in the spike for Dark, Light and `Default` with Windows
in dark mode (`real-*`). `Default` with Windows in light mode is not measured; Light would be the target there anyway.

## High contrast
Removed in fix.1 by the planner: subscribing to `AccessibilitySettings.HighContrastChanged` throws `0x80070490` in the unpackaged
app, so every start would have failed. `HighContrast` is read on each `Apply`; a change of the Windows setting counts from the next
theme change or start. Until then a solid background can stay under system text colours (secure.1 N2, accepted, manual case 29).

## Verified without starting the window
- The compiled dictionary exists: `bin\...\Themes\NightshadeTheme.xbf`, and `MailWarden.App.pri` holds
  `ms-resource://MailWarden.App/Files/Themes/NightshadeTheme.xbf` (makepri dump), next to `Files/MainWindow.xbf`.
- XML check of the generated file: only the theme dictionary `Dark`, 337 entries, no duplicate key, every `ResourceKey` defined
  earlier, every key of the required table with its colour. The generator stops on a missing or differing required key.
- Contrast computed: text on surface 13.4, link on surface 6.9, textMuted on window 7.1, selection 9.6, accent text 7.5, text on
  the warning banner 10.8.
- Self-check of existing test classes, no proof: `ReadingDocumentBuilder` 17 of 17 green. `UiSettingsFile` 37 of 38; the one red is
  `UiSettingsFileTests.Defaults_are_side_by_side_system_280_and_420`, which expects the old default (E2 changes it).

## Tested / untested
- Tested: nothing by a test stage.
- Only built here; the planner then ran the real `WindowThemeChrome` and the real dictionary in the spike (planner, fix.1): the
  read back of `palette.ThemeDictionaries["Dark"]["..."]` works, and so does every direction including System. `MainWindow` itself
  has never run: the first run with mails is Dennis's first start.
- `TextBlock` with `IsTextSelectionEnabled` (mail header lines) selects in `#0078D4` in every theme, measured in fix.2. No key of the
  palette decides it; `TextControlSelectionHighlightColor` covers the text boxes. Dennis decides in case 26b.

## Questions
None that block. Regeneration: `python tools\make_nightshade_theme.py` from the project folder; `WINUI_PACKAGE_VERSION` at
`tools\make_nightshade_theme.py:18` must follow the package of the app project.
