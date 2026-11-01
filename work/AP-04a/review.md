# AP-04a – review.1 (2026-11-01)

**Verdict: BLOCK.** Planner decisions per finding: `requirement.md`, section "Decisions fix.2". Reviewer's status: diff and
generated code read, build green, no GUI start, no tests read by rule.

| Id | Level | Finding | Decision |
|---|---|---|---|
| B1 | BLOCK | Info bar never closes once `Banner` had text: `x:Bind` function bindings skip null (`MainWindow.g.cs:1644-1651`) | K1 |
| S1 | SHOULD | Replaced selected row clears the reading pane and loses the image approval (`MainWindow.xaml.cs:229-235`) | K3 |
| S2 | SHOULD | Closing during a read closes the connection without serialisation (`RuntimeConnections.cs:47-59`) | K4 |
| S3 | SHOULD | Closing during start: `StartAsync` can run on disposed connections (`App.xaml.cs:100-101`) | K4 |
| S4 | SHOULD | Theme list with `DisplayMemberPath` on a private record | K8 |
| S5 | SHOULD | List can push the reading pane out of the window | K9 |
| S6 | SHOULD | Duplicates: atomic write, subject rule, status dictionary, log level | K16 |
| S7 | SHOULD | Invisible bidi literals in `SafeDisplayText.cs:66-68` | K14 |
| NIT | NIT | Late `ShowAsync` after close, stretch of list items, `heldLock` in the summary, others | K4, builder's choice |

Questions: P1 queue of the copy → checked, empty. P2 reload on errors → K15. P3 `form` and `IsUserInitiated` → K10, K11.
P4 list size per layout → K9.

Unsure, only a run can tell (cases 42–45): tree selection before containers exist; `AsRandomAccessStream` as response stream.
