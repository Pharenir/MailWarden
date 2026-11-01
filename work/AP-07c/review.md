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

# review.2 (2026-11-01, HEAD 52b336a)

**Verdict: APPROVE**, with two SHOULD. B1, S1, S2 and the NITs of review.1 are fixed.

| Id | Level | Finding | Decision |
|---|---|---|---|
| S1 | SHOULD | Retention fully parses every quarantine mail in every pass (`ClassificationPass.cs:443-452`) | fix.3 H3 |
| S2 | SHOULD | Probe `execute`: limit per command, deletes allowed | fix.3 H6 |
| N1 | NIT | Broad `catch` loses `TopReceived` (`ClassificationInputReader.cs:65-72`) | fix.3 H2 |
| N2 | NIT | `SUBSCRIBE` before the role check (`MailKitFolderProvisioner.cs:137-143`) | fix.3 H4 |
| N3, N5 | NIT | Doc points: failed subscribe, first sync never completes | fix.3 H8 |
| N4 | NIT | Stamp count says little for Microsoft | fix.3 H7 |

Questions: unstable `Received` → `Act` stays off until case 33 (fix.3). Case 33 flow: completed sync on the copy with
`sync-probe` → `pass` (marker) → test mails + sync → `pass` → `execute` → sync. Failed commands blocking retention → H8.
