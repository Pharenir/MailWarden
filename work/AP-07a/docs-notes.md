# AP-07a – Docs notes (Documenter)

## Changed
- `docs\classifier.md` (new, in the main checkout `K:\Projekte\Windows-Tools\MailWarden\docs\`; `docs\` is git-ignored,
  stays local): tiers, decision order, AuthStatus (DMARC precedence C11/C12/C22), seven signals with id/points/example
  reason, defaults of `ClassifierSettings.Default`, `RandomDomainScorer` in words, parser hardening (C10, C17, C19,
  C20, C26, C25 marked as stricter than RFC 5322), known limits (C1, C4, C16, C24, unbalanced quotes), section
  "Noch nicht umgesetzt" (AP-07b/AP-08/AP-06/AP-12).
- `README.md` (in this worktree, as AP-01 is merged here): status line, project structure (+ `tests\MailWarden.Core.Tests`,
  Core line mentions the classifier core), test bullet. `## AI usage` unchanged.

## Notes
- Example reason texts in `classifier.md` are taken from the source format strings; the example values are artificial.
- The root README on `chore/app-icon` is still the pre-AP-01 version; the worktree README is the one that merges.
- Not described (not implemented): moving to folders, quarantine retention, loading settings, authserv-id check.
