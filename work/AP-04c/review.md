# AP-04c review

## review.1 (reviewer, 2026-11-02): CHANGES
BLOCK B1: `AttachmentFiles.Clear` followed junctions and directory symlinks and deleted files outside the open folder (probe). SHOULD: S1 gaps in the SaveOnly list; S2 a failed save after a change of mail was silent; S3 new duplicate of the stream options of `FileEmlStore`; S4 `Process.Start` unmeasured for level Ask. NIT: N1 to N11. Questions: Q1 measuring and more manual cases; Q2 banner; Q3 feedback during long copies; Q4 dead attachment names; Q5 documents.
Checked without a finding: `SortParts` as the one rule, parser limits, `expected` against other content, lifetime of the actions, the order of questions, `DataPaths`.
Decisions: `requirement.md`, "Decisions fix.2".

## review.2 (reviewer, 2026-11-02): APPROVE
B1, S1 to S3, Q2 to Q4 fixed. S4 and Q1 stay open until the measurement. SHOULD S1: part 11 of the manual tests needed test mails it did not provide; solved with an artificial data folder. NIT N1 to N11: `requirement.md`, "Decisions fix.3".

## review.3 (reviewer, 2026-11-02): APPROVE
Stand `ad4cf5c`. `NativeLinks` is correct: `Pack = 4` matches `WIN32_FIND_DATAW`, measured. No case where a deliberate action on the same mail does nothing. SHOULD S1 to S3 and NIT N1 to N3: `requirement.md`, "Decisions fix.4".
