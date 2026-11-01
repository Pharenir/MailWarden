# AP-04a – secure.1 (2026-11-01, `1dd62cd`)

**Result: 0 Critical, 1 High, 3 Medium, 4 Low.** Gate not met. Planner decisions per finding: `requirement.md`, section
"Decisions fix.2". Probes with artificial data in the session scratchpad; no GUI, no server. Threat model: the sender
controls HTML, CSS, headers, file names, MIME structure and sizes.

| Id | Level | Finding | Decision |
|---|---|---|---|
| H1 | High | About 1,000 nested MIME parts overflow the stack (exit `0xC00000FD`, tested), again after every start (`SyncEngine.cs:542`, `MailContentReader.cs:38`) | K2 |
| M1 | Medium | Many references to one inline image blow past the 25 MB limit (419 million characters, 3.8 GB, tested) | K5 |
| M2 | Medium | `HtmlNestingLimiter` limits depth but not run time (100,000 attributes over 40 s, tested) | K6 |
| M3 | Medium | Header lines without a length limit can hide the real sender and the content | K7 |
| N1 | Low | A self-starting browser launch depends on the sanitizer alone | K11 |
| N2 | Low | `mailto:` keeps all parameters, including `attach` | K12 |
| N3 | Low | After "Bilder laden": LAN and localhost targets, cookies, referer | K13 |
| N4 | Low | `Clean` leaves line separators and invisible characters | K14 |

Checked without finding: no script, no form, no network connection without approval across 57 payload groups; image approval
limited to one message; logs, error window and settings hold no mail data; attachment names never used as paths.

Manual test notes: check that the queue of the copy is empty; end the app before using the AuthProbe.

# secure.2 (2026-11-01, `5b18527`)

**Result: 0 Critical, 0 High, 3 Medium, 3 Low.** H1, old M1, N1, N4 closed, old M2 and M3 not fully. The limiter cannot keep a
dangerous element (34 wrappers tested), it can only cost time and memory.

| Id | Level | Finding | Decision |
|---|---|---|---|
| M1 | Medium | Short prefix bypasses the limiter (23-30 s; over 60 s with attributes) | fix.3 L1, L3 |
| M2 | Medium | Reconstructed formatting elements: 154 KB mail, over 5 GB, then `OutOfMemoryException` | fix.3 L2 |
| M3 | Medium | Sender can stay hidden in the header line (`MailDisplayText.cs:56-60`) | fix.3 L6 |
| N1 | Low | Content below depth 32 invisible without a notice; also unseen by the classifier | fix.3 L5; signal → TODO AP-08 |
| N2 | Low | K13 holds for IP literals, not for names and some ranges | fix.3 L8; the rest documented |
| N3 | Low | `SanitizeMailto` leaves two spellings | fix.3 L9 |

Untested: WebView2 behaviour (real click, redirects, `DeleteAllCookies`, InPrivate in the WinUI control).

# secure.3 (2026-11-01, `691cc5e`)

**Result: 0 Critical, 0 High, 2 Medium, 2 Low.** M3, N1, N2, N3 closed. New: M1 `<!--!>` keeps scan and parser apart (7.4 s for
259 KB) → fix.4 M2. M2 formatting counter counts down on end tags the parser ignores (172 KB mail, over 5 GB) → fix.4 M1.
N1 hidden content without a notice → M5. N2 `multipart` without a usable boundary → M6.
