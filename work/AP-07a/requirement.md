---
id: AP-07a
slug: classifier-core
title: Spam classifier core (pure logic in MailWarden.Core)
test-scope: S2
test-filter: "Category=feature:classifier|Category=smoke"
base: feat/ap-01-skeleton-storage (builder) / test/ap-01-skeleton-storage (tester)
status: ready (2026-10-31)
---

# AP-07a – Spam classifier core

## Goal
The four-level spam classifier as pure, deterministic logic in `MailWarden.Core`: parse the receiving provider's
`Authentication-Results` header, evaluate a fixed set of spam signals, each returning points **with a reason**, and
map the sum plus contact/allow/block lookups to one of four tiers. No IO, no packages, no MimeKit: the input is an
already-extracted record. Extraction from real messages, storage of decisions, folder actions, quarantine ageing and
UI are AP-07b/AP-08.

Why now: it is the main reason for the project (`PLAN.md`), it has no dependency on storage or IMAP, and it can be
tuned early.

## Context
- `PLAN.md`, section "Vier Stufen" and "Sollverhalten AP-07" (cases 1–8; this package covers the tier decision of
  1–6 and 8; case 1's move out of Junk and case 7 quarantine ageing are AP-07b).
- `MailWarden.Core` exists after AP-01 (`net9.0`, no package references, `TreatWarningsAsErrors`, XML docs required).
  Do not change anything from AP-01 except adding files.
- Provider header formats (verify against real headers in AP-07b; the parser must handle both):
  - Gmail: `mx.google.com; dkim=pass header.i=@example.test header.s=s1 header.b=AbCd; spf=pass (google.com: domain
    of bounce@example.test designates 192.0.2.1 as permitted sender) smtp.mailfrom=bounce@example.test; dmarc=pass
    (p=REJECT sp=REJECT dis=NONE) header.from=example.test`
  - Outlook.com (no authserv-id, i.e. not RFC 8601 compliant): `spf=pass (sender IP is 192.0.2.1)
    smtp.mailfrom=example.test; dkim=pass (signature was verified) header.d=example.test;dmarc=pass action=none
    header.from=example.test;compauth=pass reason=100`
- Only the **top-most** `Authentication-Results` header is trustworthy (prepended by the receiving provider); lower
  ones can be forged by the sender. The caller passes headers top-most first; the classifier uses index 0 only.

## Non-goals
- No MIME parsing, HTML-to-text or link extraction (AP-07b, Infrastructure, with MimeKit).
- No persistence, no folder moves, no quarantine retention, no learning filter (AP-07b/AP-12), no RDAP lookups.
- No localisation: reason texts are English for now (UI language is decided in AP-04).
- No public suffix list: organisational domain is approximated (see contract).

## Contract (binding) – namespace `MailWarden.Core.Classification`

```csharp
public enum SpamTier { TrustedContact, LikelyLegit, LikelySpam, CertainSpam }

public enum AuthVerdict { None, Pass, Fail, SoftFail, Neutral, TempError, PermError, Policy, Unknown }

/// Result of one method in an Authentication-Results header. Domain: SPF = domain of smtp.mailfrom (address part
/// after '@' if an address is given), DKIM = header.d (or the domain of header.i when header.d is absent),
/// DMARC = header.from. Lower-case, null when absent.
public sealed record AuthMethodResult(AuthVerdict Verdict, string? Domain);

/// Parsed header. Dkim may contain several signatures; Spf/Dmarc null when the method is not present.
public sealed record AuthenticationResults(
    string? AuthServId, AuthMethodResult? Spf, IReadOnlyList<AuthMethodResult> Dkim, AuthMethodResult? Dmarc)
{
    public static AuthenticationResults Empty { get; }
}

public static class AuthenticationResultsParser
{
    /// Tolerant parser for untrusted input: comments "(...)" and quoted strings are skipped, method/verdict names are
    /// case-insensitive, folded whitespace and missing spaces after ';' are accepted, unknown methods (compauth, arc,
    /// bimi, ...) are ignored, unknown verdict words map to Unknown. Never throws for malformed content – returns what
    /// could be parsed (worst case Empty). A leading token without '=' before the first ';' is the AuthServId.
    /// headerValue null → ArgumentNullException.
    public static AuthenticationResults Parse(string headerValue);
}

public enum AuthStatus { Unknown, Authenticated, Failed }

public static class DomainNames
{
    /// Domain part of an address: text after the last '@', trimmed, lower-case, trailing '.' removed.
    /// No '@' or empty domain → ArgumentException. null → ArgumentNullException.
    public static string GetDomain(string emailAddress);

    /// Approximated organisational domain: last two labels, or last three when the last two form a known
    /// multi-label public suffix (built-in list, at least: co.uk, org.uk, ac.uk, gov.uk, com.au, net.au, org.au,
    /// co.jp, co.nz, com.br, com.tr, co.za, com.cn). Single-label input is returned unchanged. Lower-case.
    public static string GetOrganizationalDomain(string domain);

    /// Registrable label = organisational domain without its public suffix ("mail.qzxkvb73.top" → "qzxkvb73",
    /// "shop.example.co.uk" → "example").
    public static string GetRegistrableLabel(string domain);

    /// Public suffix of the organisational domain ("top", "de", "co.uk").
    public static string GetPublicSuffix(string domain);

    /// True when both domains have the same organisational domain (relaxed DMARC-style alignment).
    public static bool AreAligned(string domainA, string domainB);
}

/// Everything the classifier needs, extracted by the caller. AuthenticationResultsHeaders: raw header values,
/// top-most first (may be empty). BodyText: plain text (may be empty). LinkHosts: host names of http/https links
/// in the body (may be empty, may contain duplicates).
public sealed record ClassificationInput(
    string FromAddress,
    string FromName,
    string? ReplyToAddress,
    IReadOnlyList<string> AuthenticationResultsHeaders,
    string BodyText,
    IReadOnlyList<string> LinkHosts);

public enum ListMembership { None, Allowed, Blocked }

/// Lookups backed by the address book and allow/block lists (implemented in AP-06/AP-07b). Synchronous on purpose:
/// implementations keep an in-memory snapshot. Matching rules (address exact, case-insensitive; domain lists match the
/// sender's organisational domain) are the implementation's job; the classifier just asks.
public interface ISenderDirectory
{
    bool IsContact(string emailAddress);
    ListMembership GetListMembership(string emailAddress);
}

/// Configuration. Validate() → ArgumentException when LikelySpamThreshold >= CertainSpamThreshold, either threshold
/// <= 0, or a list contains null/empty entries. Lists are compared case-insensitively.
public sealed record ClassifierSettings(
    int LikelySpamThreshold,
    int CertainSpamThreshold,
    IReadOnlySet<string> SuspiciousTlds,
    IReadOnlySet<string> FreemailDomains)
{
    /// 30 / 70; SuspiciousTlds: top, xyz, click, loan, work, gq, tk, ml, cf, ga, icu, buzz, rest, cfd, sbs;
    /// FreemailDomains: gmail.com, googlemail.com, hotmail.com, hotmail.de, outlook.com, outlook.de, live.com, live.de,
    /// msn.com, yahoo.com, yahoo.de, gmx.de, gmx.net, web.de, t-online.de, icloud.com, me.com, aol.com, proton.me,
    /// protonmail.com, freenet.de.
    public static ClassifierSettings Default { get; }
    public void Validate();
}

/// Precomputed per message, shared by all signals.
public sealed record ClassificationContext(
    string FromDomain, string FromOrganizationalDomain, AuthenticationResults Authentication, AuthStatus AuthStatus,
    bool FromIsFreemail, ClassifierSettings Settings);

/// One finding. Points may be negative (evidence for legitimacy). Reason: one English sentence naming the concrete
/// values (e.g. "Sender domain label 'qzxkvb73' looks random (score 0.91)").
public sealed record SignalResult(string SignalId, int Points, string Reason);

public interface ISpamSignal
{
    string Id { get; }
    /// null = signal does not apply / no finding. Must not throw for any well-formed input record.
    SignalResult? Evaluate(ClassificationInput input, ClassificationContext context);
}

/// Final decision. Signals: all non-null signal results in evaluation order. Override: reason text when an
/// allow/block/contact rule decided the tier instead of the score, else null.
public sealed record ClassificationResult(SpamTier Tier, int Score, AuthStatus AuthStatus,
    IReadOnlyList<SignalResult> Signals, string? Override);

public static class RandomDomainScorer
{
    /// 0.0 (natural word) … 1.0 (random). Labels shorter than 5 characters (after removing '-') → 0.0.
    /// Deterministic, culture-invariant. Must satisfy the example lists in the behaviour table.
    public static double Score(string registrableLabel);
}

public sealed class SpamClassifier
{
    /// settings.Validate() is called here (fail fast). signals: evaluated in the given order; duplicate Ids →
    /// ArgumentException.
    public SpamClassifier(ClassifierSettings settings, ISenderDirectory senderDirectory, IEnumerable<ISpamSignal> signals);

    /// The built-in signals below in this order: authentication, random-domain, suspicious-tld, domain-not-in-body,
    /// display-name-mismatch, reply-to-mismatch, link-domain-mismatch.
    public static IReadOnlyList<ISpamSignal> CreateDefaultSignals();

    /// input null → ArgumentNullException; FromAddress without '@' → ArgumentException.
    public ClassificationResult Classify(ClassificationInput input);
}
```

### AuthStatus (computed from the top-most header only)
- **Authenticated**: DMARC = Pass, **or** a DKIM result with Pass whose domain is aligned with the From domain, **or**
  SPF = Pass with a domain aligned with the From domain.
- **Failed**: not Authenticated **and** (DMARC = Fail, **or** SPF = Fail, **or** at least one DKIM result exists and
  all DKIM results are Fail).
- **Unknown**: everything else (no header, only None/Neutral/SoftFail/TempError/…).

### Built-in signals (ids and points are part of the contract)
| Id | Applies when | Points |
|---|---|---|
| `authentication` | AuthStatus Authenticated | −20 |
| | otherwise sum of: DMARC Fail +40, SPF Fail +20, SPF SoftFail +10, all DKIM Fail (≥1 present) +20; capped at +50; no finding when the sum is 0 | +10 … +50 |
| `random-domain` | `RandomDomainScorer.Score(registrable label of From domain)` ≥ 0.8 → +35; ≥ 0.5 → +15; From is freemail → never | +15 / +35 |
| `suspicious-tld` | public suffix of From domain (last label for multi-label suffixes) in `SuspiciousTlds` | +10 |
| `domain-not-in-body` | From not freemail, BodyText not empty, registrable label of From domain does not occur in BodyText (case-insensitive substring) **and** no LinkHost is aligned with the From domain | +15 |
| `display-name-mismatch` | FromName contains an e-mail address or a domain-like token (`label.tld` with a letter-only TLD of 2–24 chars) whose organisational domain differs from the From organisational domain | +20 |
| `reply-to-mismatch` | ReplyTo present and not aligned with From: ReplyTo freemail while From is not → +15, otherwise +10 | +10 / +15 |
| `link-domain-mismatch` | From not freemail, ≥ 1 LinkHost, and ≥ 80 % of the distinct LinkHosts are not aligned with the From domain | +10 |

### Decision order in `Classify`
1. Build the context (From domain, organisational domain, parse header 0 if present, AuthStatus, freemail flag).
2. Evaluate all signals; Score = sum of their points.
3. `GetListMembership(FromAddress)` = Blocked → **CertainSpam**, Override "Sender is on the block list".
4. Allowed and AuthStatus ≠ Failed → **TrustedContact**, Override "Sender is on the allow list".
5. `IsContact(FromAddress)` and AuthStatus = Authenticated → **TrustedContact**, Override "Sender is a contact and
   authenticated (…)".
6. `IsContact` and AuthStatus = Unknown → at most **LikelyLegit** (score tier, but never worse than LikelyLegit),
   Override "Sender is a contact but authentication could not be verified".
7. Otherwise by score: ≥ CertainSpamThreshold → CertainSpam; ≥ LikelySpamThreshold → LikelySpam; else LikelyLegit.
   (A contact with AuthStatus Failed or an allow-listed sender with Failed ends up here; the result's Signals contain
   the authentication finding, and Override is null.)
Signals are always evaluated and returned, also when an override decides – the "Why?" view needs them.

## Expected behaviour (basis for the tests)
Feature tag `feature:classifier`. All addresses artificial except the plausible-domain list (public company domains,
no personal data). `S(x)` = `RandomDomainScorer.Score(x)`.

| # | Case | Expected |
|---|---|---|
| 1 | Parse Gmail example from Context | AuthServId `mx.google.com`; Spf Pass `example.test`; one Dkim Pass `example.test` (from header.i); Dmarc Pass `example.test` |
| 2 | Parse Outlook example from Context | AuthServId null; Spf Pass `example.test`; Dkim Pass `example.test`; Dmarc Pass `example.test`; compauth ignored |
| 3 | Parse with mixed case (`SPF=FAIL`), extra spaces, comments containing `;` and `=`, quoted strings, a folded line break | verdicts correct, comments do not leak into domains |
| 4 | Parse two DKIM results (one pass, one fail) | Dkim has 2 entries in header order |
| 5 | Parse garbage (`"%%%;;=="`), empty string, `"none"` | no exception; Empty or partially parsed; `null` → ArgumentNullException |
| 6 | Unknown verdict word (`dkim=maybe`) | Verdict Unknown |
| 7 | `GetDomain("A.User@Mail.Example.TEST.")` | `mail.example.test`; no `@` → ArgumentException |
| 8 | `GetOrganizationalDomain`: `mail.qzxkvb73.top` → `qzxkvb73.top`; `shop.example.co.uk` → `example.co.uk`; `example.test` → `example.test`; `localhost` → `localhost` | |
| 9 | `AreAligned("news.example.test", "example.test")` true; `("example.test", "example-mail.test")` false | |
| 10 | `S` for random labels `qzxkvb73`, `xkqzvbnm`, `bvtrkzpq`, `a8f3k2l9m`, `zqwxjkvh`, `hjkdfgtrw`, `x7k2p9q4`, `mnbvcxzl`, `wqzrtpx8`, `kjhgfdsqw` | each ≥ 0.8 |
| 11 | `S` for plausible labels `hotmail`, `googlemail`, `amazon`, `paypal`, `sparkasse`, `telekom`, `check24`, `zalando`, `switchup`, `bundeswehr`, `lieferando`, `netflix`, `spotify`, `mediamarkt`, `kfzteile24`, `schwarzkopf`, `ebay-kleinanzeigen`, `versicherung`, `rechtsanwalt`, `1und1` | each < 0.5 |
| 12 | `S` for labels shorter than 5 (`dhl`, `gmx`, `dkb`, `xkcd`) | 0.0 |
| 13 | Unknown sender, plausible non-freemail domain, Gmail-style header all pass, body mentions the domain | LikelyLegit, Score −20, AuthStatus Authenticated, Signals contain `authentication` −20 |
| 14 | Sender `offer@qzxkvb73.top`, header `dmarc=fail spf=fail dkim=fail`, body without the label, links to other hosts | CertainSpam; Signals contain `authentication` +50, `random-domain` +35, `suspicious-tld` +10, `domain-not-in-body` +15, `link-domain-mismatch` +10; Score 120 |
| 15 | Contact, Authenticated | TrustedContact, Override set |
| 16 | Contact, DKIM fail + SPF fail (Failed) | **not** TrustedContact; tier by score; authentication finding present with its reason |
| 17 | Contact, no Authentication-Results header | LikelyLegit (even if score ≥ LikelySpam), Override "…could not be verified" |
| 18 | No header at all, non-contact, plausible domain, body mentions domain | LikelyLegit, AuthStatus Unknown, no `authentication` finding |
| 19 | Block list hit even with Authenticated and contact | CertainSpam, Override "block list" |
| 20 | Allow list, AuthStatus Unknown | TrustedContact; Allow list, AuthStatus Failed → tier by score, no override |
| 21 | Only the top-most header counts: header 0 = all fail, header 1 = all pass | AuthStatus Failed |
| 22 | DKIM pass for a **non-aligned** domain only (`header.d=mailer.test`, From `example.test`), no SPF/DMARC | AuthStatus Unknown (not Authenticated) |
| 23 | Freemail sender (`someone@gmail.com`) with empty body and random-looking local part | no `random-domain`, no `domain-not-in-body`, no `link-domain-mismatch` |
| 24 | FromName `"PayPal Service <service@paypal.test>"` with From `x@qzxkvb73.top` | `display-name-mismatch` +20; FromName `"Example Shop"` → no finding |
| 25 | ReplyTo `collect@gmail.com`, From `billing@example.test` | `reply-to-mismatch` +15; ReplyTo `support@example.test` → none; ReplyTo `a@other.test` → +10 |
| 26 | Thresholds: score exactly 30 → LikelySpam, 29 → LikelyLegit, 70 → CertainSpam (use a stub `ISpamSignal` with fixed points) | |
| 27 | `ClassifierSettings` with Likely 70 / Certain 70, Likely 0, or empty TLD entry | `Validate()` and the `SpamClassifier` ctor throw ArgumentException |
| 28 | Duplicate signal Id in ctor | ArgumentException |
| 29 | `Classify(null)`, FromAddress `"no-at-sign"` | ArgumentNullException / ArgumentException |
| 30 | Every `SignalResult.Reason` of the built-in signals in cases 13–25 is non-empty and contains the concrete value it judged (domain, label or score) | |
| 31 | **smoke**: `new SpamClassifier(ClassifierSettings.Default, <directory with no contacts>, CreateDefaultSignals())` classifies cases 13 and 14 as LikelyLegit / CertainSpam | |

Positive controls: case 22 must fail if alignment is ignored; case 21 must fail if any header other than index 0 is
used; case 16 must fail if contacts bypass authentication.

## Done criteria
- Builder: only new files under `src\MailWarden.Core\Classification\`; build `-warnaserror` green; probe program
  under `_scratch\` (git-ignored) runs cases 10–14 and prints the scores; `work\AP-07a\impl-notes.md` with the
  usual sections (STEP-RESULT, files, deviations, verified vs. untested, open questions).
- Tester: new project `tests\MailWarden.Core.Tests\` (xUnit 2.9.x like AP-01, `net9.0`, reference Core only,
  `[Fact(Timeout = …)]` only on async tests – these tests are synchronous, so plain `[Fact]`), `work\AP-07a\test-plan.md`,
  `tests\test-map.json` entry `src/MailWarden.Core/Classification/**` → `["@feature:classifier"]`.
- Planner: merge, add the test project to the solution, S2 via Tester, review, Dennis checks the reason texts.
