# AP-07c – secure.1 (2026-11-01)

**Result: 0 Critical, 2 High (one conditional), 1 Medium, 4 Low.** Gate not met. Planner decisions per finding:
`requirement.md`, section "Decisions fix.2". Threat model: the sender controls all headers, body and links and can send
several mails. Entry points: EML (reader, key), LIST/STATUS from the server, local policy file. No public entry point.

| Id | Level | Finding | Decision |
|---|---|---|---|
| H1 | High | A reused `Message-ID` makes every further mail of a campaign `SkippedKnown`: it stays unfiltered in the Inbox (`ClassificationPass.cs:207-213`, `MessageKey.cs:29-32`) | G2 |
| H2 | High (conditional on MimeKit decoding) | `Header.Value` decodes encoded words, so a crafted `smtp.mailfrom` can create `dmarc=pass` tokens and reach `TrustedContact` (`ClassificationInputReader.cs:90`) | G4, case 40 |
| M1 | Medium | Two From addresses or an empty group give `ReadFailed`: unfiltered in the Inbox for good | G7 |
| N1 | Low | Retention recognises "moved by the app" only by the forgeable key | G2, case 39 |
| N2 | Low | Whole EML in memory, unbounded `LinkHosts` | `LinkHosts` G11; rest TODO AP-08 |
| N3 | Low | Retention enqueues again every pass | G6 |
| N4 | Low | Decision log has no retention; reasons contain sender domain and name tokens | TODO AP-08 |

Notes for later:
- AP-08 must show reason texts as text, never as HTML.
- "Retention 0 = never delete" holds for this app only; a provider can clean up its Junk folder itself.
- Unverified, old code: the classifier trusts the top `Authentication-Results` without checking the stamp → counted in
  case 32 (G14).

Reviewer's status: code reading only; nothing executed; `tests\` not read by rule.

# secure.2 (2026-11-01, HEAD 52b336a)

**Result: 0 Critical, 0 High, 3 Medium, 3 Low.** Condition: `Act` only after cases 32/33 show that the top `Received`
survives a MOVE; otherwise a mail moved back is decided again, and with retention lost (would be High).
Closed: H2, N1, N3. H1 closed at its core, rest M2 and N1 below.

| Id | Level | Finding | Decision |
|---|---|---|---|
| M1-neu | Medium | `RouteWithoutSender` lets a sender pick the Screener instead of Junk or Quarantine | fix.3 H1 |
| M2 | Medium | Top `Received` of Outlook.com possibly not unique per delivery (second resolution): a salvo with a fixed Message-ID could stay unfiltered | measure (H7), then body hash |
| M3 | Medium | Probe `execute` can do more than intended on the real server | fix.3 H6 |
| N1 | Low | Mail without `Received` keeps a key the sender controls | measure (H7) |
| N2 | Low | Retention parses every quarantine mail every pass | fix.3 H3 |
| N3 | Low | Rest bypass through exceptions in reader or classifier | fix.3 H2 |
| – | unsure | Latin-1 bytes count as separators in the tokenizer | fix.3 H5 |

Reviewer's status: code reading only, nothing executed.

# secure.3 (2026-11-01, HEAD c21d041)

**Result: 0 Critical, 0 High, 0 Medium, 2 Low.** M1-neu, M3, N3, retention parsing and tokenizer closed. N1: a programming
error moves all new mail to Junk unnoticed → fix.4 J1, J2, J4. N2: subject marker only as strong as its choice → random
marker per run, count taken from the mails actually sent (case 33 instructions). F4 not relevant in practice.
