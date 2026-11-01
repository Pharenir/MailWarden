# AP-07c – review.1 (2026-11-01)

**Verdict: BLOCK.** Planner decisions per finding: `requirement.md`, section "Decisions fix.2".

| Id | Level | Finding | Decision |
|---|---|---|---|
| B1 | BLOCK | Old mail is treated as new after an interrupted first sync or a UIDVALIDITY reset: `UidValidity != 0` does not mean "folder completed" (`ClassificationPass.cs:123`, `SyncEngine.cs:337-342`, `424-429`) | G1 |
| S1 | SHOULD | Retention re-enqueues the same UIDs every pass; with D8's trigger this loops and `mail_commands` grows (`ClassificationPass.cs:387-414`) | G6 |
| S2 | SHOULD | Silent `catch` on retention rejection (`ClassificationPass.cs:416-419`) | G8 |
| NIT | NIT | Default arm in `ClassificationRouting` is the sharpest action | G9 |
| NIT | NIT | Comment "can only repeat a move" is incomplete (`ClassificationPass.cs:24, 231`) | G10 |
| NIT | NIT | Dependency on `SqliteMailCommandQueue.MaxUidsPerCommand`; `FindFolder` next to `FolderLookup`; `href` with tabs or `//host/` skipped | stay for now |

Questions: new folders are not subscribed → G12. Message-ID and `ReadFailed` bypass → G2, G7. Deviations of implement.1
all accepted → G13. Case 33 needs a command runner → G14.

Reviewer's status: derived from the code, not executed; `tests\` not read by rule.
