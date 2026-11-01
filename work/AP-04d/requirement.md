# AP-04d: reading pane follows the theme

Wish of Dennis (2026-11-01): "Wenn möglich sollte der Lesebereich auch mit dem Thema mitgehen. Also dunkler Hintergrund bei dunklem Modus. Die Schriftfarbe muss dann ggf. auf weiß angepasst werden."

This changes the rule in `ReadingDocumentBuilder` that mail content is never rendered dark. The reason behind that rule stays: a mail must always be readable.

Branch `feat/ap-04d-reading-theme` from `main` (690c062), parallel to AP-04b. Merge order: AP-04b first, then this branch merges `main`. Security review required (HTML output, WebView2 configuration).

## Sollverhalten (planner, 2026-11-01)
- D1: The reading pane follows the theme the app actually shows, also for "Design System" where the Windows setting decides. It changes while the app runs, without another click on the mail and without reading from the server again.
- D2: A mail without colours of its own, in the dark theme: dark page, light text, readable links, dark scrollbars. This covers plain text and HTML without colour information after sanitising.
- D3: A mail with colours of its own stays on its light page with its own colours, also in the dark theme. Reason: its dark text on a dark page, or a logo with a transparent background, would be unreadable, and rewriting colours is guesswork. So it is a light sheet in a dark window.
- D4: "Colours of its own" is decided on the sanitized HTML and errs towards light. It counts if the sanitized HTML holds any of: a `color` or `background`/`background-color`/`background-image` declaration in a `style` attribute or a `<style>` rule, the attributes `color` or `bgcolor`, or an `<img>`. An unparsable or unclear case counts as its own colours.
- D5: The light theme looks as it does today, for every mail.
- D6: No white flash in the dark theme: the WebView2 default background and the page background match while a mail loads. The light sheet of D3 may be white, it is the mail.
- D7: Sanitizer and Content-Security-Policy stay exactly as they are. The theme only selects a fixed style from our own constants; nothing from the mail reaches that selection except the yes/no of D4. Remote-image approval and the state of the reading pane survive a theme change.
- D8: `Core` and `Infrastructure` stay free of WinUI. The theme reaches `ReadingDocumentBuilder` as a parameter (an enum of our own), `MainWindow` reports the actual theme to the view model, code-behind only wires.

## Test cases
| # | Situation | Expected |
|---|---|---|
| 1 | Plain text, dark | Dark style, text encoded as before |
| 2 | Plain text, light | Document as today |
| 3 | HTML without colours, dark | Dark style |
| 4 | HTML with `style="color:#000"`, dark | Light sheet, colours kept |
| 5 | Each D4 trigger alone: `background-color`, `background-image`, `bgcolor`, `<font color>`, `<img>`, dark | Light sheet |
| 5a | `<style>p{color:red}</style><p>x</p>`, dark | Dark style: the sanitizer removes `<style>`, so the rule is gone and our colours apply |
| 6 | Word in text: "color: red" as plain visible text in HTML without colours, dark | Dark style: only real declarations count |
| 7 | Upper case and spaces: `STYLE="COLOR : red"`, dark | Light sheet |
| 8 | HTML with colours, light | As today |
| 9 | The CSP is the same in every combination; remote images still need approval | Same text, same rule |
| 10 | Theme change with an open mail | New document from the content already held; no read from the store or server; selection and image approval kept |
| 11 | Theme change without an open mail | Nothing happens, no failure |
| 12 | Hostile mail: a `<style>` or attribute trying to reach our page style | Sanitizer output unchanged, base style first as before |
| 13 | Manual, Dennis: dark, light and Design System, while the app runs; a plain mail, a newsletter; no white flash | As stated |

## Done when
S3 green on the branch merged with `main`, reviewer APPROVE, security-reviewer without Critical/High, documenter done, case 13 by Dennis, CPM by Dennis.

## Decisions after the first build (planner, 2026-11-01)
- A `<style>` rule is not a trigger in practice: the sanitized HTML never holds `<style>`, and D2 and D4 decide on the sanitized HTML. Looking at the raw CSS before the parser would mean reading hostile input outside the parser budget, and would turn most Word and Outlook mails light. Accepted: case 5a.
- `<body bgcolor>` and `<body style>` never reach the sanitized HTML either, so they do not count.
- The D4 check runs before the URL rules, so a blocked `background-image` or a removed `<img>` still counts as own colours. This errs towards light and stops the page jumping to light on "Bilder laden". Accepted.
- Scroll position after a theme change: it may jump to the top for mails without own colours, because the page is navigated anew. Accepted: a theme change is rare and scripts stay off.

## Decisions fix.1 (planner, 2026-11-01, after review.1 CHANGES and secure.1)
- F1 (review.1 M1, secure.1 L1): D4 also counts the declarations that repaint text: `all`, `filter`, `backdrop-filter`, `mix-blend-mode`, `box-shadow`, and any `border-image*`, as a named set in `OwnColorDetector`. Reason: a mail that is readable today must stay readable in the dark theme. `border`, `outline` and `text-shadow` do not count: they would turn ordinary mails light, and crafted overlays could already hide text in the light-only version.
- F2 (review.1 S1): Known limit, no code: light text can end up on the light sheet when its dark background was removed by the sanitizer (`<body style="background:#111">` with `<td style="color:#eee">`). `main` behaves the same. Guessing at brightness is not in scope. It goes to `PLAN.md` (risk) and `TODO.md`; Dennis sees it in the report.
- F3 (review.1 S3): The background setter gets the same protection as before, a throw there must not end the process.
- F4 (review.1 N4): A failed theme rebuild keeps the readable document instead of showing the error state; the critical log records the type name.
- F5 (review.1 N1, N2, N3): links in `<mark>` get a readable colour; comments on the fail-safe branches and on the build limit are corrected.
- F6 (review.1 S2): Old white sheet visible for a moment when going to a plain mail: watch it in case 13, change it only if Dennis sees it.

### Additional test cases (fix.1)
| # | Situation | Expected |
|---|---|---|
| 14 | Each of `all:initial`, `filter:brightness(0)`, `mix-blend-mode:multiply`, `box-shadow:0 0 1px 9999px #fff`, `border-image:linear-gradient(#fff,#fff) 1 fill`, `list-style-image:url(https://images.example.test/a.png)`, `content:url(https://images.example.test/a.png)`, dark | Light sheet |
| 14a | `backdrop-filter:blur(1px)`, dark | Dark style: the allow-list removes it, so it never reaches the browser; the name stays in the set in case the list grows |
| 15 | `border:1px solid #ccc`, `outline:1px solid #ccc`, `text-shadow:1px 1px #ccc`, `list-style:none`, `list-style:disc`, `list-style-image:none`, `content:normal`, dark | Dark style |
| 16 | The builder fails during a theme change with a readable mail open | The document stays, state stays `Ready`, type name in the log |

Added by the planner after fix.1: `content` and `list-style-image` count too, for the reason of D3. `opacity`, `mask`, `visibility`, `clip`, `column-rule` and `text-decoration-color` do not: they hide or paint alike in both themes.

After review.2 APPROVE (S1): `content` and `list-style-image` count only with a value other than `none`, `normal` or `initial`, because `list-style:none` is common in ordinary mail. Explicit no-ops of the other set, such as `box-shadow:none`, stay light (review.2 N3, accepted).

## Decision after Dennis's manual test (planner, 2026-11-01)
Dennis: plain text mails follow the theme as wanted. A styled mail such as a newsletter "bleibt hell. Ist nicht das schlimmste, aber wäre schön wenn auch diese zumindest etwas abgedunkelt werden könnten."

- G1 (changes D3 and case 4): In the dark theme a mail with colours of its own stays a light sheet with its own colours, under a fixed dark veil over the whole page (25 % black), so white becomes about `#bfbfbf`. No colour is rewritten, so the contrast between its colours holds. The light theme stays byte-identical (D5).
- G2: The veil is part of our fixed page style. It takes no clicks and no selection (`pointer-events:none`); links and the hint at the mouse work through it. D7 holds: only the yes/no of D4 selects the style.
- G3: A mail with own colours now gets a different document on a theme change, so it is navigated anew. A mail with approved remote images may fetch them again (secure.1 N1 now also covers `<img>`). Accepted: only approved URLs.

### Additional test cases (G)
| # | Situation | Expected |
|---|---|---|
| 4 (new) | HTML with `style="color:#000"`, dark | Light style plus the veil, colours kept, no dark style |
| 17 | Every light-sheet case of 5, 7, 14, dark | The veil is there |
| 18 | Plain text and HTML without colours, dark | No veil |
| 19 | Any mail, light | No veil, byte-identical to today |
| 20 | Theme change with a mail with own colours | `DocumentHtml` changes, no read from the store, image approval kept |
| 21 | Manual, Dennis: newsletter in the dark theme: dimmed, readable, link clicks work, the hint shows the target, text can be selected | As stated |
