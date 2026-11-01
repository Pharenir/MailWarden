
# AP-06 - review.1 (2026-11-01)

**Verdict: APPROVE.** Three SHOULD and several NITs. Decisions per finding: `requirement.md`, section "Decisions fix.2".

# review.2 (2026-11-01)

**Verdict: BLOCK.** R5 reopens what R4 closes: `IdnMapping` maps look-alikes to ASCII, so a full-width letter hits an allow entry (confirmed) → T1. SHOULD: a block of an IDN domain is bypassed through one unusual label → T2. NITs: trim on remove → T4; rest `TODO.md`.

# review.3 (2026-11-01)

**Verdict: APPROVE.** T1-T5 as decided. One SHOULD, fixed by the planner: the `xn--` exception also covered labels with non-ASCII characters. T3: `header.from` is the From domain under relaxed DMARC too, so real mail is hardly affected; count it on real data before `Act`.
