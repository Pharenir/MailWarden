# AP-04e security review

## secure.1 (security-reviewer, 2026-11-02): 0 Critical, 0 High, 0 Medium, 3 Low
Report written down by the planner, because the role cannot write.
- D7 of AP-04d holds in all paths: sanitizer, CSP and WebView2 settings are not in the diff; the style comes only from constants, theme and the yes/no of `HasOwnColors`. Probe: 66 artificial mails with 4 image approvals, 264 comparisons across the three themes, no difference outside the page style.
- `::selection` gives a mail nothing new: `<style>` is removed in every variant tried, so a mail cannot address the rule. The rule is not in the dimmed style.
- No personal values, paths or mail properties in logs, generator or XAML. The dictionary is compiled, its URI a constant; nothing writable from the data folder is read for it. The generator runs by hand only.
- Warnings in Nightshade, computed: banner text 10.8:1, warning icon 9.0:1, notices on the window 14.7:1, "Bilder laden" 11.7:1. Banner against window only 1.37:1, so it stands out by icon and border (case 27).
- L1: `HasOwnColors = false` does not mean no colour at all; `text-decoration-color`, `text-shadow`, `border`, `outline` survive. A fake link look or a covered text are possible as in the dark style. Accepted.
- L2: high contrast switched on while running leaves the solid background until the next theme change or start. Accepted, case 29.
- L3: `PLAN.md` and `impl-notes.md` described the removed event. Fixed in fix.2.
- Not checked: no browser ran, the window never started in this review, the generator was not run against `generic.xaml`, the probe ran on a newer runtime. For AP-13: only `data\` may become writable, not `<install>`.
