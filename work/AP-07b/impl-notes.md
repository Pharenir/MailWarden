STEP-RESULT: done (fix.2: E5-E7 done)

# AP-07b – Implementation notes (Builder, implement.1)

Branch `feat/ap-07b-classifier-calibration`. Build: `dotnet build MailWarden.sln -warnaserror` → 0 warnings,
0 errors. No package reference added (System.Text.Json is part of the BCL). Nothing under `tests\` changed.
`requirement.md` not changed; `PLAN.md`: only the column *Umgesetzt* of AP-07b rows 1–4.

## Files
| File | Change |
|---|---|
| `src\MailWarden.Core\Classification\AuthenticationResults.cs` | new `init` property `CompAuth` (default `None`); positional parameters unchanged |
| `src\MailWarden.Core\Classification\AuthenticationResultsParser.cs` | reads `compauth` (first result), `bestguesspass` → None, `timeout` → TempError; XML doc updated |
| `src\MailWarden.Core\Classification\AuthStatusEvaluator.cs` | DMARC fail + compauth pass → Unknown; new internal helper `IsDmarcFailOverruledByCompAuth` |
| `src\MailWarden.Core\Classification\Signals\AuthenticationSignal.cs` | E1 case → finding with 0 points and reason, no failure points |
| `src\MailWarden.Core\Classification\Signals\LinkDomainMismatchSignal.cs` | no finding when Authenticated |
| `src\MailWarden.Core\Classification\Signals\DomainNotInBodySignal.cs` | no finding when Authenticated |
| `src\MailWarden.Core\Classification\Signals\RandomDomainSignal.cs` | weak band (0.5 ≤ S < 0.8) no finding when Authenticated; strong band unchanged |
| `src\MailWarden.Core\Classification\AuthVerdict.cs`, `AuthStatus.cs` | XML doc only (new token mapping, compauth case) |
| `src\MailWarden.Infrastructure\Classification\ClassifierSettingsFile.cs` | new: `FileName`, `Load(path)` per E4 |
| `PLAN.md` | column *Umgesetzt* for AP-07b rows 1–4 |
| `work\AP-07b\impl-notes.md` | this file |

Central building blocks touched (S3 triggers per project CLAUDE.md): `Classifier*`/settings model is not changed, but
`AuthenticationResults*`/signals are classifier core; the S-level is the planner's call.

## Deviations from the contract
None in names, namespaces, signatures, ids, points or thresholds. Details where the contract is open:

## Decisions (file:line)
- D1 `AuthenticationResultsParser.cs:11-13` – `compauth` counts only at the **start of a statement** (after `;`
  or as the first method). It is deliberately not in the list of method names that start a new result without `;`
  (`spf`/`dkim`/`dmarc`): a compauth pass only weakens a DMARC fail, so `dmarc=fail header.from=a.test compauth=pass`
  (no `;`) leaves `CompAuth = None` and the message stays Failed (fail-safe). Outlook.com always writes
  `;compauth=…`, so real headers are not affected.
- D2 `AuthenticationResultsParser.cs:358` – first compauth result wins; an empty verdict (`compauth=;`) is not kept,
  like C26 for the other methods. `reason=` is an ignored property.
- D3 `AuthenticationResultsParser.cs:366-374` – a header that contains only a compauth result is no longer `Empty`
  (it carries `CompAuth`). `AuthenticationResults.Empty.CompAuth` is `None`.
- D4 `AuthStatusEvaluator.cs:25` – E1 returns Unknown regardless of SPF/DKIM failures in the same header (the
  contract says "DMARC Fail and CompAuth Pass → Unknown"). `DescribePass` still returns null for every DMARC fail.
  A compauth pass **without** a DMARC fail has no effect (not in the contract).
- D5 `Signals\AuthenticationSignal.cs:41-47` – reason text: "DMARC failed for sender domain '<from domain,
  truncated>', but the provider's composite authentication (compauth) passed; the sender is treated as unverified."
  Points 0 (`CompAuthOverrulePoints`). C13 validation accepts 0 points (only id and reason are checked).
- D6 `Signals\RandomDomainSignal.cs:38-46` – band check first, then the Authenticated gate only for the weak band.
- D7 `ClassifierSettingsFile.cs:48` – "missing file" = `File.Exists(path)` false (also when the directory is missing
  or `path` is a directory). `path` null → ArgumentNullException, empty/whitespace → ArgumentException
  (`ArgumentException.ThrowIfNullOrWhiteSpace`).
- D8 `ClassifierSettingsFile.cs:98` – a **duplicate key** is an InvalidDataException naming the key (not listed in the
  contract; fail fast instead of "last one wins").
- D9 `ClassifierSettingsFile.cs:118` – int keys accept only JSON numbers that fit Int32 (`70.5`, `"70"`, `null`,
  `1e10` → "must be a whole number"). List keys: non-array → "must be an array of strings"; `null` entry → "must not
  contain null entries"; other non-string entry → "must contain only strings". Empty/whitespace string entries are
  left to `Validate()` (→ InvalidDataException with inner ArgumentException). Entries are not trimmed; lists become a
  `FrozenSet` with `OrdinalIgnoreCase` (case duplicates collapse).
- D10 `ClassifierSettingsFile.cs:60` – Validate failures: message "Classifier settings file '<name>' is invalid:
  <ArgumentException message>"; the inner message names the **record property** (`LikelySpamThreshold`), not the
  JSON key (`likelySpamThreshold`). Invalid JSON: message with line/byte position, JsonException as inner exception
  (its message has no path). Unknown keys are echoed truncated to 100 characters (`MaximumShownKeyLength`, line 20).
- D11 IO errors other than "missing" (access denied, file locked, race between `Exists` and read) are **not** wrapped
  and propagate as `IOException`/`UnauthorizedAccessException`; their .NET messages may contain the full path.
  The contract lists only the content errors. See question F2.
- D12 File is read with `File.ReadAllText` (UTF-8, BOM accepted).

## Behaviour of AP-07a that changes through E1–E3
- `compauth` is no longer ignored: AP-07a case 2 (Outlook example with `compauth=pass reason=100`) now yields
  `CompAuth = Pass`; Spf/Dkim/Dmarc unchanged. A test comparing the whole record by value against a positionally built
  record would now differ (`CompAuth`); member-wise asserts are unaffected.
- A header consisting only of a compauth statement is no longer `Empty`.
- DMARC fail with `compauth=pass` (as first compauth result, at statement start): AuthStatus Unknown instead of
  Failed; authentication finding 0 points instead of +40…+50; contacts get the "could not be verified" override
  (LikelyLegit) instead of the score tier; allow-listed senders become TrustedContact (allow list applies when
  AuthStatus ≠ Failed). C11 case 21a (no compauth) unchanged.
- Verdict tokens `bestguesspass` (any method) → None instead of Unknown, `timeout` → TempError instead of Unknown.
  AuthStatus effect: none for DMARC (None and Unknown both fall through to DKIM/SPF).
- Authenticated senders: no `link-domain-mismatch`, no `domain-not-in-body`, no weak `random-domain` (+15). Scores of
  authenticated mail drop accordingly; strong `random-domain` (+35) and all other signals unchanged.
- Self-check (not a tester run): existing `MailWarden.Core.Tests` with `Category=feature:classifier` →
  299/299 passed after the change, so no existing AP-07a test appears to assert the changed behaviour.

## Verified vs. untested
Verified (probe `_scratch\Ap07bProbe`, git-ignored, artificial data, all checks PASS):
- Cases 1–12 and 21 of the requirement via the public API (`SpamClassifier`, `AuthenticationResultsParser`), plus:
  compauth inside a dmarc statement ignored (D1), empty compauth skipped (D2), `spf=TIMEOUT`, failed sender keeps
  `link-domain-mismatch`. Weak-band label found by the probe: `kbxshop` (S = 0.51; probe output `0,51`).
- Cases 13–20 with a temp file below the probe's `bin`: missing file → same instance as `Default`, 40/70, list exactly
  `{"zip"}` (case-insensitive lookup), unknown key / case-different key, string and decimal threshold, null and number
  list entries, list as string, duplicate key, 70/30 and empty entry (inner ArgumentException), invalid JSON, root array
  (messages contain the file name, not the directory), comments + trailing commas, BOM + `{}`, null path.
- Build `-warnaserror` green.
Untested: IO error paths (D11), very large files, non-UTF-8 files.

## Questions for the planner
- F1 D1: is "compauth only at statement start" acceptable, or should `compauth=` also start a new result without `;`
  like spf/dkim/dmarc (then a missing `;` before compauth would downgrade a DMARC fail)?
- F2 D11: should `Load` wrap IO errors into an exception whose message names only the file name (AP-07c logs errors
  and the log must not contain the user profile path)?
- F3 D10: should the Validate failure message name the JSON key instead of the record property name?

## fix.2 (E5–E7)
| File | Change |
|---|---|
| `src\MailWarden.Core\Classification\DisplayText.cs` | new public `DisplayText` (`MaximumLength = 100`, `Truncate`) with the former `ReasonText` logic (E5) |
| `src\MailWarden.Core\Classification\ReasonText.cs` | `Truncate` delegates to `DisplayText.Truncate`; `MaximumValueLength` removed (no users) (E5) |
| `src\MailWarden.Infrastructure\Classification\ClassifierSettingsFile.cs` | `InvalidKey` uses `DisplayText.Truncate`, `MaximumShownKeyLength` removed (E5); comment why the inner JsonException is kept (E6) |
| `src\MailWarden.Core\Classification\Signals\AuthenticationSignal.cs` | compauth reason literal wrapped, text unchanged (E7) |
| `PLAN.md` | AP-07b row 4 ⚠️ → ✅ (F3 accepted) |

Deviations: none. Only difference in detail: `DisplayText.Truncate(null)` throws ArgumentNullException (public API, fail
fast); before, `ReasonText.Truncate(null)` would have thrown NullReferenceException. No internal caller passes null.
Verified: build `-warnaserror` green; probe `_scratch\Ap07bProbe` all PASS again (incl. key messages of cases 16–17).
Not run: test stages (tester).
