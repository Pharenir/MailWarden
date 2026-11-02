---
id: AP-04e
slug: nightshade-theme
title: Nightshade theme as the default
test-scope: S2 during the rounds, S3 before the merge
test-filter: Category=feature:ui
---

# AP-04e: Nightshade theme

Wish of Dennis (2026-11-01): next to Light, Dark and System a theme Nightshade, and it is the default. Whoever has a theme stored keeps it. The colours come from the Nightshade project, not from the icon.

Branches: `feat/ap-04e-nightshade-theme` (builder), `test/ap-04e-nightshade-theme` (tester), both from the commit holding this file. Security review required: HTML output, WebView2 configuration.

## Palette
Source: `K:\Projekte\Windows-Tools\nightshade\src\Nightshade.Core\themes\nightshade.json`.

| Role | Colour | Role | Colour |
|---|---|---|---|
| window, title bar | `#1A1626` | input | `#17131F` |
| surface | `#231E33` | inputBorder | `#463C66` |
| surfaceHover | `#30294A` | selection | `#4A3B7A` |
| border | `#3E355C` | selectionText | `#FFFFFF` |
| text | `#ECE8F7` | error | `#FF8A8A` |
| textMuted | `#A99FC6` | warning | `#FFD166` |
| accent | `#FF8A3D` | buttonTop (used for plain buttons) | `#2E2744` |
| accentHover | `#FFA060` | buttonBottom (used for pressed) | `#2A2340` |
| accentText | `#1A1626` | | |

Contrast, computed by the planner: text on surface 13.4:1, link (accent) on surface 6.9:1, text on window 14.7:1, textMuted on window 7.1:1, selectionText on selection 9.6:1, accentText on accent 7.5:1.

## What the spike found (planner, 2026-11-02)
Spike in `K:\Projekte\Windows-Tools\MailWarden\_scratch\theme-spike` (not in the repo): a self-closing WinUI window with the same package versions and the controls MailWarden uses. Read `make_dictionaries.py` and `SpikeWindow.cs` there; the pictures are in its `out\`.

- Overriding only the base brushes (`ControlFillColorDefaultBrush` and so on) changes nothing visible. `generic.xaml` aliases them as `<StaticResource x:Key="ButtonBackground" ResourceKey="ControlFillColorDefaultBrush"/>`, and that alias is resolved inside its own dictionary. **Base brushes alone do not work.**
- Base brushes **plus** the aliases of the controls in use (255 aliases, 46 base brushes, 15 direct values in the spike) in a merged dictionary at application level work for: Button, DropDownButton, ComboBox, AutoSuggestBox, ToggleSwitch, InfoBar, ListView, TreeView, FlyoutPresenter, MenuFlyoutPresenter, ContentDialog.
- The dictionary loads through `new ResourceDictionary { Source = new Uri("ms-appx:///...xaml") }` in the unpackaged build. The keyed theme dictionary is `Dark`; `Default` behaves the same.
- **Live switching works:** add or remove the merged dictionary, then set `RootGrid.RequestedTheme = Light` and straight after the target theme. No frame is drawn in between (0 frames measured, 9 to 14 ms). Without this bounce the resources do not resolve again. Removing the dictionary and bouncing returns to the plain dark look.
- `ActualThemeChanged` fires twice per bounce, **after** the call, not inside it. `ActualTheme` was `Dark` right after the bounce.
- `DefaultTextForegroundThemeBrush` must be overridden too, or a `TextBlock` outside a control stays white.
- Backdrop and title bar switch live: `Window.SystemBackdrop = null` plus a solid `RootGrid.Background`, back with a new `MicaBackdrop` and `ClearValue`. `AppWindow.TitleBar` colours work; `null` restores the default.
- Known glitch, simulated with the application theme Light: the one button that holds the keyboard focus kept the dark default background at that moment. All others were right. Dennis runs Windows in dark mode. Not to be fixed in this AP unless the manual test shows more.
- A `MenuFlyout` built before the switch has the right background; its presenter `Foreground` stayed white in the spike (case 26 checks the items).

## Sollverhalten
| # | Behaviour |
|---|---|
| E1 | `AppTheme` gets the value `Nightshade = 3`; the settings file stores it as `"nightshade"`; a file with this value loads, an unknown value is still rejected |
| E2 | `UiSettings.Default.Theme` is `Nightshade`: without a settings file, and with a file without the key `theme`. Any stored theme (`system`, `light`, `dark`) stays as stored |
| E3 | The theme choice offers Nightshade, System, Hell, Dunkel, in this order, from `AppThemes.ChoiceOrder`; the name of the entry is `UiText.ThemeNightshade` = `Nightshade` |
| E4 | `ReadingTheme` gets the value `Nightshade = 2`. `AppThemes.ToReadingTheme(theme, isWindowDark)` gives `Nightshade` for `AppTheme.Nightshade` whatever the window reports; otherwise as today `Dark` or `Light` from `isWindowDark` |
| E5 | Mail without colours of its own and plain text, Nightshade: page `#231E33`, text `#ECE8F7`, links `#FF8A3D` (`a:link`, `a:visited`), `color-scheme:dark`, text selection `#4A3B7A` with `#FFFFFF`. No veil. Links in `<mark>` as in the dark style (`#0b57d0`), because `mark` stays yellow |
| E6 | Mail with colours of its own (`HasOwnColors`), Nightshade: the same light sheet under the same veil as in the dark theme, byte for byte the same style |
| E7 | `ReadingDocumentBuilder.PageBackground(ReadingTheme.Nightshade)` gives `#231E33`, so no other colour flashes while a mail loads |
| E8 | Light and Dark documents stay byte-identical to today, links in the dark style stay `#8ab4f8` |
| E9 | D7 of AP-04d holds: sanitizer and Content-Security-Policy unchanged; the theme only selects a fixed style from our own constants; of the mail, only the yes/no of `HasOwnColors` reaches the selection |
| E10 | Switching with an open mail works as in AP-04d: new document from the content already held, no read from the store, selection and image approval stay. Switching Dark ↔ Nightshade changes the reading theme although `ActualTheme` stays `Dark` |
| E11 | `src\MailWarden.App\Themes\NightshadeTheme.xaml` holds the palette as `ThemeDictionaries` with the key `Dark` and no other key. Every `ResourceKey` of a `StaticResource` is defined **earlier in the same dictionary** (otherwise the app fails at load). No key twice. The keys and colours in the table below are in it |
| E12 | With Nightshade the window shows the palette: controls, selection in list and tree, flyouts, menus, dialogs, the link hint, title bar `#1A1626`, no Mica; the window background is `#1A1626` |
| E13 | Switching works while the app runs, in every direction, without a restart and without a visible flash of another theme. Leaving Nightshade restores Mica, the title bar and the look of Light, Dark and System as they are on `main` |
| E14 | The app starts with Nightshade without flashing another theme first: the dictionary is merged and the chrome applied before the window is shown |
| E15 | Windows high contrast active: the palette, the solid window background and the title bar colours are not applied, because system text colours on our background could be unreadable. The theme stays stored and the reading pane still follows E5 |
| E16 | `Core` and `Infrastructure` stay free of WinUI; `Presentation` holds `AppThemes`, no WinUI reference; `MainWindow` and a helper in `App` only wire. Code-behind reports the reading theme from `AppThemes.ToReadingTheme` |

Required in `NightshadeTheme.xaml` (key → colour; `x:Key` exact, colour compared case-insensitively, a `<StaticResource>` alias to a key with the colour also counts):
| Key | Colour | Key | Colour |
|---|---|---|---|
| `TextFillColorPrimaryBrush` | `#ECE8F7` | `ListViewItemBackgroundSelected` | `#4A3B7A` |
| `DefaultTextForegroundThemeBrush` | `#ECE8F7` | `TreeViewItemBackgroundSelected` | `#4A3B7A` |
| `TextFillColorSecondaryBrush` | `#A99FC6` | `ListViewItemForegroundSelected` | `#FFFFFF` |
| `ButtonBackground` | `#2E2744` | `AccentFillColorDefaultBrush` | `#FF8A3D` |
| `ComboBoxBackground` | `#2E2744` | `TextOnAccentFillColorPrimaryBrush` | `#1A1626` |
| `TextControlBackground` | `#17131F` | `ToggleSwitchFillOn` | `#FF8A3D` |
| `MenuFlyoutPresenterBackground` | `#231E33` | `ContentDialogBackground` | `#231E33` |
| `FlyoutPresenterBackground` | `#231E33` | `SolidBackgroundFillColorBaseBrush` | `#231E33` |
| `SystemFillColorCautionBrush` | `#FFD166` | `SystemFillColorCriticalBrush` | `#FF8A8A` |

## Schnittstelle (contract)
```csharp
// src\MailWarden.Presentation\UiSettings.cs
public enum AppTheme { System = 0, Light = 1, Dark = 2, Nightshade = 3 }

// src\MailWarden.Presentation\AppThemes.cs
public static class AppThemes
{
    public static IReadOnlyList<AppTheme> ChoiceOrder { get; }   // Nightshade, System, Light, Dark
    public static string DisplayName(AppTheme theme);            // the UiText of the entry; unknown value: ArgumentOutOfRangeException
    public static ReadingTheme ToReadingTheme(AppTheme theme, bool isWindowDark);
}

// src\MailWarden.Presentation\UiText.cs
public static string ThemeNightshade => "Nightshade";

// src\MailWarden.Infrastructure\Mime\ReadingTheme.cs
public enum ReadingTheme { Light = 0, Dark = 1, Nightshade = 2 }

// Unchanged signatures, new value accepted:
ReadingDocumentBuilder.Build(MailContent, bool, bool, ReadingTheme);
ReadingDocumentBuilder.BuildFromPlainText(MailContent, ReadingTheme);
ReadingDocumentBuilder.PageBackground(ReadingTheme);
ReadingPaneViewModel.ApplyThemeAsync(ReadingTheme);
```
The settings key stays `theme`, the new value is `nightshade`.

## Decisions (planner)
- The bounce: only when the dictionary was added or removed. `MainWindow.ApplyReadingTheme` asks `AppThemes.ToReadingTheme(ViewModel.Theme, RootGrid.ActualTheme == ElementTheme.Dark)`, so the bounce cannot reach the reading pane as Light: the events come after the call, `ApplyThemeAsync` does nothing when the theme stays. If the builder finds otherwise, a guard flag in the code-behind, noted in `impl-notes.md`.
- The dictionary is one XAML file; its alias part comes from a small generator kept in the repo at `tools\make_nightshade_theme.py` (from the spike's script, with its role table), so a WinAppSDK update can regenerate it. The generated file is committed. The script reads `generic.xaml` from the NuGet folder and writes the file; it never runs during the build.
- Include the aliases of these control families: Button, DropDownButton, ComboBox, TextControl, AutoSuggestBox, ToggleSwitch, MenuFlyout, ListViewItem, TreeViewItem, TreeView, ScrollBar, InfoBar, ContentDialog, Flyout, ToolTip, RepeatButton, AccentButton.
- The window chrome (backdrop, solid background, title bar, dictionary, bounce) goes into one class in `App`, because it needs WinUI and is not testable. `MainWindow.xaml` keeps its default `MicaBackdrop`; the class removes it for Nightshade before the window is shown.
- Title bar colours read the same values as the palette: from the merged dictionary or a shared constant, not a third copy. The reading pane constants in `Infrastructure` and the XAML are necessarily two places; case 19 keeps them equal.
- A stored `"theme":"system"` stays: the settings file holds it even for someone who never chose a theme. Dennis switches once by hand. This follows "wer schon ein Thema gespeichert hat, behält es".

## Nicht-Ziele
Corner radius 6 and font size of the Nightshade project; the Frost theme; a general theme or skin format; button gradients (`buttonTop`/`buttonBottom` as flat colours); changing the link colour of the dark style (`#8ab4f8`, stays until Dennis decides); the focus glitch above; `scrollbar-color` in the reading page.

## Test cases
All with artificial data. The window itself runs in no test.

| # | Situation | Expected |
|---|---|---|
| 1 | `UiSettings.Default.Theme` | `Nightshade` |
| 2 | No settings file; file without `theme` | Load gives `Nightshade` |
| 3 | File with `"theme":"system"`, `"light"`, `"dark"`, `"nightshade"` | Load gives exactly that value |
| 4 | Save with `Nightshade`, then Load | Same value; the file holds `"nightshade"` |
| 5 | File with `"theme":"Nightshade"` (capital), `"theme":"frost"`, `"theme":3` | `UiSettingsException` naming the key, no value |
| 6 | `AppThemes.ChoiceOrder` | Exactly Nightshade, System, Light, Dark; every `AppTheme` value once |
| 7 | `DisplayName` for each; unknown enum value | The `UiText`, `Nightshade` for the new one; `ArgumentOutOfRangeException` |
| 8 | `ToReadingTheme` for all themes with `isWindowDark` true and false | Nightshade → Nightshade both times; for System, Light and Dark `isWindowDark` alone decides (true → Dark, false → Light), as on `main` where the window reports what it shows |
| 9 | Plain text, Nightshade | Page style with `#231E33`, `#ECE8F7`, `color-scheme:dark`, no veil; text encoded as before |
| 10 | HTML without colours, Nightshade | As 9, plus link rule `#FF8A3D` and selection rule `#4A3B7A`/`#FFFFFF` |
| 11 | HTML with `style="color:#000"` and every other trigger of AP-04d case 5, 7, 14, Nightshade | Light sheet with the veil; the style is identical to the dark theme's for the same mail, no `#231E33` |
| 12 | Same mail, Light and Dark, before and after | Byte-identical to the current output (Dark still `#202020`, `#8ab4f8`) |
| 13 | Content-Security-Policy in every combination of theme and image approval | Same text as Dark and Light |
| 14 | Hostile mail: `<style>` or an attribute reaching for our page style; Nightshade | Sanitizer output identical across all three themes; only the page style differs |
| 15 | `PageBackground(Nightshade)` | `(0x23, 0x1E, 0x33)` |
| 16 | Theme change Dark → Nightshade with an open mail | New document, no read from the store, selection and approval kept, state `Ready` |
| 17 | Nightshade → Dark, Nightshade → Light, Light → Nightshade with an open mail | As 16 |
| 18 | The same theme reported twice | No new build |
| 19 | Parse `NightshadeTheme.xaml` from the repo as XML; compare with the reading pane | Colours of case 9 and 10 equal the keys `SolidBackgroundFillColorBaseBrush`, `TextFillColorPrimaryBrush`, `AccentFillColorDefaultBrush` |
| 20 | The same XML: structure | Only the theme dictionary `Dark`; no duplicate `x:Key` in it; every `StaticResource/@ResourceKey` defined earlier in it; every key of the table above with its colour |
| 21 | Contrast computed from the colours in the XML and the style | Text on surface ≥ 7:1, links on surface ≥ 4.5:1, textMuted on window ≥ 4.5:1 |
| 22 | Shell view model, set `Theme = Nightshade` | Saved, change event as for the others |

Manual, Dennis (`work\manual-tests.md`):
| # | Situation | Expected |
|---|---|---|
| 23 | Fresh data folder or file without `theme`, start | Nightshade from the first frame, no flash |
| 24 | Your existing settings | Your stored theme stays; choose Nightshade once; it survives a restart |
| 25 | Switch through all four with a mail open: plain text, newsletter | All parts change at once, reading pane follows, no flash, title bar and Mica come back |
| 26 | With Nightshade: open the move menu, context menu, settings flyout, theme list, delete confirmation, failed actions, link hint, also after switching with these opened once before | Palette everywhere, text readable |
| 27 | Readable: selected row in list and tree, unread bold, disabled buttons, the warning banner, hover on rows | As stated |
| 28 | Newsletter in Nightshade | Dimmed light sheet, links clickable, hint shows the target, text selectable |
| 29 | Optional: Windows in light mode with Nightshade; high contrast | Palette holds apart from the known focus glitch; in high contrast system colours and readable text |

## Done when
S3 green on the merged branch, reviewer APPROVE, security-reviewer without Critical/High, documenter done, cases 23 to 28 by Dennis, CPM by Dennis.

## Decisions fix.1 (planner, 2026-11-02, before verify.1)
The planner ran the real `WindowThemeChrome` and the real `NightshadeTheme.xaml` in the spike (mode `THEME_SPIKE_REAL`). Subscribing to `AccessibilitySettings.HighContrastChanged` throws `COMException 0x80070490` in the unpackaged app, in the constructor, so every start would have failed. The live reaction went beyond E15 and is removed: `HighContrast` is read on each `Apply`, a change of the Windows setting counts from the next theme change or start. `new AccessibilitySettings()` and reading `HighContrast` work.
