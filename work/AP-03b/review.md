# AP-03b – review.1 (reviewer, opus)

**Verdict: APPROVE** (diff `1e474b8..2b32c57`, `src` + `work\AP-03b\impl-notes.md`; `src` unchanged up to `5cf1dd9`;
`tests\` not read). Build `dotnet build MailWarden.sln -warnaserror` green (0 warnings, 0 errors).
Not verified at runtime: MailKit `GetStreamsAsync` against real servers (order of bodies, BODY.PEEK) – case 18.

## Contract
B1–B7 implemented as specified; names/signatures exact; Core gets only the interface members. fix.2/fix.3 rules kept
(per-message counting and Error text unchanged, failed UIDs stay missing above the floor, state only after a complete
folder). Read-only kept: `OpenAsync(FolderAccess.ReadOnly)` (EXAMINE), bodies only via `GetStream(s)Async`
(BODY.PEEK[]), no write member added to `IMailTransport`.

Checked in detail:
1. MailKit: two commands per batch (metadata `FetchAsync` + `GetStreamsAsync`). MailKit doc of 4.18.2 confirmed:
   "Once this callback returns, the stream argument will be disposed". Bodies are buffered one at a time, so memory per
   batch is bounded by the largest message (same as the single fetch), not by the batch. Inside the callback only
   `onMessage` can throw (lookup, `MapFlags`, `RemoteMessage` ctor do not); the engine's `onMessage` only lets
   cancellation through (`StoreCountingFailuresAsync`), so MailKit never drops the connection because of a store
   failure. Double dispose of the stream (engine `await using` + MailKit) is harmless.
2. Out-of-order: hold back + Protocol + single fallback is sound. Buffering and sorting would hold up to 500 bodies in
   memory (unbounded by message size) – worse. IMAP servers answer FETCH in sequence order, and sequence order equals
   UID order, so the fallback should not trigger in practice; case 18 is the check.
3. B2: `deliveredUids` is recorded before storing, so a message that failed to store is not fetched again in the
   fallback (counted once). Single fallback failures use `AbortsFolder` (Network ends the folder, everything else is
   counted). Cases 3–6 hold by reading.
4. B4: every transport is disposed exactly once (variable nulled before dispose; factory failure leaves null; `finally`
   disposes the current one). Pass-abort set = cancel, `AuthenticationFailedException`, Authentication; connect and
   folder list stay outside the folder catch. A failed folder keeps its state (no `SaveAsync` reached).
5. B3: reports at start (0/0), candidates known (0/n), after every batch, at the end (success and folder failure);
   record has no names; null progress is a no-op (`FolderProgressReporter`).
6. B5 after rule 4, Google only, docs renumbered. B6: `ConnectionProbe` keeps timeout, classification, sort,
   disconnect, error texts; client disposal on connect failure moved into `ImapSession` (same effect). B7 throws
   before candidates and state.

## BLOCK
None.

## SHOULD
1. **Reconnect check placement** (`SyncEngine.cs:164-181`). Two edge effects of "check only after a failed folder,
   reconnect right away":
   - After a failure in the **last** folder the engine still counts the loss and reconnects. If the network is still
     down, the factory throws, or the count exceeds `MaxReconnectsPerPass`, and the whole pass throws, so the caller
     loses the `SyncReport` of folders that were all processed. It also costs a token fetch and connect for nothing.
   - A connection lost **without** a folder failure (fallback single fetch raising `ImapProtocolException` for the last
     undelivered UID of a folder without R7 step) is not detected. The next folder then fails on `OpenFolderAsync`
     with Network and is lost for this pass.
   Proposal for the planner: check `transport.IsConnected` **before each next folder** (index > 0), regardless of the
   previous outcome. Covers both cases; case 10 (5 folders) still aborts after the 3rd loss. Needs a small wording
   change of B4 ("after every folder failure"). The builder's literal reading is contract-conform.
2. **Stream lifetime in the Core contract** (`IMailTransport.cs:49-50`). "The callback owns Content and disposes it"
   suggests the callback may keep the stream. With MailKit it is disposed as soon as the callback's task completes.
   Add: "Content is valid only until the returned task completes; do not keep it or hand it to other work." Otherwise
   a later consumer (AP-03c/AP-04) may queue the stream and read a disposed one.
3. **Case 15 has no positive control** (`test-result.md`: "no ConnectionProbe unit test exists"). B6 changes the
   `ConnectionProbe` IMAP path (`ConnectionProbe.cs:95-123`) and rests on reading only. Planner: add "Verbindung
   testen" for both real accounts to Dennis's manual check (case 18) or a tester case.

## NIT
- `SyncEngine.cs:164` comment says "before the next folder", but the code also reconnects after the last folder.
- `SyncEngine.cs:111` XML doc "when the folder ends (also on error)": add "not when the pass aborts (cancel/auth)" –
  matches the code (`:300` catch filter) and `impl-notes.md`.
- `SyncEngine.cs:288/297/302`: an exception from a consumer's `IProgress.Report` escapes the folder catch (`:302` runs
  inside the catch) and aborts the pass. Fine, but worth one sentence in the overload doc.
- `SyncEngine.cs:175`: message built without `string.Create(CultureInfo.InvariantCulture, …)` unlike the transport
  messages; ints only, so harmless.
- `MailKitTransport.cs:179-181` and `:249-251`: same metadata `FetchRequest` items twice; a static
  `MessageSummaryItems` constant would keep single and batch fetch aligned.
- `ImapSession.cs:34` calls `ConnectionProbe.ToSocketOptions` (helper depends on its consumer). Acceptable as decided
  (SMTP check uses it too); moving it into `ImapSession` later would invert the dependency cleanly.
- `FolderRoleResolver.cs:7-8`: summary line 141 chars after the edit; reflow.
- Ich bin mir unsicher: whether MailKit's multi-UID `GetStreamsAsync` raises `MessageNotFoundException` when a UID was
  expunged between the metadata and body command (the single path catches it, `MailKitTransport.cs:202`). If it does,
  it passes `Classify` unmapped and the folder fails with that type for this pass only; worth a look in case 18 logs.
- Out-of-order fallback is silent when it succeeds (impl-notes). If case 18 shows no speed-up, count
  `FetchMessageAsync` calls in the SyncProbe first.

# review.2 (fix.2, reviewer, 2026-10-31)
Scope: `git diff 7858f40 e28523a -- src work/AP-03b/impl-notes.md` against "Decisions fix.2" (K1–K4) on top of B1–B7.
`tests\` not read. Build `dotnet build MailWarden.sln -warnaserror`: 0 warnings, 0 errors.

## Verdict: APPROVE

Checked by reading:
1. K1 (`SyncEngine.cs:149-180`): the check runs only for `folderIndex > 0`, before the folder's progress reporter and
   sync, independent of the previous outcome; nothing runs after the last folder (cases 19/20 hold). Budget:
   increment, then `> MaxReconnectsPerPass` → `MailTransportException(Network)` (case 21: 2nd loss with budget 1
   throws; case 10 with 5 folders: 4th loss throws). Disposal: budget throw → current transport disposed in
   `finally`; variable nulled before `lostTransport.DisposeAsync()` (which swallows everything but OOM), so a
   throwing `ConnectAsync` leaves null and nothing is disposed twice or leaked. The `(Result, Failed)` tuple is gone
   cleanly.
2. `SyncReport`: a failure in the last folder no longer triggers a connect, so the report with all folders is returned
   (SHOULD 1 of review.1 resolved). Budget/reconnect failure before a later folder still throws and loses the report,
   as B4/K1 require; finished folders keep their stored state.
3. K2 (`IMailTransport.cs:51-53`) and K3 (`SyncEngine.cs:110-115`, `:153-154`, class remarks `:40-42`) match the code.
4. K4 (`MailKitTransport.cs:413-414`): `MessageNotFoundException` → Protocol in `Classify`. The single fetch catches it
   around `GetStreamAsync` inside the action (`:201-209`), so `Classify` never sees it there; no side effect.
   `MailKitTransportFactory.cs:39` also uses `RunAsync`, but connect cannot raise it. The MailKit source check in
   `impl-notes.md` is plausible (multi-UID `GetStreamsAsync` documents no `MessageNotFoundException`).
5. NITs: `MessageMetadataItems` (`MailKitTransport.cs:29-31`) has the same four items as before in both fetches;
   `string.Create(InvariantCulture, …)` (`SyncEngine.cs:162-164`) formats ints only, same text; resolver summary is a
   pure reflow. No behaviour change.

## SHOULD
None.

## NIT
- `SyncEngine.cs:112-113`: "No folder-end report is sent when the pass aborts (… reconnect failure or budget)" –
  those two happen between folders, where no folder is open (and the next folder gets no start report either).
  Correct, but "when the pass aborts inside a folder (cancellation, authentication)" would be more precise.
- `MailKitTransport.cs:184-187`: a `MessageNotFoundException` from the single fetch's metadata `FetchAsync` (not raised
  by MailKit 4.18) would now be Protocol instead of unmapped. Both count as a per-message failure in the fallback;
  only the type name in the folder Error text would differ. No action needed.
- PLAN.md AP-03b row 3 still reads "Verbindungsabbruch in einem Ordner … verbindet neu"; K1 also covers a loss without
  folder error. Compatible; documenter may widen the wording when marking the row.
