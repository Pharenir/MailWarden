# AP-07b – review.1 (reviewer, opus; recorded by the planner)

**Verdict: APPROVE** (diff `d39915b...feat/ap-07b-classifier-calibration`, `src`, `PLAN.md`, `impl-notes.md`;
`tests\` not read). Build `-warnaserror` green, no test runs.

## Contract
E1–E4 fulfilled. compauth counts only as the first key of a statement (not in `EvaluatedMethodNames`,
`AuthenticationResultsParser.cs:14`), comments/quoted strings cannot inject it; unterminated comment/quote swallows the
rest → `None` → stays Failed (fail-safe). "No silent points" kept. Core without IO/packages.

## BLOCK
None.

## SHOULD
1. Duplicate truncation logic: `ClassifierSettingsFile.cs:154-168` (`InvalidKey`, `MaximumShownKeyLength = 100`)
   copies `ReasonText.Truncate` (`ReasonText.cs:18-28`). Make one public helper in Core and call it.

## NIT
1. `ClassifierSettingsFile.cs:48`: `File.Exists` false for unreadable file/directory → silent defaults (same pattern as
   `AuthConfiguration.Load`).
2. `ClassifierSettingsFile.cs:79-83`: keeps the inner JsonException unlike `AuthConfiguration`; add a comment why
   (no secrets in this file).
3. `Signals\AuthenticationSignal.cs:46`: very long string literal.

## Builder questions
- F1: keep D1 (compauth only at the start of a statement).
- F2: do not wrap IO errors here; one rule for all file loaders in AP-07c/AP-04 (log only type + own message).
- F3: accept property name in the validation message; set PLAN row 4 to ✅.

## Questions to the planner
- P1: allow-listed sender with DMARC fail + compauth pass becomes TrustedContact (status Unknown, not Failed).
- P2: "first compauth counts" is not fail-safe against a statement injected before the real one (same precondition as
  an injected `dmarc=pass`); possible hardening: contradicting compauth results → None.
- P3: E3 removes content points for authenticated spammers with own domains – watch in the PstProbe run.
