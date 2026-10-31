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