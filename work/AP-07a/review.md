# AP-07a – Review

## review.1 (2026-10-31)

Scope: `git diff e5eea7e 4760f62 -- . ":(exclude)tests"` (26 new files under `src\MailWarden.Core\Classification\`,
plus `work\AP-07a\`). Files read via `git show 4760f62:<path>`. `tests\`, `test-plan.md`, `test-result.md` not read.
Build not run by the reviewer (worktree changes in parallel); builder reports `-warnaserror` green.
All findings below are from reading the code, nothing was executed.

**Verdict: BLOCK** (1 BLOCK, 1 SHOULD, 4 NIT, 3 questions for the planner)

### What is fine
- Contract: names, namespaces, signatures, ids, points, decision order 3–7 and C1–C9 match the requirement.
  Core stays BCL-only, no IO, no AP-01 file touched, no duplicate of existing helpers (AP-01 has no domain code).
- Parser runtime is linear: single-pass tokenizer, `FindIndex` per statement from the previous end, no regex,
  no backtracking; unterminated comments/quotes end at input end; never throws for non-null input.
- AuthStatus rules, alignment through the organisational domain (missing/label-less domain never aligned), only
  header index 0, "authentication" sum/cap/no-finding-at-0, all reason texts name the judged value, no addresses
  in exceptions or reasons, `CultureInfo.InvariantCulture` for numbers, `RandomDomainScorer` is pure arithmetic on
  `ToLowerInvariant` text with ordinal lookups (deterministic, culture-invariant).
- Docstrings on all public and internal members.

### BLOCK
**B1 – property values (RFC 8601 `pvalue`) are split at '=' and at a quoted local part; the SPF/DKIM domain can be
wrong, in one variant falsely aligned.**
`src\MailWarden.Core\Classification\AuthenticationResultsTokenizer.cs:47-58` (quoted string and '=' always end the
current word) together with `AuthenticationResultsParser.cs:84-96` (value = exactly one Word token) and `:141-153`.
RFC 8601 allows `pvalue = value / [ [ local-part ] "@" ] domain-name`; `local-part` may be a quoted string and its
unquoted form may contain '=' (atext). Consequences:
1. `smtp.mailfrom="x@victim.test"@attacker.test` → tokens `"x@victim.test"`, `@attacker.test` → value
   `x@victim.test` → Spf domain `victim.test`. The receiving provider checked SPF for `attacker.test`. With From
   `boss@victim.test` the message becomes **Authenticated** (SPF pass "aligned", checked before DMARC fail is even
   looked at), gets −20 and, if the sender is a contact, **TrustedContact** – exactly what positive control #16
   is meant to prevent.
2. VERP/SRS envelope senders (`bounces+123-user=example.com@em.example.test`, `SRS0=h=tt=example.com=u@fwd.test`)
   unquoted → value = text before the first '=' → Spf domain `bounces+123-user` / `srs0` (wrong, aligned SPF pass
   lost).
3. Local part `x=dmarc=none@a.test` (or `x=dkim=...`) unquoted: the inner `dmarc=none@a.test` forms a new pair,
   `IsEvaluatedMethodName` starts a DMARC result (verdict Unknown), and because only the first DMARC is kept the
   provider's real `dmarc=fail` later in the header is dropped → Failed downgraded to Unknown (contact + Unknown →
   LikelyLegit with override instead of score tier). Same for an injected `dkim=` defeating "all DKIM fail".
I am unsure whether Gmail writes these values unquoted or quotes the whole value (whole-value quoting would be parsed
correctly); the RFC form above is valid and the parser is specified as robust for untrusted input, so the domain
extraction contract ("address part after '@'") is not met for valid headers.
Suggested fix: after `key =` read the value as one contiguous run up to whitespace, ';' or '(' – concatenating
adjacent word, quoted and '=' parts without separator (e.g. tokenizer marks "no whitespace before" per token, parser
joins). Then `LastIndexOf('@')` picks the real domain and inner `x=dmarc=...` can no longer start a method.
Tester: add cases for 1–3 (quoted local part with '@', '=' in local part, injected `dmarc=` in local part before
the real DMARC).

### SHOULD
**S1 – the classifier does not enforce "no silent points" for custom signals.**
`src\MailWarden.Core\Classification\SpamClassifier.cs:96-104`. Project rule (CLAUDE.md): every `ISpamSignal` returns
points with a reason text. The only place that can enforce it for third-party signals is `Classify`; currently a
finding with empty/whitespace `Reason`, or a `SignalId` different from `signal.Id`, is summed and returned (impl-notes
"does not validate custom SignalResults"; not covered by C1–C9). Fail fast: throw `InvalidOperationException` naming
the signal id (no message data) when `Reason` is null/whitespace or `SignalId` does not equal `signal.Id`
(ordinal-ignore-case, consistent with C5). Built-in signals are unaffected.

### NIT
- **N1** `src\MailWarden.Core\Classification\SpamClassifier.cs:164` – `scoreTier > LikelyLegit ? LikelyLegit :
  scoreTier` always yields `LikelyLegit` (score tiers never return TrustedContact); per C8 write `SpamTier.LikelyLegit`
  directly, the ternary suggests a case that cannot occur.
- **N2** `src\MailWarden.Core\Classification\RandomDomainScorer.cs:13` – docstring says
  `changes / (characters - 1)`, code (`:177`) and impl-notes use alphanumerics − 1.
- **N3** `src\MailWarden.Core\Classification\SpamClassifier.cs:113-122` – null `FromAddress` ends in "FromAddress must
  contain '@'..." while the other members get the null message; add `FromAddress` to the null check for a matching
  message.
- **N4** `Signals\DisplayNameMismatchSignal.cs:39`, `Signals\LinkDomainMismatchSignal.cs:58`,
  `Signals\ReplyToMismatchSignal.cs:37/43` – attacker-controlled values are copied unbounded into `Reason`
  (a 700 kB display-name token yields a 700 kB reason that AP-07b will persist). Consider truncating judged values
  (e.g. 100 chars + "…") – or decide it in AP-07b with the storage of decisions.

### Questions for the planner (contract level, not builder findings)
- **Q1 – Authenticated overrides the provider's DMARC fail.** `AuthStatusEvaluator.cs:17-24` implements the contract
  literally: an own "aligned" DKIM/SPF pass wins over `dmarc=fail`. With relaxed alignment a provider DMARC fail plus
  an aligned SPF/DKIM pass is contradictory; it only happens when our approximation disagrees with the provider – B1,
  or the missing public suffix list: unknown multi-label suffixes (`com.pl`, `co.il`, `com.ua`, `co.th`, `github.io`,
  `herokuapp.com`, …) make `evil.com.pl` "aligned" with `bank.com.pl`, so an attacker's SPF pass authenticates a
  spoofed contact. Recommendation: when a DMARC result is present (pass or fail), trust it and use own alignment only
  when DMARC is absent/none/temperror; at least let DMARC Fail veto Authenticated.
- **Q2 – DMARC pass without checking `header.from`.** `dmarc=pass header.from=other.test` with From `example.test`
  counts as Authenticated (contract: "DMARC = Pass"). Normally identical; differs with multiple/odd From headers or
  extraction differences in AP-07b. Require `Dmarc.Domain` null or aligned with From?
- **Q3 – authserv-id is not checked (for AP-07b).** Index 0 is only trustworthy if the provider always prepends its
  own header. Where it does not (internal delivery, some Outlook paths), a sender-forged header at index 0 decides.
  AP-07b extraction could pass only headers whose authserv-id matches the provider profile (`mx.google.com`; for
  Outlook.com, which has none, another criterion).

## review.2 (2026-10-31)

Scope: fix.2 `git diff f28d11a 8a96d44 -- src` (commits `d86d0a8`, `021522f`, `94f3eb2`), checked against the full
AP-07a diff `git diff e5eea7e 8a96d44 -- . ":(exclude)tests"`, requirement C10–C19, impl-notes fix.2 incl. addenda.
Files read via `git show 8a96d44:<path>`; the worktree `src\` is identical to `8a96d44`. `tests\`, `test-plan.md`,
`test-result.md` not read. Build not run by the reviewer (a tester run is active in the worktree; builder reports
`-warnaserror` green). Everything below is from reading the code ("by reasoning"), nothing was executed.

**Verdict: APPROVE** (0 BLOCK, 1 SHOULD, 4 NIT, 2 questions for the planner). S2 is cheap and recommended before the
merge; the planner decides whether it goes into a fix.3 or into AP-07b.

### Focus items
- **B1 / C10 – fixed.** Tokenizer marks `IsJoinedToPrevious` (`AuthenticationResultsTokenizer.cs:21-25`, `:60`,
  `:67`, `:79-83`); `ReadValue` (`AuthenticationResultsParser.cs:126-181`) joins the contiguous Word/`=`/quoted run
  after `key =`; pair detection (`:91-93`) only fires on tokens that are not part of a value run, so `x=dmarc=none@…`,
  `x=dkim=pass@…`, VERP/SRS and `header.b=…==` no longer start pairs or results. All three B1 variants resolved.
- **C17 – correct.** Domain = text after the last unquoted `@` up to the next quoted part (`:146-163`, `:170-177`);
  all-quoted `@` → null; no `@` at all → whole value. `"x@victim.test"@attacker.test` and `x@attacker.test"@victim.test"`
  both → `attacker.test`.
- **C19 – correct as specified** (`:136`, `:146`): exactly one quoted token is unquoted, then the `@` rule runs on its
  content; mixed values keep C17. See S2 for a gap that C19 opens together with C10.
- **C11/C12 – correct.** `AuthStatusEvaluator.cs:20-23` DMARC fail → Failed before any pass; `:44-47` keeps
  `DescribePass` consistent with `Evaluate`; `:50-62` DMARC pass counts only with null or aligned `header.from`,
  otherwise falls through to DKIM/SPF. AuthenticationSignal points unchanged and consistent (DMARC fail now always
  reaches the failure branch).
- **S1 / C13, C18 – correct.** `SpamClassifier.cs:103`, `:115-127`: SignalId ordinal-equal, Reason not
  null/whitespace, `InvalidOperationException` naming only the signal id, checked before the score is summed. Doc on
  `Classify` and `ISpamSignal.cs:5-8` match.
- **N1–N3 / C15 – fixed** (`SpamClassifier.cs:180-184`, `:132-137`, `RandomDomainScorer.cs:13`).
- **N4 / C14 – fixed.** `ReasonText.Truncate` (100 chars + `…`, no split surrogate pair) applied to every message value
  in all 7 signals and in `DescribePass` (thus also the contact override). No other value reaches a reason or an
  exception message. No duplicate helper elsewhere in `src\`.
- **Linear / never throws – confirmed.** Tokenizer single pass without recursion (comment depth counter, escapes
  `+= 2` bounded by the loop); `FindIndex` per statement from the previous end (start == Count allowed);
  `ParseStatement`/`ReadValue` advance monotonically, each token text scanned once; the domain slice satisfies
  `domainStart <= domainEnd <= length` in all branches; `ToDomain`/`Normalize`/`TrySplitLabels` do not throw.
- **Builder's remaining edge (escaped quotes inside one quoted string) – acceptable, and safer than described.**
  `"x@attacker.test\"@victim.test\""` unquotes to `x@attacker.test"@victim.test"`; the last `@` gives
  `victim.test"` **with** the trailing quote (`Normalize` does not strip `"`, `DomainNames.cs:127-130`), whose
  organisational domain `victim.test"` is never aligned with `victim.test`. The only variant that yields a clean
  `victim.test` needs an unbalanced inner quote (`"x@attacker.test\"@victim.test"`), i.e. an envelope address whose
  real domain cannot be determined; a provider would reject it. No action needed (impl-notes wording: see N3).

### SHOULD
**S2 – a quoted (or unquoted) local part separated from `@domain` by whitespace or a comment yields the local part's
domain; with C19 this can make a spoofed sender Authenticated.**
`AuthenticationResultsParser.cs:128-132` (value ends at the first non-joined token), `:136`/`:146` (C19 unquoting),
`:94-97` (the following `@attacker.test` is skipped as a stray token).
RFC 8601 `pvalue = [CFWS] ( value / [ [ local-part ] "@" ] domain-name ) [CFWS]` takes `local-part` from RFC 5322,
where `quoted-string` and `dot-atom` carry optional CFWS, so `"x@victim.test" @attacker.test` and
`"x@victim.test"(c)@attacker.test` are valid pvalues for the domain `attacker.test`. The parser reads the value as
the single quoted string → C19 → domain `victim.test`; same for the unquoted `victim.test (c) @attacker.test` (value
without `@` → domain `victim.test`). With an SPF pass (or, Gmail-style, a DKIM pass whose domain comes from
`header.i` because Gmail writes no `header.d`) and From `…@victim.test` the message is **Authenticated** (−20;
contact → **TrustedContact**) whenever the provider does not report `dmarc=fail` for the victim – i.e. for victim
domains without a DMARC record, which is common for private contacts' own domains. C11 does not help there.
Before C19 the quoted variant gave null (C17), so C19 reopened it; the unquoted variant existed since review.1.
Likelihood: needs a provider that writes CFWS inside a pvalue (not valid in an RFC 5321 path, not a valid DKIM
`sig-i-tag`; Gmail/Outlook are not known to do it) – hence SHOULD, not BLOCK.
Suggested fix (linear, a few lines in `ReadValue`/`ParseStatement`): after the joined run, if the next token in the
statement is a Word that starts with `@` and is not a key (not followed by `=`), append it and its joined run to the
value before applying C17/C19. Alternative fail-safe: a value immediately followed by such an `@…` token gets domain
null. Tester: `smtp.mailfrom="x@victim.test" @attacker.test`, `smtp.mailfrom="x@victim.test"(c)@attacker.test`,
`header.i=victim.test (c) @attacker.test` → domain `attacker.test` (or null), never `victim.test`.

### NIT
- **N5** `AuthenticationResultsParser.cs:120-123` – the ReadValue comment still says
  `"\"x@victim.test\"" → null`; since C19 that value gives `victim.test` (`:134-135` say so). Example should be a mixed
  all-quoted value, e.g. `"a""b@c.test"`.
- **N6** `AuthenticationResultsParser.cs:101`, `:128` – the first token after `key =` is taken even when separated by
  whitespace (needed for case 3 `dkim = pass`). An empty value followed by whitespace and no `;`
  (`smtp.mailfrom= dmarc=fail header.from=a.test`) swallows the next pair as value `dmarc=fail`, so a provider DMARC
  fail is lost (Failed → Unknown). Only for sloppy headers without `;`; not attacker-controlled. Optional: do not take
  a non-joined first token that is itself followed by `=`.
- **N7** impl-notes fix.2 addendum 2, "Remaining edge": the result is `victim.test"` (not aligned), not
  `victim.test` – see focus item above. Notes only.
- **N8** `PLAN.md:141-153` – AP-07 columns *Umgesetzt*/*Getestet* still empty (coordinator task, unchanged since
  review.1). Row 1 reads "SPF/DKIM pass → TrustedContact"; with C11 an SPF/DKIM pass under a provider DMARC fail no
  longer qualifies – wording could reference C11 when the row is marked.

### Checklist (fix.2 diff)
Correctness: as above. Architecture: Core-only, BCL-only, no new dependency, no AP-01 file touched, `HeaderToken`
stays internal, public signatures unchanged. Data: n/a (no IO). Security: no message data in exceptions, reasons
bounded, no new trust path except S2. Consistency: all reason texts use `ReasonText.Truncate`; all three pass
descriptions and both override paths consistent with `Evaluate`. Readability: names and comments fine apart from N5;
no dead code (`scoreTier` ternary removed), no debug output. Uniqueness: `ReasonText` is the only truncation helper.
Plan: see N8.

### Questions for the planner
- **Q4 (S2)** Fix now (fix.3, small) or verify with real headers in AP-07b? If deferred, add the fail-safe variant
  (domain null) at least, because the risk direction is "too much trust".
- **Q5** C12 treats a DMARC pass with domain null as authenticated. Null currently has two meanings: `header.from`
  absent (intended) and `header.from` present but without a determinable domain per C17 (e.g. `"a@b""c@d"`). The
  second is ambiguous input and should rather not grant trust. Distinguish (e.g. keep "property present" in the
  parser and give such a DMARC pass no effect)? Low likelihood; can go to AP-07b with the authserv-id check (C16).

## review.3 (2026-10-31)

Scope: fix.3 `git diff 8b2b155 be10e1e -- src` (`AuthenticationResultsParser.cs`, `AuthStatusEvaluator.cs`,
`AuthenticationResults.cs`) plus interactions, against requirement C20–C24 and impl-notes fix.3. Files read via
`git show be10e1e:<path>`; worktree `src\` is identical to `be10e1e`. `tests\`, `test-plan.md`, `test-result.md` not
read. Build and probe not run by the reviewer; everything below is by reasoning, nothing was executed.

**Verdict: APPROVE** for fix.3 (0 BLOCK, 1 SHOULD outside the fix.3 diff, 2 NIT, 1 question for the planner).

### Focus items
- **S2 / C20 – fixed.** `AuthenticationResultsParser.cs:148-160` continues the value over a separated token when
  `IsAddressDomainContinuation` (`:213-219`: Word starting with `@`, not a key) – only for property values
  (`isPropertyValue`, `:99-100`), never for method verdicts. Appending happens before C17/C19, so a single quoted
  local part followed by ` @attacker.test` / ` (c) @attacker.test` is no longer "exactly one quoted string" and the
  domain is the last unquoted `@` → `attacker.test`; `victim.test (c) @attacker.test` likewise. Variants checked by
  reasoning: `"x@victim.test" @ attacker.test` and `x@ (c) attacker.test` → domain part empty → `ToDomain` null
  (fail-safe); `"x@victim.test" "@attacker.test"` → mixed, all `@` quoted → null; `dkim=pass @x.test` → stray, verdict
  intact. C20 can only move the domain towards a later `@word` the provider wrote outside a comment; no new trust
  path from the envelope (an unquoted SMTP path cannot contain whitespace, and the real domain is always last).
- **N6 / C21 – fixed.** `:141-146` empty value when the token after `=` is missing or is separated and itself a key;
  `key = value` (case 3) unchanged, `smtp.mailfrom=dmarc=fail` (joined, B1) still one value. `IsKeyAt` (`:117-122`)
  now accepts `key=` at the statement end; `tokens[start]` is only read after the `start >= end` short circuit.
  See N10 for a side effect on methods.
- **Q5 / C22 – fixed.** Flag set only for the kept (first) DMARC result (`:331-334`, `case "dmarc" when dmarcResult
  is null`), carried by the internal init property (`AuthenticationResults.cs:26`), evaluated in `DescribePass`
  (`AuthStatusEvaluator.cs:56`, `:61`), which `Evaluate` uses – both stay consistent. `AuthenticationSignal` only reads
  DMARC fail, so no other consumer needs the flag. Placing it on the outer record (not on `AuthMethodResult`) is
  justified in impl-notes; a `with` copy can keep a stale `true`, but the flag only matters when `Dmarc.Domain` is
  null, so a replaced DMARC result with a domain is unaffected – acceptable.
- **Linear / never throws – confirmed.** `ReadValue` returns `start` (= key position + 2) for empty values, so
  `ParseStatement` always advances; the continuation check is O(1) per token (`StartsWith`, `IsKeyAt`); every token is
  still visited once in `ReadValue` and its text scanned once; the domain slice invariants of review.2 are unchanged.
- **C24 – acceptable.** `x@victim.test @attacker.test=foo` → `victim.test` needs a provider emitting a bare
  `@domain=...` word outside a comment, which no valid pvalue produces; it opens no path beyond what S3 below already
  allows. Re-evaluating with real headers in AP-07b is fine.

### SHOULD (outside the fix.3 diff, pre-existing since fix.1; found while checking C20 interactions)
**S3 – attacker data inside a provider comment can close the comment early; the rest becomes live tokens, so a
spoofed `smtp.mailfrom=` can be injected and later statements (incl. `dmarc=fail`) swallowed by a stray quote.**
`AuthenticationResultsTokenizer.cs:104-133` (`SkipComment` ignores quoted strings, so the first unescaped `)` closes
the comment), `AuthenticationResultsParser.cs:296` (first `smtp.mailfrom` wins).
Example (Gmail style, the provider copies the envelope sender verbatim into its SPF comment), envelope sender
`"x) smtp.mailfrom=victim.test y "@attacker.test`, SPF pass for `attacker.test`, From `…@victim.test`:
`mx.google.com; spf=pass (google.com: domain of "x) smtp.mailfrom=victim.test y "@attacker.test designates 192.0.2.1 as permitted sender) smtp.mailfrom="x) smtp.mailfrom=victim.test y "@attacker.test; dmarc=fail (p=QUARANTINE) header.from=victim.test`
→ the comment ends after `x`; `smtp.mailfrom=victim.test` becomes the first SPF property; the quote before
`@attacker.test designates` swallows the genuine `smtp.mailfrom=` key; the last quote runs to the input end and
swallows `; dmarc=fail …`. Result: SPF Pass `victim.test`, no DMARC → **Authenticated**; with a contact → TrustedContact,
which per PLAN row 1 even moves the message out of Junk. Precondition: the provider writes `)`/`"` from the local part
unescaped into a comment. RFC 5322 `ctext` forbids that (a compliant generator writes `\)`), but whether Gmail
escapes it is unknown (reviewer unsure). Attacker-triggered and "too much trust" direction, hence SHOULD; not a
fix.3 regression (C20 incidentally defeats the variant without the trailing `y`, not the one above).
Suggested fix (small, linear): in `SkipComment`, treat a quoted string inside a comment as opaque (skip `"…"` with
backslash escapes, ending at the input end). A valid quoted local part is balanced and escapes inner `"` as `\"`, so
the comment then ends at the provider's own `)`. Tester case: the header above → SPF domain `attacker.test`, DMARC
Fail, status Failed. Alternatively verify Gmail's escaping with a real header in AP-07b before any real header reaches
the parser.

### NIT
- **N9** `AuthenticationResultsParser.cs:19-22` – the public `Parse` doc still says a property value is "one contiguous
  run … up to whitespace, ';' or '('"; since C20/C21 a separated `@…` word is appended and `key=` before another key
  or at the statement end is an empty value. Add one sentence each. `:116` "directly followed by '=' (whitespace in
  between allowed" – "directly" contradicts the parenthesis; "followed by '='" suffices.
- **N10** `AuthenticationResultsParser.cs:117-119`, `:141-146`, `:99`, `:321-336` – C21 also applies to method keys:
  `spf=;` / `dmarc=;` at a statement end now create a result with verdict Unknown (before fix.3 they were skipped).
  Because only the first SPF/DMARC result is kept, `dmarc=; dmarc=fail …` now loses the provider's DMARC fail
  (Failed → Unknown or even Authenticated via an aligned pass) and `spf=; spf=fail` loses the SPF fail. Sloppy provider
  output only; optional: do not keep a method result whose verdict value is empty.

### Checklist (fix.3 diff)
Correctness: C20–C22 as specified, C24 as accepted. Architecture: Core-only, BCL-only, no new dependency, public API
unchanged (new member is internal). Data: n/a. Security: no new trust path from fix.3; S3 is pre-existing.
Consistency: `Evaluate`/`DescribePass` consistent; reason texts unchanged. Readability: comments updated (N5 done),
except N9; no dead code, no debug output. Uniqueness: `IsKeyAt` replaces the inline pair check and is used in all
three places; no duplicate helper. Plan: AP-07 rows marked in this diff (N8 done by the planner); row 1 mentions C11.

### Question for the planner
- **Q6 (S3)** Fix in a short fix.4 (tokenizer, a few lines + one tester case) or bind it as a must-do of AP-07b
  before real headers are classified? Recommendation: fix now – it is cheap, and the risk direction is "too much
  trust" with attacker-controlled input.

## review.4 (2026-10-31)

Scope: fix.4 `git diff 8b62121 fe361fc -- src` (`AuthenticationResultsTokenizer.cs`, `AuthenticationResultsParser.cs`)
plus direct interactions, against requirement C25–C27 and impl-notes fix.4. Worktree `src\` is identical to `fe361fc`
(the merge `9446c7b` touches no `src\`). `tests\`, `test-plan.md`, `test-result.md` not read. Executed: `dotnet build
MailWarden.sln -warnaserror` → 0 warnings, 0 errors. Everything else by reasoning; probe and tests not run.

**Verdict: APPROVE** (0 BLOCK, 0 SHOULD, 2 NIT; notes for AP-07b below).

### Focus items
- **S3 / C25 – fixed.** `AuthenticationResultsTokenizer.cs:119-123`: inside a comment, `"` hands over to
  `ReadQuotedString` (backslash escapes honoured, unterminated → input end), so `(`/`)` inside `"…"` neither nest nor
  close. The escape check (`:113-117`) runs first, so `\"` inside a comment is still no quote. Review.3 example by
  reasoning: the comment now ends at the provider's own `)`, the genuine `smtp.mailfrom="x) … y "@attacker.test` is one
  value (mixed, last unquoted `@` → `attacker.test`), `; dmarc=fail …` is live again → SPF `attacker.test`, DMARC Fail,
  status Failed. Case 3d of the requirement likewise → `attacker.test`, Fail. Bypass variants checked: `"x\" ) … "`
  (escaped quote inside the quoted local part) stays opaque; unbalanced `(((` inside the quoted part no longer raises
  the depth; `\"` outside a quoted string would need a backslash in an SMTP dot-string, which is not possible.
- **Linear / never throws (C25) – confirmed.** Every character is consumed exactly once, either by `SkipComment` or by
  `ReadQuotedString`, which returns a position > its start; `Tokenize` resumes at the returned index. `position += 2`
  past the end only exits the loop (`return text.Length`); `ReadQuotedString` is only entered with `position <
  text.Length` and guards `position + 1 < text.Length` before reading an escape. Hostile `("("(…` inputs stay O(n).
- **N10 / C26 – fixed.** `AuthenticationResultsParser.cs:113-114` sets `hasVerdict` from
  `!IsNullOrWhiteSpace(value.Text)`; `ResultCollector.Add` returns early (`:331-334`) before any of the three cases, so
  an empty SPF/DKIM/DMARC verdict neither occupies the first-wins slot nor sets `isDmarcHeaderFromWithoutDomain`.
  The method still collects its properties (`:106-109`), so `spf= smtp.mailfrom=victim.test; spf=fail …` cannot leak
  `victim.test` into the next method. `dmarc=; dmarc=fail header.from=a.test` → DMARC Fail. `dmarc= fail …` (separated
  non-key word) still reads `fail` as the verdict (C21 path unchanged). `dmarc=;` alone without AuthServId → `Empty`.
  This restores the pre-fix.3 behaviour for empty method verdicts; no consumer depended on an Unknown entry for them.
- **N9 / C27 – done.** `Parse` doc (`:22-25`) now covers C20 (`@…` after CFWS), C21 (empty values) and C26;
  `ParseStatement` comment (`:88-89`) mentions C26; `:121` reads "A Word followed by '='". Docs match behaviour.

### Checklist (fix.4 diff)
Correctness: C25–C27 as specified. Architecture: Core-only, BCL-only, no public signature changed (`PendingMethod`
private, tokenizer internal). Data: n/a. Security: closes S3; no new trust path (see AP-07b note 1 for the trade-off).
Consistency: the only comment/quote scanner is this tokenizer; top-level and in-comment quotes now use the same
`ReadQuotedString`. Readability: comments updated, no dead code, no debug output. Uniqueness: no new helper, the
existing `ReadQuotedString` is reused. Plan: no PLAN row changes needed (hardening under rows 1 and 5).

### NIT
- **N11** `AuthenticationResultsTokenizer.cs:29` – the class summary line is about 150 characters after the edit; the
  rest of the file wraps at about 120. Re-wrap.
- **N12** `AuthenticationResultsTokenizer.cs:121` – `ReadQuotedString(…, out _)` builds and discards the content string
  for every quoted string in a comment. Linear and measured fast (impl-notes: ≤ 169 ms per 1 MB), so only a possible
  allocation-free skip variant later; not worth a round.

### Für AP-07b (outside the fix.4 diff or by design; not BLOCK/SHOULD)
1. **Unterminated quote swallows the rest of the header.** Since C25 also inside comments (before only at top level,
   e.g. `smtp.mailfrom=x"@attacker.test; dmarc=fail …`). If a provider writes an attacker-supplied lone `"` unescaped,
   a later `dmarc=fail` is lost (Failed → weaker status). Not new as a class (the top-level path existed before
   fix.4), and the direction is "less evidence", not "trusted". Check with real Gmail/Outlook headers whether lone or
   unescaped quotes can appear in comments or `smtp.mailfrom`; option: end an unterminated quoted string at the next
   `;` instead of the input end.
2. **C25 is a deliberate deviation from RFC 5322 grammar.** RFC 5322 `ctext` includes `"`, comments do not know quoted
   strings; the requirement text "(RFC 5322: …)" in C25 attributes the rule to the RFC. The code comment
   (`AuthenticationResultsTokenizer.cs:104-106`) correctly justifies it with provider copying, not with the RFC.
   `docs\classifier.md` (documenter) should describe it as hardening, not as RFC behaviour.
3. Provider escaping of `)`/`"` in comments (review.3 S3 precondition) is still unverified; re-check with real
   headers together with C24 (`@domain=…` continuation).
