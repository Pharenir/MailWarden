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

# fix.1

Decisions F1-F6 in `requirement.md`. Merged `test/ap-04d-reading-theme` first, without conflict; test files not read.
`dotnet build MailWarden.sln -warnaserror`: 0 errors, 0 warnings. No test run, no GUI.

## F1: declarations that repaint text
`OwnColorDetector.TextRepaintingProperties` holds `all`, `filter`, `backdrop-filter`, `mix-blend-mode`, `box-shadow`; a prefix
covers `border-image*`.

Checked against the real allow-list (`HtmlSanitizerDefaults.AllowedCssProperties`, 240 names, used unchanged by the sanitizer)
and by running each through `Sanitize`:

| Name | On the list | In the output |
|---|---|---|
| `all`, `filter`, `mix-blend-mode`, `box-shadow`, `border-image`, its 5 longhands | yes | yes, seen by the check |
| `backdrop-filter` | **no** | **no**: `<p style="backdrop-filter:blur(1px)">x</p>` becomes `<p>x</p>` |

**Open, case 14:** the check runs after the allow-list, so `backdrop-filter:blur(1px)` alone gives the **dark** style, against
the expected light sheet. It is readable: the declaration never reaches the browser. The name stays in the set as F1 lists it,
as a safeguard should the allow-list change; it cannot trigger today. Same situation as case 5a.

Allowed, not added, for a decision (statements about the screen are from the CSS rules, nothing rendered):
- `content` and `list-style-image`: with an approved remote URL they show an image without an `<img>`, so a logo with a
  transparent background can land on the dark page, which is the reason of D3. `url()` is removed until the approval.
- `opacity`, `mask`, `mask-*`, `visibility`, `clip`: hide or fade content, about equally in both themes.
- `column-rule`, `column-rule-color`, `text-decoration-color`: paint, like `border` and `outline`; no effect on the text
  unless crafted into an overlay, which F1 accepts.

## F3: the background setter
- `InitializeAsync` sets `DefaultBackgroundColor` again as the first line of its `try`, with the stored color.
- Throw path there, read: the first synchronous part of the async method is inside the `try`; the filter takes everything but
  `OutOfMemoryException`; while not closed: a critical log line with the type name and `InitializationFailed`, which leads
  `MainWindow` to `isBrowserUnavailable` and the fixed text. Closed: silent. An `OutOfMemoryException` stays in the discarded
  task, which ends nothing. Same as before AP-04d.
- `SetDefaultBackground` before the start only stores the color, so the window constructor no longer touches the control.
- After the start (a theme change) it needs a guard of its own, because that call is not inside the `try`: same form as
  `DeleteCookies`, a log line with the type name, no `InitializationFailed`; a wrong background does not make the browser
  unavailable.

## F4: failed theme rebuild
- `RebuildAsync(..., bool isThemeChange)` replaces `keepPlainText` and the log source. On failure with `isThemeChange`: the
  document, `State` (`Ready`), notice and blocked count stay; critical log line, source `ReadingPaneViewModel.ApplyThemeAsync`,
  type name only. The image load still goes to `ShowFailure`, source and state as before.
- **The stored theme stays the reported one**, it is what the window shows. Only the document on screen is out of date. So the next
  build of any kind uses it: the next message, "Bilder laden", and the next real change.
- `ApplyThemeAsync` with the same theme again starts no build, so `Loaded` and `ActualThemeChanged` do not retry a failure: the
  document stays in the old theme until one of the above happens. Back to the shown theme gives the same HTML, no navigation.
- A rebuild over the time limit is no failure: plain text with a notice in the new theme, as before (N4, second half).

## F5
- Dark style only: `mark a:link,mark a:visited{color:#0b57d0;}`, about 6:1 on yellow (computed). Light is byte-identical: the dump
  of 34 mails in 4 approvals still equals the one from before AP-04d.
- Comments: both fail-safe branches in `OwnColorDetector` say they do not occur today; `ShowAsync` and `BuildTimeLimit` say the
  limit starts again with a theme change.

## F2, F6
F2: known limit in the risk of `PLAN.md`. `TODO.md` not changed, not in this assignment. F6: nothing, case 13.

Correction to the first section, secure.1 N2: the check runs after `ParseDocument` returned, so outside the allocation and time
budget of the parser, like `SanitizeDom` and `ApplyUrlRules`. It is bounded by the limiter, `PruneElements` and the 5 s limit.

## For the tester
Not run, and the tests were not read: tests from before fix.1 could expect the error state after a failed theme rebuild, or the
dark style for one of the F1 declarations. Case 14 for `backdrop-filter` gives dark, see above.
