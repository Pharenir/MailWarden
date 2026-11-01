# AP-04d – implementation notes (implement.1)

STEP-RESULT: implemented except one row that cannot hold together with the rest: the `<style>` trigger of D4/case 5, see
"Conflict in the requirement". Builds of `MailWarden.Infrastructure`, `MailWarden.Presentation` and `MailWarden.App` with
`-warnaserror`: 0 errors, 0 warnings. The GUI was never started, no test suite was run.

Branch `feat/ap-04d-reading-theme`. No new package. Sanitizer rules and Content-Security-Policy unchanged: a dump of every
document of 34 artificial mails in 4 image approvals, made before the first change and after the last, is byte-identical.

## Conflict in the requirement (open, needs a decision)
D4 and D2 decide on the sanitized HTML; case 5 expects a `<style>` rule to give the light sheet. The sanitized HTML never
holds a `<style>`: `HtmlNestingLimiter` removes the element before parsing and `MailHtmlSanitizer.RemovedElements` removes
what is left. So `<style>p{color:red}</style><p>x</p>` comes out as `<p>x</p>` and gets the **dark** style.

Implemented as written in D2/D4: `OwnColorDetector` treats a `style` element as own colors, but it cannot occur today.
Readable either way: the rule is gone, the text gets our colors.

To satisfy case 5 literally, the limiter would have to report what it removes. That is a look at raw, hostile CSS before
the parser, it would turn most Word and Outlook mails (`a:link{color:...}` in their `<style>`) into light sheets, and it
contradicts "HTML without color information after sanitising" in D2. Not done without a decision.

## Where the check runs and how the parser budget is kept
- `OwnColorDetector.HoldsOwnColors(body)` is called inside `MailHtmlSanitizer.Sanitize`, on the document that is already
  parsed. It parses no HTML of its own, so no second parser run exists that would need protection: the HTML has passed
  `HtmlNestingLimiter` and `ParseWithinBudget`, `PruneElements` has limited the depth to 256, and everything runs on the
  build thread with the large stack.
- It reads the style attributes with `GetStyle()`, as `ApplyStyleRules` does for the same elements right after it.
- It only reads; `Html`, the blocked count and `WasReduced` do not depend on it. Only the bool leaves.
- Probe: alone 4 ms for 6,000 styled tags, against 1,646 ms for the whole `Sanitize`.
- When the parser budget or the output budget ends in the encoded text (`ToTextOnly`), `HasOwnColors` is true: "unparsable
  counts as own colors", although the text itself would be readable on dark. The same goes for a missing body.

## Interpretations of D4
| Point | Decision | Why |
|---|---|---|
| Position | After the allow-lists, **before** the URL rules | A blocked `background-image: url(https://...)` is removed by the URL rules. After them it would be dark, which fails case 5, and it would change to light once the user approves the images |
| Blocked or removed `img` | Counts, also `<img src="javascript:...">` and `cid:` unknown, which the URL rules remove | Consequence of the position; errs towards light |
| `background-*` | Every declaration starting with `background` counts, also `background-position` alone | AngleSharp can return a shorthand as longhands, so `background` cannot be told apart reliably |
| `border-color`, `outline-color` and similar | Do not count | Not in D4, and they do not affect readability of the text. `-webkit-text-fill-color` does not pass the allow-list |
| Attributes of `<body>` and `<html>` | Do not count | They never reach the sanitized HTML, the serializer writes the children of `body`; `<body bgcolor="#000" style="color:#fff">` is dark and readable |
| `<td bgcolor>` without a table | Does not count | The parser drops the tag, the attribute is not in the output |
| `style=""` | Does not count | Holds nothing |
| Style text without any readable declaration | Counts | "Unclear"; the package normally removes such a value first (`style="foo"` is gone before the check) |

## Behaviour worth knowing
- The light style is byte-identical to the former `BaseStyle` (D5); the sheet of D3 is exactly this style.
- Dark style: page `#202020`, text `#f0f0f0`, links `#8ab4f8`, `html{color-scheme:dark;}` for the scrollbars.
- Markers for the document: the dark style contains `color-scheme:dark`, the light one `html,body{background:#ffffff;color:#1a1a1a;}`.
- `ReadingDocumentBuilder.PageBackground` is the only source of the color of the WebView2 default background, so page and
  browser cannot drift apart (D6). It depends on the theme only; a light sheet in the dark theme loads on dark, then turns white.
- A theme change repeats the shown document with `currentApproval` (Junk stays without embedded images) and without a read.
  The old document stays until the new one is there; the state stays `Ready`.
- A colored mail gives the same HTML in both themes, so `DocumentHtml` raises no change and the browser does not navigate.
- A mail without colors does navigate, so its **scroll position jumps to the top** on a theme change. Scripts are off; keeping
  the position would need a script or a change of the browser configuration, which the requirement does not ask for.
- A document shown as plain text after a build over the limit stays plain text on a theme change, without a second long build.
- `LoadRemoteImagesAsync` became the shared `RebuildAsync`; the log source of its failures stays
  `ReadingPaneViewModel.LoadRemoteImagesAsync`, a theme rebuild logs `ReadingPaneViewModel.ApplyThemeAsync`.

## Interface changes
Public:
- `enum ReadingTheme { Light = 0, Dark = 1 }` (`MailWarden.Infrastructure.Mime`), new
- `SanitizedHtml(string Html, int BlockedRemoteResourceCount, bool WasReduced = false, bool HasOwnColors = true)`
- `ReadingDocumentBuilder.Build(MailContent content, bool allowRemoteImages, bool allowEmbeddedImages = true, ReadingTheme theme = ReadingTheme.Light)`
- `ReadingDocumentBuilder.BuildFromPlainText(MailContent content, ReadingTheme theme = ReadingTheme.Light)`
- `ReadingDocumentBuilder.PageBackground(ReadingTheme theme)` returns `(byte Red, byte Green, byte Blue)`, new
- `ReadingPaneViewModel.ApplyThemeAsync(ReadingTheme newTheme)` returns `Task`, new
- `ShellViewModel(..., Func<MailContent, bool, bool, ReadingTheme, ReadingDocument>? documentBuilder = null)`: **breaking**, one more parameter

Internal: `OwnColorDetector.HoldsOwnColors(IElement body)`, new; `ReadingPaneViewModel` constructor with the same `Func`.
App only: `MailWebViewHost.SetDefaultBackground(Windows.UI.Color color)`, new.

The solution build stops at `tests\MailWarden.Presentation.Tests\TestSupport\ShellHarness.cs` (CS1503, seen in the compiler
output only, the file was not read), which passes a three-parameter builder to `ShellViewModel`. Further errors may follow
once that compiles. The three `src` projects build with `-warnaserror`.

## Plan
`PLAN.md`: row 8 of AP-04a and the dark-mode risk describe the new rule; *Getestet* of row 8 unchanged. There is no table for
AP-04d in `PLAN.md`, its Sollverhalten lives in `work\AP-04d\requirement.md`, therefore the state per row is here:

| # | State | Where |
|---|---|---|
| D1 | Done, the live change with "Design System" is unverified (window) | `MainWindow.xaml.cs` (`ActualTheme`, `ActualThemeChanged`), `ReadingPaneViewModel.ApplyThemeAsync` |
| D2 | Done | `ReadingDocumentBuilder` (`DarkStyle`) |
| D3 | Done | `ReadingDocumentBuilder` (`LightStyle`) |
| D4 | Done, except the `<style>` conflict above | `OwnColorDetector`, `MailHtmlSanitizer` |
| D5 | Done, byte comparison | `ReadingDocumentBuilder` |
| D6 | Done in code, the absence of a flash is unverified (window) | `ReadingDocumentBuilder.PageBackground`, `MailWebViewHost.SetDefaultBackground` |
| D7 | Done, byte comparison; approval and state kept | `ReadingPaneViewModel` |
| D8 | Done | `ReadingTheme` in Infrastructure, `MainWindow.xaml.cs` only reports |

## Left to others
- `README.md` line 41 still says mail content is always on a light background; documenter.
- Merge with AP-04b: the edits in `MailWebViewHost.cs`, `MainWindow.xaml.cs` and `ReadingPaneViewModel.cs` avoid the lines
  it touches; the merge itself was not tried.
