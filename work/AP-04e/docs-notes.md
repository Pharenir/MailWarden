# AP-04e docs notes

## Changed
- `README.md`, "Using the app" > Design: themes now Nightshade, System, Hell, Dunkel; Nightshade is the default of a new
  installation, a stored theme stays; Nightshade and Dunkel turn mails without own colours dark; high contrast note
  (change counts from the next theme change or start; fix.1, secure.1 N2).
- `README.md`, "Project structure": `src\MailWarden.App` mentions the palette (Themes); new line for `tools` (`make_nightshade_theme.py`).
- Project `CLAUDE.md` (in `K:\Projekte\Windows-Tools\MailWarden\`, not part of the worktree), Stolperfallen: three entries
  (HighContrastChanged 0x80070490; generic.xaml alias / generated NightshadeTheme.xaml; RequestedTheme bounce incl. first Apply).

## Not changed / open
- README status paragraph does not list themes: left as is.
- README names no test count: nothing to change (line 198 lists areas only).
- No CHANGELOG (project rule); `docs\classifier.md`, `docs\auth-setup.md` not affected.
- Question: the README does not say the settings key `theme` accepts "nightshade" (the file section only says "theme"): left as is.
- The project `CLAUDE.md` is untracked in the worktree and lies outside it; the planner must handle that file separately.
