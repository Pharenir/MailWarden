# review.1

Scope: `git diff main...HEAD -- src PLAN.md` at `72c7421`, 2026-11-01. `tests/` not read. Spec: `requirement.md` D1-D8 and "Decisions after the first build".

**Verdict: CHANGES** (1 MUST, 4 SHOULD, 6 NIT). The view model, D5, D7 and D8 hold. The gap is in `OwnColorDetector`: CSS properties that repaint text pass the sanitizer, are not counted, and make a mail unreadable on the dark page that is readable on the light one.

## How this was verified
- **Read:** all of the diff; `MailHtmlSanitizer.cs`, `HtmlNestingLimiter.cs`, `ReadingDocumentBuilder.cs`, `OwnColorDetector.cs`, `ReadingPaneViewModel.cs`, `MainWindow.xaml(.cs)`, `MailWebViewHost.cs`; AP-04b's diff of `MailWebViewHost.cs`, `ReadingPaneViewModel.cs`, `App.xaml.cs`, `UiSettings.cs` and the theme lines of its `MainWindow.xaml.cs`.
- **Ran:** a throwaway console probe in the session scratchpad against the branch's built `MailWarden.Infrastructure.dll` (18:19, after the last source change): about 110 artificial snippets through `MailHtmlSanitizer.Sanitize` and `ReadingDocumentBuilder.Build`, plus the dump of `HtmlSanitizerDefaults.AllowedCssProperties`. Also `git merge-tree --write-tree` against `feat/ap-04b-ui-actions` (`6582bfd`). Labelled "probe" below.
- **Not run:** test suite, the app, any browser. What a declaration does on screen is from the CSS rules, not rendered. No merged build.

## MUST

### M1: paint properties that are not counted turn a readable mail unreadable in the dark theme
`src/MailWarden.Infrastructure/Mime/OwnColorDetector.cs:57-60` counts only `color` and `background*`. The package's default list (240 properties, `MailHtmlSanitizer.cs:118`) lets more through. Probe: each snippet below gives the **dark** style with the declaration still in the output.

| Artificial HTML | On the dark page | On the light page |
|---|---|---|
| `<p style="filter:brightness(0)">x</p>` | black on `#202020` | black on white |
| `<p style="filter:invert(1)">x</p>` | `#0f0f0f` on `#202020` | see note |
| `<p style="all:initial">x</p>` | `all` also resets `color-scheme` to `normal`, so `color` is black, on `#202020` | black on white |
| `<p style="mix-blend-mode:multiply">x</p>`, `darken` | about `#1e1e1e` on `#202020` | `#1a1a1a` on white |
| `<p style="box-shadow:0 0 1px 9999px #fff">x</p>` | white painted below all in-flow text, `#f0f0f0` on white | white on white around dark text |
| `<p style="border-image:linear-gradient(#fff,#fff) 1 fill">x</p>` | same, inside the element | same |

Note: `filter:invert(1)` is also poor on light (`#e5e5e5` on white), the others are readable there. So this is a regression of this AP compared with `main`, not an old weakness. Such mails are rare and partly crafted, but D4 says an unclear case errs towards light, and the fix is one set.

Fix: count `all`, `filter`, `mix-blend-mode`, `box-shadow` and `border-image*` as well, as a named set next to the two constants. Add them to the D4 list (planner) and to the test cases. A small `box-shadow:0 1px 3px rgba(0,0,0,.2)` then also gives the light sheet, which costs nothing in readability. `Ich bin mir unsicher:` that Chromium blends `mix-blend-mode` with the canvas colour when that comes from `html`; counting it is right either way.

## SHOULD

### S1: light text on the light sheet when the mail's dark background is lost (same on `main`, needs a decision)
`ReadingDocumentBuilder.cs:65` with `MailHtmlSanitizer.cs:62`, `:81-87`. The text colour survives, the background it was written for does not. Probe, all **light** sheet:
- `<body style="background:#111"><table><tr><td style="color:#eee">x</td></tr></table>`: output `<td style="color: rgba(238, 238, 238, 1)">`, on white.
- `<style>body{background:#000}</style><font color="#fff">x</font>`: output `<font color="#fff">`, white on white.
- `<table background="http://x.test/dark.png"><tr><td><font color="#fff">x</font></td></tr></table>`: attribute gone, white on white.
- `<div style="color:#fff;background:url(http://x.test/dark.png)">x</div>`: white on white until "Bilder laden".
- `<div style="color:#fff;background:color-mix(in srgb,#000,#000)">x</div>`: AngleSharp drops the background, keeps the color. Reverse: `background:#000;color:color-mix(...)` gives `#1a1a1a` on black.

No regression: `main` shows all of these the same way, and D3/D5 forbid rewriting. But it is exactly "light text on a light page", and D3's reason suggests the sheet solves it. Do not fix without a decision. Enter it as a known limit in `PLAN.md:344-345` and `TODO.md`. Option for Dennis: in the dark theme, a mail whose only own colours are text colours (no background, no image) keeps the dark page. That would help the first three cases without touching the light theme, but it would make dark text unreadable there unless the brightness is checked, which is guesswork. My advice is the known limit.

### S2: the old light sheet shows when the browser becomes visible (D6)
`src/MailWarden.App/MainWindow.xaml.cs:344,350`, `MailWebViewHost.cs:71-81`. `Clear()` forgets the document but does not navigate. The control is collapsed during Loading, and on `Ready` it is set visible **before** `Navigate`. Dark theme, newsletter (white sheet), then a plain mail: dark window, old white sheet with the previous mail, then the dark page. The default background does not help, because a document is still there. Read only, not seen; duration unknown. `main` has the same order, but only now does the jump go from white to dark. Check in case 13 with exactly this sequence; if visible, make the control visible at `NavigationCompleted` or drop the old document in `Clear`.

### S3: the background setter is outside any guard
`MailWebViewHost.cs:98-104`, called from `MainWindow.xaml.cs:146`. The removed line sat inside the `try` of `InitializeAsync`; now it runs in the window constructor and in the theme handlers. `App.OnUnhandledException` (`App.xaml.cs:200-203`) only logs and does not set `Handled`, so a throw ends the process. Compare `DeleteCookies` in the same class, which guards a browser call for the same reason. `Ich bin mir unsicher:` whether the setter can throw at all (crashed browser process, failed start). Fix: same `try`/`catch` with a log line as `DeleteCookies`.

### S4: `PLAN.md` has no AP-04d
`PLAN.md:85` lists AP-04a to c. `CLAUDE.md` fixes the Sollverhalten in `PLAN.md`, table per AP, and AP-04b has its own section on its branch. D1-D8 live only in `requirement.md`; AP-04a row 8 (`PLAN.md:254`) carries everything, and its *Getestet* column still shows case 40 as done, which predates the new rule. Fix: add the AP and a table with the state per D row; row 8 points to it.

## NIT
- **N1** `ReadingDocumentBuilder.cs:39`: `<mark><a href="https://x.test">x</a></mark>` (probe: dark) gives `#8ab4f8` on yellow, about 2:1. Readable with effort. Own constant, so D7 allows `mark a:link,mark a:visited{color:...}`.
- **N2** `OwnColorDetector.cs:31,52`: the `style` element and `style.Length == 0` cannot occur today (probe: `style="foo"`, `style="color"` are gone before the check). Fine as fail-towards-light; a short comment saying so would help.
- **N3** `ReadingPaneViewModel.cs:235,380-391`: "at most BuildTimeLimit from this call" no longer holds when the theme changes during the build, each loop gets a new limit. Bounded by the user's clicks. Adjust the sentence.
- **N4** `ReadingPaneViewModel.cs:576-581`: a failed theme rebuild replaces a readable document with the error state, and a second build over the limit turns it into plain text with a notice. Honest and rare; accepted unless Dennis objects.
- **N5** `MailWebViewHost.cs:96`: public method between private ones, the class otherwise has public first.
- **N6** Crafted only, accept: `<p style="position:absolute;z-index:-1;border-top:9999px solid #fff"></p><p>x</p>` (probe: dark, kept) lights the page under `#f0f0f0`. A mail can also hide itself in any theme (`font-size:0`). Dark borders such as `<td style="border:1px solid #000">` vanish on the dark page, the text stays readable.

## Checked, no finding

**1. Detector.**
- `class` without a stylesheet: no effect, `id` is removed. `<font face>`, `bordercolor`, `background` attribute, `text`/`link`: dark, nothing coloured remains (probe).
- `<font color>`, `<tr bgcolor>`, `<hr color>`, `!important`, `COLOR :`, `c\6flor`, `colo&#114;`, `/**/color/**/`: light (probe).
- `*color`, `color\9`, `_color`, `-webkit-text-fill-color`, `-webkit-text-stroke*`, `-webkit-filter`, `-webkit-box-shadow`, `-webkit-background-clip`, `fill`, `stroke`, `accent-color`, `color-scheme`, `forced-color-adjust`, `--x`: removed by the package (probe).
- `caret-color`, `opacity`, `text-shadow`, `text-decoration-color`, `column-rule`, `border-color`: kept and dark, correctly: no effect on the text, or the same in both themes.
- AngleSharp against Chromium: the serialised style comes from AngleSharp itself, so what it drops (`color(srgb ...)`, `color-mix`, `light-dark`, `hwb`, `color:000`) Chromium never sees, and our colours apply (probe). What it keeps and Chromium rejects (`color:var(--x)`) is counted, a readable sheet. It converts `oklch`, `lab` and `rgb(0 0 0 / 50%)`. No path where a colour is applied but not seen. Only the half-removed pairs of S1 come from this.
- `HtmlNestingLimiter`, `PruneElements`: they remove colours before the check and from the output alike. 300 nested `div` around a coloured span: dark, no `color` in the output; a style with 300 `;`: dark, `<p>x</p>` (probe). `<o:p>`, `<marquee>`, `<form>` lose the tag and its style.
- Same DOM: the check runs on the DOM that is serialised, later steps only remove, so the answer is a superset of the output and errs towards light.

**2. Light sheet.** `html,body` carry the background, so margins and canvas are white. No `color-scheme`, so scrollbars and default link colours are light. No `prefers-color-scheme` rule can reach the page (`<style>` is removed). Read only; scrollbars for case 13.

**3. Dark page.** `<!DOCTYPE html>` means standards mode, so tables inherit the colour. `a:link,a:visited` wins against the UA sheet also while active; `<a>` without `href` inherits. `#f0f0f0` about 14:1, links about 7.7:1 on `#202020`. `<mark>` is black on yellow; `<hr>`, table borders and `fieldset` are drawn in gray or dark-scheme colours; `blockquote` and `code` have no colours of their own. Read, not rendered.

**4. View model.**
- Every path into `DocumentHtml` is behind a `generation` check; `ShowAsync`, the image load and `Close` raise it, so a stale build cannot overwrite a newer one.
- Theme change during `ShowAsync` or the image load: `isDocumentIdle` is false, the loop in `BuildForCurrentThemeAsync` builds again. Toggling back during the build reuses the result.
- "Bilder laden" during a theme rebuild: cancels it, waits for the running build, builds with the approval. Theme change during the image load: the loop keeps the passed approval, not `currentApproval`.
- Junk: `currentApproval` keeps `Embedded: false`. Fallback: `keepPlainText`, synchronous, no second long build.
- `AreRemoteImagesAllowed` survives, the state stays `Ready`, no read from store or server.
- The build thread only gets values, no view model state. After `Close`, `ApplyThemeAsync` only stores the theme.
- `_ = ApplyThemeAsync(...)`: `RebuildAsync` holds its failures, continuations on the UI thread. Same pattern as `LoadRemoteImages`.

**5. Wiring.** Background before document; `Loaded`, `ActualThemeChanged` and `ApplyTheme` are idempotent. `webViewHost` exists before the first `ApplyTheme`, and `SelectedIndex` is set before the subscription. No `Microsoft.UI`, `Windows.UI` or `Microsoft.Web` in `Core`, `Infrastructure`, `Presentation` (grep). Code-behind only maps and reports.

**6. D7.**
- No line of the allow-lists, `RemovedElements` or `BuildContentSecurityPolicy` in the diff. Probe: in all four approvals everything before `<style>`, and so the CSP, is the same in both themes, and only the style block differs. A coloured mail gives the same document in both themes.
- Light style equals the old `BaseStyle` by concatenation (D5).
- The check only reads, runs after the parser budget and inside the 5 s limit of the build; it does not touch `Html`, the count or `WasReduced`.
- `HasOwnColors = true` as default fits: `ToTextOnly` and the missing body give the light sheet, readable. The only constructor calls are those in `MailHtmlSanitizer`.

**7. Merge.** `git merge-tree` against `6582bfd`: no text conflict. AP-04b's `StatusBarTextChanged`, `IsStatusBarEnabled=false` and `HoverLinkText` do not touch the theme path. Removing `title` changes nothing here: the detector does not read it. Its dialogs take `RootGrid.ActualTheme`, fine. To do at merge: `ShellHarness.cs` and any AP-04b test that passes a three-parameter builder must move to four parameters (from `impl-notes.md`; file not read).

## For case 13 (Dennis), besides what is listed
- "Design System", change the Windows setting while the app runs. `Ich bin mir unsicher:` that `ActualThemeChanged` fires on an element with `ElementTheme.Default` in an unpackaged WinUI 3 app; if it does not, D1 fails for this mode only.
- Dark theme: newsletter, then plain mail (S2).
- Scrollbars of a light sheet in the dark window.
- With AP-04b merged: theme change with the mouse on a link; does the hint go away?
