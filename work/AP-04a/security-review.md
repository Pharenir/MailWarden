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
