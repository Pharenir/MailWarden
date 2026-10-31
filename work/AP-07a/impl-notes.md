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

---

## fix.2 (2026-10-31)

STEP-RESULT: done

Basis: planner decisions C10–C16 (`requirement.md`), review.1 (`review.md`). Commit `d86d0a8` on
`feat/ap-07a-classifier-core`. Build `dotnet build MailWarden.sln -warnaserror` → 0 warnings, 0 errors.
Changed only `src\MailWarden.Core\Classification\**` (12 files changed, 1 new: `ReasonText.cs`) and this file.
No package, no IO, no public signature changed (`HeaderToken` is internal and got a third member).
`tests\`, `test-plan.md`, `test-result.md` not read.

### Decision → change → verification
| # | Change (file:line) | Verified by |
|---|---|---|
| C10 | Tokenizer marks every token `IsJoinedToPrevious` (no whitespace/control/comment before it): `AuthenticationResultsTokenizer.cs:24`, `:43`, `:53`, `:73`, `:93-97`. Parser reads the value after `key =` as the run of joined Word/`=` tokens and concatenates them: `AuthenticationResultsParser.cs:100` (call), `:121-141` (`ReadValue`); key detection no longer needs a Word right after `=`. Domain = part after the last `@` (unchanged, `:189`). Whitespace around `=` still allowed (case 3). Linear: every token visited once in `ReadValue`, no backtracking. | Probe: `"x@victim.test"@attacker.test` → `attacker.test`; `bounces+123-user=example.test@mailer.test` → `mailer.test`; `SRS0=h=tt=example.com=u@fwd.test` → `fwd.test`; `x=dmarc=none@a.test; dmarc=fail header.from=a.test` → Dmarc Fail (also without `;`); injected `x=dkim=pass@a.test` → single DKIM Fail; `header.b=Ab+/Cd==` keeps the following `header.d`; comment directly after a value ends it; `dkim = pass header.d = …`; whole-value quotes; quoted local part with space/`;`/`=`. #1–#5 unchanged. Hostile 1 MB inputs incl. 250 000 joined `"a"=` parts: ≤ 141 ms. End-to-end B1.1/B1.3: spoofed contact with `dmarc=fail` → Failed, not TrustedContact; without DMARC → Unknown. |
| C11 | `AuthStatusEvaluator.cs:20` DMARC fail → Failed before any pass check; `:44` `DescribePass` returns null on DMARC fail (stays consistent with `Evaluate`); SPF fail / all DKIM fail as before (`:30-31`). | Probe #21a (aligned SPF pass + DMARC fail → Failed, `authentication` +40; contact → not TrustedContact, LikelySpam); aligned DKIM pass + DMARC fail → Failed; DMARC none / temperror / bestguesspass + aligned pass → Authenticated; aligned DMARC pass + SPF fail → Authenticated (unchanged). |
| C12 | `AuthStatusEvaluator.cs:50-62`: DMARC pass counts when `Domain` is null (`"DMARC pass"`) or aligned (`:58`); otherwise falls through to DKIM/SPF. | Probe #22a (`dmarc=pass header.from=other.test` → Unknown, no finding); without header.from → Authenticated; `news.example.test` → Authenticated; other-domain pass + aligned DKIM → Authenticated via DKIM; other-domain pass + SPF fail → Failed; contact with other-domain pass → unverified contact (LikelyLegit + override). |
| C13 | `SpamClassifier.cs:103` call, `:115-127` `EnsureValidFinding`: `SignalId` ordinal-equal to `signal.Id`, `Reason` not null/whitespace → `InvalidOperationException("Signal '<id>' returned a result with a different SignalId." / "... without a reason.")`. No message data in the text. XML doc `<exception>` added on `Classify`. | Probe: empty, whitespace, null reason; different id, case-different id (`Stub` vs `stub`), null id → IOE naming `'stub'`; valid stub accepted. |
| C14 | New `ReasonText.Truncate` (`ReasonText.cs:18`): > 100 chars → first 100 + `…` (99 when char 100 would split a surrogate pair). Applied to every message value in reasons: `AuthenticationSignal.cs:34/74`, `DisplayNameMismatchSignal.cs:39`, `DomainNotInBodySignal.cs:44`, `LinkDomainMismatchSignal.cs:58`, `RandomDomainSignal.cs:43`, `ReplyToMismatchSignal.cs:37/43`, `SuspiciousTldSignal.cs:35`, and the pass descriptions `AuthStatusEvaluator.cs:60/68/75` (thus also the contact override text). | Probe #30a: 1 000-char display-name token → reason 179 chars, value = 100 × `a` + `…`. All 7 signals with ~250–300-char values → every reason contains `…`, max 281 chars. Override with long DMARC domain → 158 chars. Short values unchanged (`'qzxkvb73' … (score 0.92)`). Surrogate pair at the cut → no lone surrogate. |
| C15 | N1 `SpamClassifier.cs:183` returns `SpamTier.LikelyLegit` directly (score tier only computed on the score path). N2 `RandomDomainScorer.cs:13` docstring "letters and digits − 1". N3 `SpamClassifier.cs:132` `FromAddress` in the null check → `ArgumentException` (param `input`, "… must not be null"). | Probe: null FromAddress → ArgumentException, ParamName `input`; contact + Unknown → LikelyLegit (#17, C15). N2 docs only. |
| C16 | nothing (AP-07b). | – |

### Probe (`_scratch\ClassifierProbe`, git-ignored, not a test)
`dotnet run --project _scratch\ClassifierProbe` → **126 checks OK, 0 FAIL** ("ALL OK", exit 0). Contains the
previous cases (#1–#17, #19–#25, #27–#29, hostile inputs, 700 kB display name) unchanged, plus new: #18, #26
(stub 29/30/69/70), #30 (all reasons non-empty), #31 (smoke = #13/#14 with empty directory), all C10–C15 examples
above. #11 scores unchanged (scorer untouched apart from the docstring).

Self-check with existing tests (punctual, via watchdog, not a test stage):
- `FullyQualifiedName~AuthenticationResultsParser` → 43/43 passed.
- `FullyQualifiedName~Classifier` → 102/104; the 2 failures are expected contract changes (see note to the tester).

### Tested vs. untested
- Verified by probe: everything in the table above.
- Untested / by reasoning only: Unicode/IDN domains in values; a quoted part after the `@`
  (`x@attacker.test"@victim.test"`, see open question 1); real Gmail/Outlook headers with VERP/SRS (AP-07b).

### Note to the tester – existing contract cases whose behaviour changes
Seen in the self-check output (test names only, file not read):
1. `SpamClassifierTests.AuthStatus_is_derived_from_the_header(header: "mx.test; dmarc=pass header.from=other.test",
   expected: Authenticated)` → now **Unknown** (C12; matches new case 22a).
2. `SpamClassifierTests.AuthStatus_is_derived_from_the_header(header: "mx.test; dmarc=fail header.from=paypal.test;
   dkim=…", expected: Authenticated)` → now **Failed** (C11; DMARC fail is no longer rescued by an aligned DKIM pass).
Other places that could change (not seen failing, worth a look): any case combining `dmarc=fail` with an aligned
SPF/DKIM pass (contacts/allow list then lose TrustedContact, `authentication` gives + points instead of −20); any
DMARC pass whose `header.from` differs from From; parser cases that relied on a value ending at `=` or at a quoted
string (e.g. `header.b=…==` padding, `smtp.mailfrom="…"@…`); reason texts with values > 100 chars (now cut + `…`);
null `FromAddress` (message text changed, still ArgumentException/`input`); custom stub signals whose `SignalId`
differs from `Id` or whose reason is empty now throw `InvalidOperationException`.

### Open questions (fix.2)
1. ~~`AuthenticationResultsParser.cs` – last `@` inside quotes~~ → decided as C17, implemented below.
2. ~~`ISpamSignal.cs` doc~~ → decided as C18, implemented below.

### fix.2 addendum – C17/C18 (commit `021522f`)
| # | Change (file:line) | Verified by |
|---|---|---|
| C17 | Tokenizer flags quoted words `IsQuoted` (`AuthenticationResultsTokenizer.cs:25`, `:60`). `ReadValue` (`AuthenticationResultsParser.cs:125-171`) records the position after the last `@` in an **unquoted** token; the domain part runs from there to the next quoted part or the value end; no unquoted `@` → whole value if no `@` at all (Outlook `smtp.mailfrom=example.test`, `header.d`, `header.from`), `null` if all `@` are quoted (`:171`). Values are stored as `PropertyValue(Text, DomainPart)` (`:229`); `ToDomain` (`:214`) normalises `DomainPart`. "First occurrence wins" also when the first one has no domain. Still linear (each token text scanned once). | Probe: `x@attacker.test"@victim.test"` → `attacker.test`; `"x@victim.test"@attacker.test` → `attacker.test`; `"x@victim.test"` → null; `"a""b@c.test"` → null; `x@"victim.test"` → null; `x@a.test"junk"` → `a.test`; `"q@r.test"x@a.test` → `a.test`; `a@b@c.test` → `c.test`; `example.test`, `"example.test"`, `<x@a.test>`, `@a.test` as before; `header.i="@victim.test"` → DKIM domain null; spoof via quoted domain suffix with contact → Unknown, not TrustedContact. Hostile 1 MB inputs ≤ 174 ms. |
| C18 | `ISpamSignal.cs:5-9` summary states: SignalId must equal `Id` (ordinal), Reason non-empty/non-whitespace, else `Classify` throws `InvalidOperationException` naming the signal id. Doc only. | Build `-warnaserror` (cref resolution). |

Build `-warnaserror` → 0 warnings, 0 errors. Probe → **141 checks OK, 0 FAIL**. Self-check
`FullyQualifiedName~AuthenticationResultsParser` → 43/43 passed (watchdog, not a test stage).

Behaviour change the probe exposed (my own informative #3 expectation, adjusted to C17): a **completely quoted
address value** such as `smtp.mailfrom="Bounce@Example.TEST"` now has **no** SPF domain (before: `example.test`).
Note to the tester: any case with a fully quoted address value in `smtp.mailfrom`/`header.i` now expects domain
null; an SPF/DKIM pass with such a value is no longer aligned.

### fix.2 addendum 2 – C19 (commit `94f3eb2`)
| # | Change (file:line) | Verified by |
|---|---|---|
| C19 | `AuthenticationResultsParser.cs:136` `isSingleQuotedString` (value = exactly one token and it is quoted); `:146` such a token is treated as unquoted, so the `@` rule runs on its content. Mixed values (several joined tokens) unchanged (C17). Value without `@` = domain (question 2 confirmed). | Probe: `"x@victim.test"` → `victim.test`; `"Bounce@Example.TEST"` → `example.test` (probe #3 back to `example.test`); `header.i="@victim.test"` → `victim.test`; `"no-at-sign"` → `no-at-sign`; fully quoted aligned SPF pass → Authenticated; mixed `"x@victim.test""@a.test"` and `"a""b@c.test"` → null; C17 examples unchanged (`attacker.test`). |

Build `-warnaserror` → 0 warnings, 0 errors. Probe → **146 checks OK, 0 FAIL**. Self-check
`FullyQualifiedName~AuthenticationResultsParser` → 43/43 passed (watchdog, not a test stage).
Note to the tester: the "fully quoted address → domain null" note from addendum 1 is **void**; fully quoted values
give the domain of their content again (as before C17).

Remaining edge (no action, by reasoning only; corrected in fix.3 per review.2 N7): inside a single quoted string,
escaped quotes are plain characters after unquoting, so `"x@attacker.test\"@victim.test\""` gives `victim.test"`
**with** the trailing quote (`Normalize` does not strip `"`), which is never aligned with `victim.test`. Only an
unbalanced inner quote (`"x@attacker.test\"@victim.test"`) yields a clean `victim.test` – an envelope address
whose real domain cannot be determined and that a provider would reject; with C11 a provider DMARC fail still wins.

### Open questions (fix.2 addendum) – resolved
Both answered by C19 (question 1) and the planner's confirmation (question 2); kept below for the record.
1. `src\MailWarden.Core\Classification\AuthenticationResultsParser.cs:171` – C17 read literally makes a fully
   quoted address (`smtp.mailfrom="bounce@example.test"`) domain-less. RFC 8601 `pvalue = value / [[local-part] "@"]
   domain-name` with `value = token / quoted-string` would read a whole-value quoted string as the plain value
   (→ `example.test`). If a provider quotes whole values, aligned SPF passes are lost (Authenticated → Unknown for
   real mail; not a security risk, it errs towards less trust). Implemented literally; decide now or verify with
   real headers in AP-07b?
2. Interpretation of C17 "a value without such an `@` has no domain": applied only to values that contain `@`
   exclusively inside quotes; values without any `@` (Outlook `smtp.mailfrom=example.test`, `header.d`,
   `header.from`) keep the whole value as domain – otherwise case 2 would break. Confirm.

---

## fix.3 (2026-10-31)

STEP-RESULT: done

Basis: planner decisions C20–C23 (`requirement.md`, fix.3), review.2 (`review.md`). Commit `34648e9` on
`feat/ap-07a-classifier-core`. Build `dotnet build MailWarden.sln -warnaserror` → 0 warnings, 0 errors.
Changed: `AuthenticationResultsParser.cs`, `AuthStatusEvaluator.cs`, `AuthenticationResults.cs` (internal member
only), this file. No public signature, no package, no IO. `tests\`, `test-plan.md`, `test-result.md` not read.

### Decision → change → verification
| # | Change (file:line) | Verified by |
|---|---|---|
| C20 | `AuthenticationResultsParser.cs:150-160` the value run continues over a separated token when `IsAddressDomainContinuation` (`:213`): a Word starting with `@` that is not a key (`IsKeyAt`, `:117`). Only for property values (`isPropertyValue`, decided before reading, `:99-100`), never for method verdicts. Quoted `"@…"` words are appended too → mixed value → quoted `@` gives no domain (fail-safe). Appending happens before C17/C19. | Probe 3b: `"x@victim.test" @attacker.test`, `"x@victim.test" (c) @attacker.test`, `"x@victim.test"(c)@attacker.test`, `victim.test (c) @attacker.test`, folded `x@victim.test CRLF TAB @attacker.test` → `attacker.test`; `"x@victim.test" "@attacker.test"` → null; `a@b.test @c.test @d.test` → `d.test`; `header.i=victim.test (c) @attacker.test` → DKIM `attacker.test`; following `; dmarc=fail` intact; `dkim=pass @x.test header.d=a.test` → verdict Pass. End-to-end: spoofed contact via SPF and via Gmail-style `header.i` → Unknown, not TrustedContact. |
| C21 | `:141-146` empty value when the token after `=` is missing (statement end) or is separated by whitespace/comment **and** is itself a key; `IsKeyAt` now needs only `position + 1 < end`, so `key=` at the statement end is a present property with empty value. `key = value` (case 3) unchanged because `value` is not followed by `=`. | Probe 3c: `spf=pass smtp.mailfrom= dmarc=fail header.from=a.test` → Dmarc Fail, SPF domain null; `smtp.mailfrom=;` / `header.d=` at the end → domain null; `spf= dkim=fail …` → SPF Unknown, DKIM Fail parsed; `spf = pass smtp.mailfrom = a@b.test` → Pass `b.test`; #3 unchanged. |
| C22 | Parser records whether the kept (first) DMARC result had a `header.from` that was present but domain-less (`:334`) → `AuthenticationResults.IsDmarcHeaderFromWithoutDomain` (internal init, `AuthenticationResults.cs:26`, set at `AuthenticationResultsParser.cs:348`). `AuthStatusEvaluator.cs:56` trusts a domain-less DMARC pass only when that flag is false (header.from absent); otherwise C12 "otherwise" rules apply. | Probe 22b: `header.from=""`, `header.from=` (end), `header.from= spf=none`, `header.from="a""b@c.test"`, `header.from=.` → Unknown, no `authentication` finding; absent header.from → Authenticated; domain-less DMARC pass + aligned DKIM → Authenticated via DKIM; only the first DMARC counts; contact → unverified (LikelyLegit + override). |
| C23 | N5: `ReadValue` comment (`AuthenticationResultsParser.cs:134`) example now `"a""b@c.test"` → null, C19 mentioned. N7: "Remaining edge" in fix.2 addendum 2 corrected (`victim.test"`, not aligned). N8 not mine (PLAN.md). | Reading. |

Design note C22: the flag lives on `AuthenticationResults` (not on `AuthMethodResult`) so that record equality of
`AuthMethodResult` values (e.g. `Dmarc == new AuthMethodResult(Pass, null)`) is unaffected; `AuthenticationResults`
equality is only affected for headers with a present but domain-less DMARC `header.from`. Custom signals cannot see
the flag (internal); they see `Dmarc` = Pass/null as before, but `context.AuthStatus` already reflects C22.

### Probe and self-check
- `dotnet run --project _scratch\ClassifierProbe` → **174 checks OK, 0 FAIL** (all previous cases 1–31, C10–C19,
  plus the C20–C22 cases above). Hostile 1 MB inputs incl. 150 000 `(c) @a` continuations and 330 000 empty
  `k= ` values: ≤ 180 ms (linear; each token visited once, never throws).
- Watchdog self-check (not a test stage): `FullyQualifiedName~AuthenticationResultsParser` 49/49,
  `FullyQualifiedName~Classifier` 115/115 passed.

### Tested vs. untested
- Verified by probe: everything in the table.
- Untested / by reasoning: real provider headers with CFWS inside pvalues (none known, AP-07b); Unicode in values.

### Note to the tester
- New behaviour can affect existing cases only where a property value is followed by a separated `@…` word, where
  `key=` is followed by whitespace and another `key=` (previously the next pair was swallowed as the value), where
  `key=` stands at the statement end (previously the property was absent, now present with domain null – for
  `header.from` that turns a DMARC pass untrusted), and DMARC pass with a present but domain-less `header.from`.
  The self-check above showed no failing existing test.

### Open questions (fix.3)
1. `src\MailWarden.Core\Classification\AuthenticationResultsParser.cs:213` – per C20 an `@…` word that is itself a
   key (`x@victim.test @attacker.test=foo`) is not appended, so the domain stays `victim.test`. A real domain cannot
   contain `=`, so this needs a provider writing invalid data; no action unless the planner wants the fail-safe
   (domain null) for that case too.
2. `src\MailWarden.Core\Classification\AuthenticationResultsParser.cs:141` – C21 read as "empty value when the
   next separated word is a key"; a literal "`key=` + whitespace → always empty" would break case 3
   (`SPF = FAIL`, `dkim = pass`). Interpretation consistent with review.2 N6 – confirm.

---

## fix.4 (2026-10-31)

STEP-RESULT: done

Basis: planner decisions C25–C27 (`requirement.md`, fix.4), review.3 (`review.md`). Commit `4cf8cf2` on
`feat/ap-07a-classifier-core`. Build `dotnet build MailWarden.sln -warnaserror` → 0 warnings, 0 errors.
Changed: `AuthenticationResultsTokenizer.cs`, `AuthenticationResultsParser.cs`, this file. No public signature, no
package, no IO. `tests\`, `test-plan.md`, `test-result.md` not read.

### Decision → change → verification
| # | Change (file:line) | Verified by |
|---|---|---|
| C25 | `AuthenticationResultsTokenizer.cs:104-125` `SkipComment` skips a `"…"` inside a comment with `ReadQuotedString` (`:121`, backslash escapes honoured, unterminated → input end), so `(`/`)` inside it neither nest nor close. Backslash-escaped `\"` in a comment is still no quote. Single pass: the quoted part is consumed once by `ReadQuotedString`. | Probe 3d: `spf=pass (sender "x) smtp.mailfrom=victim.test y "@attacker.test) smtp.mailfrom=attacker.test; dmarc=fail header.from=victim.test` → SPF `attacker.test`, Dmarc Fail. Review.3 S3 Gmail-style header → AuthServId `mx.google.com`, SPF `attacker.test`, Dmarc Fail; spoofed contact → Failed, LikelySpam (not TrustedContact). `(c "(" d)`, nested `(a (b ")" c) d)`, escaped `(a \" b)` → comment ends at the provider's `)`. Unterminated quote in a comment → rest of the header swallowed (SPF Pass domain null, no DMARC – "less trust" direction). #1–#3 unchanged. |
| C26 | `AuthenticationResultsParser.cs:114` a new method gets `hasVerdict: !IsNullOrWhiteSpace(value)`; `PendingMethod.HasVerdict` (`:287`); `ResultCollector.Add` returns early for it (`:331`). The empty-verdict method still collects its properties (so `spf= smtp.mailfrom=victim.test` cannot leak its property into anything) but is never kept. | Probe 3e: `dmarc=; dmarc=fail header.from=a.test` → Dmarc Fail; `spf= smtp.mailfrom=victim.test; spf=fail smtp.mailfrom=a.test` → SPF Fail `a.test`; `dmarc=" "` not kept; `dkim=;` not kept; `dmarc=;` alone → `Empty` (same instance); `dmarc=maybe; dmarc=fail` → first (Unknown) kept as before. Probe check `spf= dkim=fail …` adjusted: SPF now null (was Unknown under C21). |
| C27 | `AuthenticationResultsParser.cs:22-25` `Parse` doc: C20 (`@…` after CFWS), C21 (empty values), C26. `:121` "A Word followed by '='". | Reading; build (XML doc). |

### Probe and self-check
- `dotnet run --project _scratch\ClassifierProbe` → **190 checks OK, 0 FAIL** (all earlier cases incl. 1–31,
  C10–C22, plus C25/C26 above). Hostile 1 MB inputs incl. 500 000 `("`, 150 000 `("a)\""`, 150 000 `dmarc=;`:
  ≤ 169 ms (linear, never throws).
- Watchdog self-check (not a test stage): `FullyQualifiedName~AuthenticationResultsParser` 53/53,
  `FullyQualifiedName~Classifier` 117/117 passed.

### Tested vs. untested
- Verified by probe: everything in the table.
- Untested / by reasoning: whether Gmail/Outlook escape `)`/`"` in their comments (AP-07b with real headers);
  a provider comment with a stray unbalanced `"` would now swallow the rest of the header (fail-safe: results after
  it are lost; if that drops a provider `dmarc=fail`, the status could become Unknown instead of Failed – but only
  when the provider writes an unbalanced quote into its own comment).

### Note to the tester
- Changed behaviour: comments containing `"…"` with `(`/`)` inside; a comment with an unbalanced `"` now runs to the
  input end; a method with an empty verdict (`spf=`, `dmarc=;`, `dkim=""`) is no longer a result (previously, since
  fix.3, a result with verdict Unknown) – any existing case asserting `Spf/Dmarc` Unknown for an empty verdict
  changes to null / a later result. Self-check showed no failing existing test.

### Open questions (fix.4)
- None. (Untested provider escaping behaviour is noted above for AP-07b.)
