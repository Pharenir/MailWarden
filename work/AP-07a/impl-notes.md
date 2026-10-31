STEP-RESULT: done

# AP-07a – Implementation notes (Builder)

Branch `feat/ap-07a-classifier-core`. Build: `dotnet build MailWarden.sln -warnaserror` → 0 warnings, 0 errors.
Only new files under `src\MailWarden.Core\Classification\`; no AP-01 file changed, no package reference, no IO.
Nothing created under `tests\`. `PLAN.md` and `requirement.md` not changed.

## Files
| File | Content |
|---|---|
| `SpamTier.cs`, `AuthVerdict.cs`, `AuthStatus.cs`, `ListMembership.cs` | enums as in the contract |
| `AuthMethodResult.cs`, `AuthenticationResults.cs` (+ `Empty`) | parsed header records |
| `ClassificationInput.cs`, `ClassificationContext.cs`, `SignalResult.cs`, `ClassificationResult.cs` | records as in the contract |
| `ISenderDirectory.cs`, `ISpamSignal.cs` | interfaces |
| `ClassifierSettings.cs` | record, `Default` (FrozenSets, OrdinalIgnoreCase), `Validate()`, internal case-insensitive lookups |
| `DomainNames.cs` | `GetDomain`, `GetOrganizationalDomain`, `GetRegistrableLabel`, `GetPublicSuffix`, `AreAligned` + internal non-throwing `TryGetDomain`/`TryGetOrganizationalDomain`/`Normalize` |
| `AuthenticationResultsTokenizer.cs` (internal) | hand-written single-pass tokenizer (no regex) |
| `AuthenticationResultsParser.cs` | statement/pair parser on top of the tokenizer |
| `AuthStatusEvaluator.cs` (internal) | AuthStatus rules + pass description for reason/override texts |
| `RandomDomainScorer.cs` | label scorer (formula below) |
| `Signals\{Authentication,RandomDomain,SuspiciousTld,DomainNotInBody,DisplayNameMismatch,ReplyToMismatch,LinkDomainMismatch}Signal.cs` (internal sealed) | the seven built-in signals, ids/points as in the table |
| `SpamClassifier.cs` | ctor validation, `CreateDefaultSignals()`, `Classify()` with the decision order |
| `work\AP-07a\impl-notes.md` | this file |

Note for S-level selection: `ClassifierSettings.cs` and `I*.cs` are "central building blocks" in the project
CLAUDE.md → S3 trigger.

## Deviations from the contract / decisions where the contract is open
None in names, namespaces, signatures, ids or points. Decided details:
- Built-in signal classes are `internal` (contract only exposes them via `CreateDefaultSignals()`); namespace
  `MailWarden.Core.Classification.Signals`.
- Parser:
  - Hand-written tokenizer instead of regex (linear time; 1 MB hostile inputs parse in < 100 ms in the probe).
    Comments nested with `\` escapes; unterminated comment/quote ends at input end.
  - Quoted strings are opaque single values (quotes stripped, never split on `;`/`=`); a quoted property value such
    as `header.d="example.test"` is therefore used as the value.
  - Each `;` statement: first `key=value` pair is the method, later pairs are its properties. A later key `spf`,
    `dkim` or `dmarc` starts a new result even without `;` (so `dmarc=fail spf=fail dkim=fail` parses as three
    results). Stray tokens (version numbers, base64 `=` padding of `header.b`) are skipped.
  - Method version suffix is stripped (`dkim/1` → `dkim`).
  - Only the **first** SPF and the first DMARC result are kept; DKIM keeps all in order.
  - `hardfail` → `Fail` (obsolete RFC 5451 synonym), all other unknown words → `Unknown`.
  - The keyword `none` never becomes the AuthServId (`"none"` alone → `Empty`). Otherwise the contract rule is
    applied literally, e.g. `"%%%;;=="` → AuthServId `%%%`, nothing else (partially parsed).
  - Domain values: trimmed, `<>` stripped, part after the last `@`, trailing (and leading) dots removed, lower-case;
    empty → null.
  - Returns `AuthenticationResults.Empty` (same instance) when nothing at all was parsed.
- `DomainNames`:
  - Empty labels are dropped (`a..b`); input without any label → ArgumentException (also `GetDomain("a@...")`).
  - Multi-label suffix list contains the required ones plus `me.uk, ltd.uk, plc.uk, edu.au, gov.au, ne.jp, or.jp,
    net.nz, org.nz, net.cn, org.cn, co.at, or.at, co.in, co.kr, com.mx, com.ar, com.sg, com.hk, com.tw`.
  - Multi-label suffixes are only recognised with ≥ 3 labels: `co.uk` alone → org `co.uk`, suffix `uk`, label `co`.
  - Single-label input: org domain and registrable label = the label, public suffix = `""`.
  - Exception messages never contain the address (personal data).
- `ClassifierSettings.Validate()`: whitespace-only entries count as empty; a null list → ArgumentException.
  Lookups first use the set's own comparer, then a linear OrdinalIgnoreCase scan (entries trimmed), so sets with
  ordinal comparers still match case-insensitively.
- `SpamClassifier`:
  - Duplicate signal ids are compared **case-insensitively**; null signal or empty id → ArgumentException.
  - `Classify`: null `FromName`/`AuthenticationResultsHeaders`/`BodyText`/`LinkHosts` or a null header at index 0 →
    ArgumentException (paramName `input`); FromAddress without `@` or without domain → ArgumentException.
  - Score sum is `checked` (overflow from custom signals → OverflowException).
  - Contact + Unknown always yields LikelyLegit (the lowest score tier), as specified.
  - The classifier does not validate custom `SignalResult`s (e.g. empty reason).
- Signals:
  - From is freemail when the From domain **or** its organisational domain is in `FreemailDomains`; same rule for
    the Reply-To freemail check.
  - `domain-not-in-body`: whitespace-only body counts as empty (no finding).
  - `reply-to-mismatch`: a Reply-To without usable domain (no `@`) gives no finding.
  - `display-name-mismatch`: tokens = runs of letters/digits/`.-@_+`; token trimmed of `.`/`-`; address → domain part;
    domain-like = ≥ 2 non-empty labels of ASCII letters/digits/`-`, last label ASCII letters only, 2–24 chars. First
    mismatching token is reported.
  - `link-domain-mismatch`: hosts normalised (trim, trailing dot, lower-case), empty/label-less ignored, distinct
    ordinal; 80 % checked as `notAligned * 5 >= distinct * 4`.
  - `random-domain` reason shows the score truncated (not rounded) to two decimals, so the text never shows 0.80
    for a raw score below 0.8.
  - Reasons with numbers are formatted with `CultureInfo.InvariantCulture`.

## RandomDomainScorer formula
Label lower-cased (invariant). Length without `-` < 5 → 0.0. Features in one pass:
- vowel deficit `V = clamp((0.30 − vowels/letters) / 0.30)`; vowels = `aeiouy` + common accented vowels; 0 when the
  label has no letters;
- consonant run `C = clamp((longest consonant run − 3) / 4)`; digits, `-` and other characters break runs;
- rare bigrams `B` = share of adjacent letter pairs that are two consonants **not** in a built-in table of ~130
  common English/German consonant clusters (`ch, ck, sch→sc, st, sp, tr, rk, nd, ng, pf, tz, zw, …`);
- alternation `A` = 0 for < 3 letter↔digit changes, else `clamp((changes/(alphanumerics − 1) − 0.4) / 0.4)`.

`Score = clamp(max(A, 0.4·V + 0.3·C + 0.3·B))`. Pure arithmetic → deterministic, culture-invariant.

Scores of the example labels (probe output, 3 decimals):

| Random (#10, must be ≥ 0.8) | Score | Plausible (#11, must be < 0.5) | Score |
|---|---|---|---|
| qzxkvb73 | 0.925 | hotmail | 0.050 |
| xkqzvbnm | 1.000 | googlemail | 0.000 |
| bvtrkzpq | 0.914 | amazon | 0.000 |
| a8f3k2l9m | 1.000 | paypal | 0.000 |
| zqwxjkvh | 1.000 | sparkasse | 0.000 |
| hjkdfgtrw | 0.888 | telekom | 0.000 |
| x7k2p9q4 | 1.000 | check24 | 0.133 |
| mnbvcxzl | 1.000 | zalando | 0.000 |
| wqzrtpx8 | 0.950 | switchup | 0.110 |
| kjhgfdsqw | 0.963 | bundeswehr | 0.000 |
| | | lieferando | 0.000 |
| | | netflix | 0.069 |
| | | spotify | 0.000 |
| | | mediamarkt | 0.000 |
| | | kfzteile24 | 0.161 |
| | | schwarzkopf | 0.293 |
| | | ebay-kleinanzeigen | 0.000 |
| | | versicherung | 0.000 |
| | | rechtsanwalt | 0.169 |
| | | 1und1 | 0.000 |

#12: `dhl`, `gmx`, `dkb`, `xkcd` → 0.000. Margins: lowest random 0.888, highest plausible 0.293.
Known limitation: purely numeric labels (`12345678`) score 0.0 (no letters, no alternation).

## Verified by probe (`_scratch\ClassifierProbe`, git-ignored, not a test)
All checks OK (`dotnet run --project _scratch\ClassifierProbe`):
- #10, #11, #12 (scores above), #13 (LikelyLegit, −20, Authenticated), #14 (CertainSpam, 120 with all five
  findings; also with header `dmarc=fail spf=fail dkim=fail` without `;`), #21, #22, #23, #24 (both variants),
  #25 (all three variants).
- Additionally, informative: #1, #2, #3 (mixed case, comment with `;`/`=`/nesting, quoted value, folded CRLF+TAB),
  #4, #5 (several garbage inputs, null), #6, #7, #8, #9, #15, #16, #17, #19, #20 (both), #27 (70/70, likely 0,
  empty TLD entry via ctor), #28, #29; hostile 1 MB headers (linear), 700 kB display name.

Untested / only by reasoning: #18, #26 (stub signal thresholds 29/30/70), #30 (reason texts – visually checked in
the probe output for 13–25, not systematically), #31 smoke (equivalent to #13/#14 with an empty directory –
covered by the probe setup), Unicode/IDN labels, `bestguesspass` from Outlook.

## Open questions
1. `src\MailWarden.Core\Classification\AuthenticationResultsParser.cs:131` – Outlook.com writes
   `dmarc=bestguesspass` when the domain has no DMARC record. Per contract it maps to `Unknown` (not Pass). Verify
   against real headers in AP-07b and decide whether it should count as Pass.
2. `src\MailWarden.Core\Classification\AuthenticationResultsParser.cs:131` – `hardfail` mapped to `Fail` (RFC 5451
   synonym) although the contract says unknown words → Unknown. Keep?
3. `src\MailWarden.Core\Classification\AuthenticationResultsParser.cs:67` – the keyword `none` is never taken as
   AuthServId; everything else literally follows the contract (`"%%%;;=="` → AuthServId `%%%`). OK?
4. `src\MailWarden.Core\Classification\Signals\DisplayNameMismatchSignal.cs:26` – the contract's "domain-like token"
   rule also matches names such as `J.Doe` or `Mr.Smith` (TLD `doe`/`smith`) → false +20. Possible refinement
   later: require a known TLD list. Left as specified.
5. `src\MailWarden.Core\Classification\SpamClassifier.cs:34` – duplicate signal ids compared case-insensitively
   (stricter than "duplicate Ids"). OK?
6. `src\MailWarden.Core\Classification\SpamClassifier.cs:135` – freemail = From domain **or** organisational domain
   in the list (e.g. `mail.gmx.net` counts as freemail). OK?
7. `src\MailWarden.Core\Classification\Signals\ReplyToMismatchSignal.cs:21` – Reply-To without `@` → no finding
   (instead of e.g. +10). OK?
8. `PLAN.md` column *Umgesetzt* not filled (coordinator rule: builder does not edit `PLAN.md`); AP-07 rows 2, 3
   (tier part), 4 (tier part), 5, 6, 8 are implemented in Core; rows 1, 4a, 4b, 7 belong to AP-07b/AP-08.
