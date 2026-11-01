
# AP-06 - secure.1 (2026-11-01)

**Result: no Critical, no High while `Act` is off; 4 Medium, 4 Low.** S1 (allow list trusts unauthenticated mail) and S2 (exact contact forged through an unlisted public suffix) would be High with `Act`: fixed in fix.2. S3 (harvest does not check that a sent mail is by Dennis), S4 (tolerant lookup applies to allowing), S5, S6, S8: fix.2. S7 and the open question about provider stamps: `TODO.md`.
