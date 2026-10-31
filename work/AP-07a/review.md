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