# AP-04e review

## review.1 (reviewer, 2026-11-02): CHANGES
No BLOCK. S1 button hover 1.03:1 (`tools\make_nightshade_theme.py`); S2 free text white at the start, not proven for the real order (`MainWindow.xaml.cs`, `WindowThemeChrome.cs`); S3 `PLAN.md` and `impl-notes.md` contradict fix.1; S4 a failure while switching ends the app (`MainWindow.xaml.cs`). NIT: N1 disabled text 2.71:1, N2 pressed in flyouts, N3 disabled accent, N4 start failure text for a missing resource.
Checked without a finding: start order, `ContentDialog`, no effect of the bounce on `ApplyReadingTheme`, all directions, Light and Dark styles textually as on `main`, E1 to E4, generator and generated file, contrast table, layers, encoding.
Decisions: `requirement.md`, "Decisions fix.2".

## review.2 (reviewer, 2026-11-02): CHANGES, text only
Code without a finding; `isFirstApply` and `ApplyThemeWhileRunning` correct by reading, `MainWindow` never ran. Generator output byte-identical to the file. S1 manual cases could move a real mail; S2 `impl-notes.md` partly outdated; S3 a failed switch is saved, needs a decision and a line in the behaviour. NIT: N1 to N3 wording of the manual cases, N4 points for `TODO.md`, N5 button border on hover.
Decisions: `requirement.md`, "Decisions fix.3".

## review.3 (reviewer, 2026-11-02): APPROVE
Text round only, no code touched. S1 and S2 of review.2 fixed, E17 and the deferred points in `TODO.md` match the code. Three NITs on wording, fixed without a new round: the context menu exists on the Junk of one account only and the dialog needs at least one mail; the setting "mark read" could set a flag during the viewing cases; `impl-notes.md` still said nothing was tested.
