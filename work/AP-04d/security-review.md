# Security review AP-04d

# secure.1 (2026-11-01; written down by the planner, the role has no write access)

Reviewed `feat/ap-04d-reading-theme` at 72c7421, diff `main...HEAD -- src`. `tests\` not read.

**Result: 0 Critical, 0 High, 0 Medium, 1 Low, 2 Notes.** D7 holds.

## Findings

### L1: A line of a mail can be visible in only one theme
- **Where:** `src\MailWarden.Infrastructure\Mime\OwnColorDetector.cs:59-60`. D4 only counts `color` and `background*`.
- **Scenario:** `<p style="all:initial">`, `filter:brightness(0)` or `filter:brightness(100)` survive the allow-list and get `HasOwnColors=false`. `all:initial` or `brightness(0)` should give black on `#202020`, barely visible in dark and readable in light. `brightness(100)` the reverse. An absolute box with `border`, `outline` or `box-shadow` in the page colour can cover text in the same way.
- **Delta against the light-only version:** no new power, `color:#fff`, `opacity:0` and overlays could already hide text. New is only the condition "appears after a theme switch". No component compares the two renderings.
- **Recommendation:** add `all`, `filter` and `mix-blend-mode` as triggers. Accept the border and shadow overlays; closing them would turn ordinary mails with `border:1px solid #ccc` light.

### N1: An approved mail can fetch its remote images again on a theme change
- **Where:** `src\MailWarden.Presentation\ReadingPaneViewModel.cs:344-346`, `src\MailWarden.App\MailWebViewHost.cs:49`.
- **Scenario:** a mail without own colours holds a remote URL in `list-style-image`, `cursor`, `content`, `mask` or `border-image`. After approval a theme change navigates anew, so the server may see a second hit. Only URLs the user already approved.
- **Recommendation:** accept.

### N2: The detector runs outside the parser budget
- **Where:** `src\MailWarden.Infrastructure\Mime\MailHtmlSanitizer.cs:172`.
- **Assessment:** nothing to exploit. The budget ends when `ParseDocument` returns, the same as `SanitizeDom` and `ApplyUrlRules`. What bounds the check: the limiter (64 KB per style, 256 declarations, 2 MB tag text), `PruneElements` (depth 256), the 5 s limit of the view. Do not describe the check as "inside the budget".

## Answers
- **D7:** `Sanitize` has no theme parameter; allow-lists, `BuildContentSecurityPolicy` and WebView2 `Configure` are not in the diff; `LightStyle` concatenates to the former `BaseStyle`; only the bool leaves the sanitizer. 16,000 fuzz outputs byte-identical between baseline and branch. No way found to smuggle a declaration past the detector (escapes, unterminated strings, comments, `}`, NBSP); the browser only sees AngleSharp's re-serialized form.
- **Thread and cost:** the only route to `Build` is the build thread with 256 MB stack. Baseline and branch within noise (8,000 styled tags 2,376 and 2,330 ms). An exception ends in `ShowFailure` with the type name only.
- **Image decision:** `currentApproval` is only set with its document; a new message clears it; `ApplyThemeAsync` rebuilds only when idle and with held content; Junk keeps `Embedded=false`. A running build is reused only with the same message, approval and theme.
- **`color-scheme:dark`, `DefaultBackgroundColor`:** no security effect; mail cannot observe the theme, because `<style>` and `<source media>` are removed.
- **Logs:** nothing new from mail content.

## Verified how
- **Read:** the diff and the code it reaches, requirement and notes, the builder's probe.
- **Ran:** a throwaway console probe on artificial strings on a 256 MB stack thread: the allowed CSS list (240 properties), about 100 evasion cases, the fuzz comparison, timings.
- **Not run:** no browser, so every rendering statement in L1 is reasoning; no GUI, no tests, no live theme change. The baseline DLL was not proven to be built from 690c062.
- **After the merge with AP-04b:** check the image approval paths again, both change `ReadingPaneViewModel.cs` and `MainWindow.xaml.cs`.
