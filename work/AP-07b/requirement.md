---
id: AP-07b
slug: classifier-calibration
title: Classifier calibration from real headers (compauth, verdict tokens, auth-gated content signals) and settings file
test-scope: S2
test-filter: "Category=feature:classifier|Category=smoke"
base: chore/session-handover (builder and tester)
status: ready (2026-10-31)
---

# AP-07b – Classifier calibration and settings file

## Goal
Adjust the AP-07a classifier core to what real mail looks like (findings of the PstProbe run over Dennis's Outlook
export, 345 messages with original headers, see `TODO.md` → "Befunde PstProbe") and load the classifier settings from
a JSON file. No wiring to synced mail yet (AP-07c).

## Context
- Classifier core: `src\MailWarden.Core\Classification\` (AP-07a, decisions C1–C27 in `work\AP-07a\requirement.md`).
  Relevant files: `AuthenticationResultsParser.cs` (verdict mapping around line 249), `AuthenticationResults.cs`,
  `AuthStatusEvaluator.cs`, `Signals\AuthenticationSignal.cs`, `Signals\LinkDomainMismatchSignal.cs`,
  `Signals\DomainNotInBodySignal.cs`, `Signals\RandomDomainSignal.cs`, `ClassifierSettings.cs`.
- Probe findings (anonymized) that drive this AP:
  - Outlook.com writes `compauth=pass reason=NNN` into its Authentication-Results. One message from a contact had
    `dmarc=fail` + `compauth=pass reason=105` and became LikelySpam (false positive).
  - `dmarc=bestguesspass` (5×, always with `dkim=none`, `compauth=pass reason=109`) is mapped to `Unknown` today.
  - `dkim=timeout` (3×) is mapped to `Unknown` today.
  - Signal hits on authenticated mail outside the junk folder vs. junk: `link-domain-mismatch` 234 vs 0,
    `domain-not-in-body` 29 vs 1, weak `random-domain` 2 vs 0 (abbreviation domains). On non-authenticated junk the
    same signals hit 4/4, 4/5 and 2/2 → they separate only when the sender is not authenticated.
- Projektregel: every signal returns points **with reason text**; no silent points.

## Non-goals
- No change of thresholds (30/70) or of the points of other signals.
- No ClassificationInput extraction from MIME, no folder moves, no logging (AP-07c).
- No public suffix list, no authserv-id check (finding: Outlook.com omits the authserv-id in most headers → C16 stays
  open, trust remains "top-most header").
- No new packages (System.Text.Json is part of the BCL).

## Contract (binding)

### E1 – `compauth` (Core)
```csharp
public sealed record AuthenticationResults(...)   // positional parameters unchanged
{
    /// Verdict of Microsoft's composite authentication ("compauth=<verdict>"); None when absent.
    /// Only the first compauth result of the header counts; its "reason" property is ignored.
    public AuthVerdict CompAuth { get; init; } = AuthVerdict.None;
}
```
- `AuthenticationResultsParser.Parse` fills `CompAuth` (same verdict token mapping as the other methods).
- `AuthStatusEvaluator.Evaluate`: DMARC Fail **and** CompAuth Pass → `AuthStatus.Unknown` (not Failed, not
  Authenticated). DMARC Fail without CompAuth Pass → `Failed` as before. `DescribePass` stays null for DMARC Fail.
- `AuthenticationSignal`: in the E1 case it returns a finding with **0 points** and a reason that says DMARC failed but
  the provider's composite authentication passed, so the sender is treated as unverified. It must not add the
  DMARC-fail, SPF-fail or DKIM-fail points in that case.
- Consequence (no extra code): a contact in the E1 case gets the existing override "Sender is a contact but
  authentication could not be verified" → LikelyLegit.

### E2 – verdict tokens (Core, parser)
- `bestguesspass` → `AuthVerdict.None` (Microsoft: no DMARC record published).
- `timeout` → `AuthVerdict.TempError`.
- Mapping applies to every method; all other tokens unchanged.

### E3 – auth-gated content signals (Core)
When `context.AuthStatus == AuthStatus.Authenticated`:
- `link-domain-mismatch` returns null (no finding).
- `domain-not-in-body` returns null.
- `random-domain`: score in the weak band (≥ 0.5 and < 0.8) returns null; strong band (≥ 0.8, +35) unchanged.
For `Unknown` and `Failed` all three behave exactly as in AP-07a.

### E4 – settings file (Infrastructure)
```csharp
namespace MailWarden.Infrastructure.Classification;

public static class ClassifierSettingsFile
{
    /// File name below the data root (the composition root combines it with DataPaths.Root in AP-04).
    public const string FileName = "classifier-settings.json";

    /// Missing file → ClassifierSettings.Default.
    /// JSON object with optional keys (camelCase, case-sensitive):
    ///   "likelySpamThreshold": int, "certainSpamThreshold": int,
    ///   "suspiciousTlds": [string], "freemailDomains": [string]
    /// A missing key keeps the default value; a given list replaces the default list completely (no merge).
    /// Comments and trailing commas are allowed.
    /// Result is validated with ClassifierSettings.Validate().
    /// Errors → InvalidDataException whose message names the file name (never the full path) and, where known, the
    /// key: invalid JSON, root not an object, unknown key, wrong value type, null list entry, failed validation
    /// (the ArgumentException from Validate() is the inner exception).
    public static ClassifierSettings Load(string path);
}
```

## Expected behaviour (test cases)
| # | Case | Expected |
|---|---|---|
| 1 | Header `spf=none …; dkim=none …; dmarc=fail action=none header.from=<from domain>; compauth=pass reason=105` | `CompAuth = Pass`; AuthStatus Unknown; authentication finding 0 points with reason; no +40 |
| 2 | Same as 1 but `compauth=fail reason=000` | AuthStatus Failed; +40 as before |
| 3 | Same as 1, sender is a contact | Tier LikelyLegit, override "Sender is a contact but authentication could not be verified" |
| 4 | `dmarc=pass` + `compauth=pass` | unchanged Authenticated (compauth irrelevant) |
| 5 | Two compauth results (`compauth=pass …; compauth=fail …`) | first counts → Pass |
| 6 | `spf=pass smtp.mailfrom=<aligned>; dmarc=bestguesspass header.from=<from>` | Dmarc verdict None; Authenticated via SPF |
| 7 | `dkim=timeout header.d=<aligned>` | Dkim verdict TempError |
| 8 | Authenticated sender, 5 link hosts of other organisations | no `link-domain-mismatch` finding |
| 9 | Unknown/Failed sender, same links | finding +10 as in AP-07a |
| 10 | Authenticated non-freemail sender whose domain label is absent from the body | no `domain-not-in-body` finding |
| 11 | Authenticated sender, domain label scoring 0.5–0.8 | no `random-domain` finding |
| 12 | Authenticated sender, label scoring ≥ 0.8 | +35 as before |
| 13 | Settings file missing | `ClassifierSettings.Default` |
| 14 | File `{ "likelySpamThreshold": 40 }` | 40 / 70, default lists |
| 15 | File with `"suspiciousTlds": ["zip"]` | list is exactly {"zip"} |
| 16 | Unknown key `"likelySpamTreshold"` | InvalidDataException naming the key |
| 17 | `"certainSpamThreshold": "70"` (string) | InvalidDataException naming the key |
| 18 | Thresholds 70/30 | InvalidDataException, inner ArgumentException |
| 19 | Invalid JSON / root is an array | InvalidDataException; message contains the file name, not the directory |
| 20 | Comments and trailing comma | loads |
| 21 | Positive control: AP-07a smoke case (`qzxkvb73.top`, auth fail) | still CertainSpam |

Existing AP-07a tests that expect content-signal points on **authenticated** mail now contradict E3 – the tester
updates them to the new contract (category c, documented in `test-result.md`).

## Done when
- Build `-warnaserror` green; S2 with the test filter green; README/`docs\classifier.md` updated by the documenter.
- Planner re-runs the PstProbe (`_scratch\PstProbe`): the compauth false positive is gone; junk detection is not worse
  (4 of 6 messages in LikelySpam or CertainSpam).
