
# AP-06 - secure.1 (2026-11-01)

**Result: no Critical, no High while `Act` is off; 4 Medium, 4 Low.** S1 (allow list trusts unauthenticated mail) and S2 (exact contact forged through an unlisted public suffix) would be High with `Act`: fixed in fix.2. S3 (harvest does not check that a sent mail is by Dennis), S4 (tolerant lookup applies to allowing), S5, S6, S8: fix.2. S7 and the open question about provider stamps: `TODO.md`.

# secure.2 (2026-11-01)

**Result: no Critical, no High.** S1 closed. S2 closed for DKIM and SPF. S3 mostly closed. S4 partly: N1 → T1. N2 DMARC path still accepts siblings → T3. N3 automatic mails without RFC 3834 marks → T5. N4 remaining block variants: no pressure. Parent outside the list and child rule: enough for now, `TODO.md` before `Act`.
