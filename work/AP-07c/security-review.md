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
