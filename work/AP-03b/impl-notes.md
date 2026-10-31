# AP-03b – implementation notes (builder, implement.1)

Branch `feat/ap-03b-sync-robust-fast` (from `chore/session-handover-4` @ `1e474b8`). Contract: `requirement.md` (B1–B7).
No tests written or changed. Line numbers refer to the committed state.

## Changes per contract item
| Item | File | Lines | Change |
|---|---|---|---|
| B1 | `src\MailWarden.Core\Sync\IMailTransport.cs` | 9–12, 46–65 | `bool IsConnected { get; }` and `FetchMessagesAsync(IReadOnlyList<uint>, Func<RemoteMessage, CancellationToken, Task>, CancellationToken)` with the contract wording as XML doc. |
| B1 | `src\MailWarden.Infrastructure\Sync\MailKitTransport.cs` | 52–54 | `IsConnected` = not disposed and `client.IsConnected && client.IsAuthenticated`. |
| B1 | same | 214–347 | `FetchMessagesAsync`: one metadata `UID FETCH` (UID FLAGS INTERNALDATE RFC822.SIZE) for all UIDs, then one `UID FETCH (BODY.PEEK[])` for every UID that got metadata, via `IImapFolder.GetStreamsAsync(IList<UniqueId>, ImapFetchStreamAsyncCallback, …)`. Details below. |
| B1 | same | 40, 91, 449–456 | `openFolder` is now `IImapFolder` (cast after `GetFolderAsync`; needed for `GetStreamsAsync`); INTERNALDATE error text moved into `MissingInternalDate()`, used by single and batch fetch (wording unchanged). |
| B1 | `src\MailWarden.Infrastructure\Sync\SyncOptions.cs` | 15–55 | `FetchBatchSize` (default 50, 1..500) and `MaxReconnectsPerPass` (default 3, 0..10) as validated init properties, `ArgumentOutOfRangeException` like `InitialSyncDays`. Positional constructor unchanged. |
| B2 | `src\MailWarden.Infrastructure\Sync\SyncEngine.cs` | 383–396 | Candidates (fix.3 G1, unchanged) in ascending batches of `FetchBatchSize`. |
| B2 | same | 431–500 | `FetchBatchAsync`: batch fetch; each delivered message goes through `StoreCountingFailuresAsync` (per-message rule, F1 counting/Error text unchanged, batch continues). `MailTransportException(Protocol)` of the batch → single `FetchMessageAsync` for every batch UID not delivered yet, each with the per-message rule. `StoreAsync` = former `FetchAndStoreAsync` without the fetch (EML first, parse stored bytes, row + FTS; content disposed on every path). |
| B3 | `src\MailWarden.Infrastructure\Sync\SyncProgress.cs` | new | Record exactly as in the contract (no names). |
| B3 | `SyncEngine.cs` | 99–102, 128–193 | New overload `SyncAccountAsync(Guid, IProgress<SyncProgress>?, CancellationToken)`; the old one calls it with `null`. |
| B3 | same | 288, 297, 302, 384, 395, 708–725 | Reports via `FolderProgressReporter`: folder start (0/0), candidates known (0/n, also for n = 0), after every batch (Processed += batch size), folder end (on success and on folder failure). No report when the pass aborts (cancel/auth). |
| B4 | same | 128–193 | Transport kept in a variable; after a folder **failure** (not per-message failures) with `IsConnected == false`: count, abort with `MailTransportException(Network)` "…lost too often…" when the count exceeds `MaxReconnectsPerPass`, otherwise dispose the old transport, connect a new one via the factory, continue. Factory exception aborts the pass. `finally` disposes the current transport (never twice: the variable is nulled before the old one is disposed). |
| B4 | same | 280–305, 684–704 | `AbortsPass` (cancel, `AuthenticationFailedException`, `MailTransportException(Authentication)`) decides folder vs. pass; `AbortsFolder` (= `AbortsPass` + Network) decides per-message vs. folder. Network inside a folder → folder Error "MailTransportException: …", previous state kept. |
| B4 | same | 14–45 | Class remarks: batches, R9 change, reconnect, accepted consequence (a message that always makes MailKit drop the connection costs the rest of its folder). |
| B5 | `src\MailWarden.Core\Accounts\FolderRoleResolver.cs` | 5–10, 19–21, 44–53, 107–112 | New rule 5 (Google only, after rule 4): `[Gmail]/Important` / `[Google Mail]/Important` (ordinal, case-insensitive) → (None, hidden). Microsoft rules renumbered 6, fallback 7; XML doc list updated. Role flags are handled before by rule 2, so "without a role flag" holds automatically. |
| B6 | `src\MailWarden.Infrastructure\Providers\ImapSession.cs` | new | Internal static helper: `ConnectAsync(endpoint, userName, accessToken, timeoutMs, ct)` (new `ImapClient`, connect with `ConnectionProbe.ToSocketOptions`, XOAUTH2; client disposed on failure, exceptions unchanged) and `ListPersonalFoldersAsync(client, ct)` (personal namespaces, empty namespace root skipped). |
| B6 | `MailKitTransportFactory.cs` | 34–49 | Uses `ImapSession.ConnectAsync` inside `MailKitTransport.RunAsync` (error mapping unchanged). |
| B6 | `MailKitTransport.cs` | 56–77 | `ListFoldersAsync` uses `ImapSession.ListPersonalFoldersAsync`. |
| B6 | `src\MailWarden.Infrastructure\Providers\ConnectionProbe.cs` | 95–123 | IMAP check uses both helpers; timeout (30 s), classification, sort, disconnect and error texts unchanged. |
| B7 | `SyncEngine.cs` | 633–643 | Window search containing UID 0 → `MailTransportException(Protocol)` "The server returned UID 0 in the initial window search." Thrown in `InitialFloorAsync`, before any candidate fetch and before the state is saved. |

## MailKit API (verified against 4.18.2)
- `MailKit.xml` of the 4.18.2 package (`lib\net8.0`) lists
  `ImapFolder.GetStreamsAsync(IList<UniqueId>, ImapFetchStreamAsyncCallback, CancellationToken, ITransferProgress)`
  and the delegate `ImapFetchStreamAsyncCallback(ImapFolder folder, int index, UniqueId uid, Stream stream,
  CancellationToken ct)`. The doc says: "Once this callback returns, the stream argument will be disposed".
- Behaviour checked in the MailKit source of tag 4.18.1 (no 4.18.2 tag on GitHub; the package API matches):
  - `QueueGetStreamsCommands` → `Engine.CreateCommands(…, "UID FETCH %s (BODY.PEEK[])\r\n", uids)`: one command for
    the whole set; MailKit only splits a set whose command line would exceed the server limit (≥ 16 KB for Gmail, so
    never for 50–500 UIDs in practice). `BODY.PEEK` → `\Seen` is never set.
  - The literal is read into a `MemoryStream`/`MemoryBlockStream`, rewound to 0, then the callback is awaited
    **inline while the response is parsed**; after it returns MailKit disposes the stream. So the engine's
    `onMessage` must not issue IMAP commands (it does not: EML + SQLite only) and must finish with the stream before
    returning (it does: awaited). The engine also disposes the stream; double dispose is harmless.
  - Any exception thrown inside the callback lands in `ImapEngine.IterateAsync` → `catch (Exception) { Disconnect(ex); throw; }`:
    MailKit **drops the connection**. Therefore the transport never throws inside the callback on its own.
- Commands per batch: 2 (metadata + bodies), independent of the batch size. Before: 2 per message. Expected round
  trips for 845 messages with batch 50: 34 instead of 1,690.
- Why metadata separately: `FetchRequest`/`MessageSummaryItems` cannot request `BODY[]` content; `GetStreamsAsync`
  requests `UID BODY.PEEK[]` only. Two commands is the minimum with the public API.

## Decisions within the contract
- **Ascending order / out-of-order bodies:** bodies are requested in ascending UID order. Servers answer in sequence
  order (= UID order), but RFC 3501 does not promise it. A body whose UID is below the last delivered one is not passed
  on; after the command finished the call throws `MailTransportException(Protocol)` "…sent message bodies out of UID
  order." The connection stays usable, and the engine fetches the left-out UIDs singly (B2 fallback). Repeated bodies
  and bodies for unrequested UIDs are ignored.
- **INTERNALDATE in a batch:** a metadata answer without INTERNALDATE for a requested UID → Protocol **before** any body
  is fetched (no callback in that batch). The engine's fallback then fetches every UID singly; only the affected UID
  fails (single rule, unchanged), the others are stored.
- **Callback exceptions** are captured (`ExceptionDispatchInfo`) and rethrown unchanged after MailKit returns, so
  `RunAsync` cannot map them (e.g. an `IOException` of the callback stays an `IOException`). MailKit has dropped the
  connection by then (see above); in the engine only cancellation reaches this path.
- **Protocol failures that drop the connection** (`ImapProtocolException` from MailKit): the fallback's first single
  fetch then fails with Network (`ServiceNotConnectedException`), which ends the folder (B4) and triggers the reconnect.
  Within the contract; the fallback only helps for failures that keep the session (server NO/BAD on the FETCH, own
  INTERNALDATE/out-of-order checks).
- **Reconnect after the last folder:** taken literally ("after every folder failure … dispose, connect a new one"):
  the engine also reconnects when the failed folder was the last one, and also counts it against
  `MaxReconnectsPerPass`. Costs one extra connect at the end of such a pass; keeps cases 10/13 independent of the
  folder position.
- **Folder failure vs. per-message failures:** only a folder that ended with "&lt;ExceptionType&gt;: …" triggers the
  `IsConnected` check; a completed folder with "n message(s) failed" does not.
- **Progress:** `Processed` grows per batch (by the batch size, including fallback UIDs and skipped ones); a folder that
  fails mid-batch ends with the `Processed` of its completed batches. The "lost too often" message contains only
  counts.
- **B6 namespace root:** `ConnectionProbe` previously did not skip an empty-named folder; it would have passed it to
  `FolderRoleResolver.Resolve`, which throws `ArgumentException` (→ IMAP check failed). MailKit's `GetFoldersAsync`
  does not return the namespace root itself, so the listed folders are the same as before. The helper skips it as the
  contract asks.
- `ToSocketOptions` stays in `ConnectionProbe` (also used by the SMTP check); `ImapSession` calls it.

## Build
- `dotnet build MailWarden.sln -warnaserror`: `MailWarden.Core`, `MailWarden.Infrastructure`, `MailWarden.App` and
  `MailWarden.Core.Tests` green (0 warnings). Only `tests\MailWarden.Infrastructure.Tests` fails with 2× CS0535:
  `FakeMailTransport` (`TestSupport\FakeMailServer.cs:201`) lacks `IsConnected` and `FetchMessagesAsync` – expected,
  tester branch.

## Points for the tester / planner
- Existing tests that change meaning (category c): `SyncFolderTests.Transport_contract_has_no_write_operation`
  (method list now includes `FetchMessagesAsync`; `IsConnected` is a property getter `get_IsConnected` and shows up in
  `GetMethods()`), every test that expects a Network failure inside a folder to abort the pass (R9 change), and tests
  that inspect `FetchMessageAsync` calls (now only used by the fallback).
- Case 18 (planner): the SyncProbe can use the new overload `SyncAccountAsync(id, progress, ct)`.
- Ich bin mir unsicher whether Outlook.com answers a 50-UID `BODY.PEEK[]` FETCH in UID order; if not, every batch
  would fall back to single fetches (visible as no speed-up in case 18). The out-of-order Protocol message would then
  appear nowhere in the report (the fallback succeeds). If case 18 shows no speed-up, this is the first suspect.
