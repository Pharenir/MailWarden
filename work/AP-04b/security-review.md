
# AP-04b - secure.1 (2026-11-01)

**Result: 0 Critical, 0 High, 1 Medium, 5 Low.** M1: "Ordner leeren" also takes mails with a pending move → U2. N1 same count is not same mails → U3. N2, N3 → U4. N4 → U8. N5 → U5. Code reading only.

# secure.3 (2026-11-01; written down by the planner, the role has no write access)

Delta `f8bb513..6e6428d -- src`, trigger: permanent-delete path of "Ordner leeren". `tests\` not read.

**Result: 0 Critical, 0 High, 0 Medium, 1 Low, 3 Notes.** Nothing can be expunged outside the confirmed set; the executor sends only the stored UIDs after a UIDVALIDITY check.

## Findings

### L1: The pending-move check is outside the transaction, and a later classifier rescue is overtaken
- **Severity:** Low. Every mail lost this way was in the confirmed set, so the count covered it. It takes narrow timing and Act mode, but the loss is irreversible.
- **Location:** `src\MailWarden.Infrastructure\Composition\MailboxActions.cs:102` and `:331-337` (check), `src\MailWarden.Infrastructure\Storage\SqliteMailCommandQueue.cs:140` (transaction starts later), `src\MailWarden.Infrastructure\Classification\ClassificationPass.cs:146` and `:456-463`, `src\MailWarden.Core\Classification\ClassificationRouting.cs:12` (Junk + TrustedContact → Inbox), `src\MailWarden.Infrastructure\Background\MailboxService.cs:470-471`.
- **Why the gate does not help:** `connectionGate` only serialises the UI actions. The classification pass enqueues moves out of Junk on its own connection.
- **Scenario A (milliseconds):** the classifier stores a `Move` between the check and `BEGIN IMMEDIATE`, so the empty command is stored after it. The `Move` fails transiently and goes into backoff. The empty command is due and expunges the mail on its way out. This is the case U2 was meant to prevent.
- **Scenario B (longer window):** sync stores a trusted contact's mail in Junk; classification only runs after all folders are synced (`src\MailWarden.Infrastructure\Composition\ClassifyingSyncCycle.cs:56-57`). In that window the user empties Junk and the mail is in the count. No move is pending yet, so the check passes. Classification then stores the rescue `Move`, which the queue accepts because the UID is still local. The next cycle runs the empty command first (lower id) and the mail is gone, while the classification log still records the move as enqueued.
- **Recommendation:** A: repeat the pending-move check inside the transaction of `EnqueueEmptyFolderAsync`. B: a decision for Dennis, because the mail was counted: accept it, or for Junk refuse when a local UID is above `ClassificationMarker.ClassifiedUpToUid`. The refusal needs a rule for folders without a marker and changes W-c.

### N1: The confirmation covers a count, not the rows on screen
- **Severity:** Note, accepted by W1.
- **Location:** `src\MailWarden.Presentation\ShellViewModel.cs:1108-1111`, `src\MailWarden.Presentation\UiText.cs:220-225`.
- **Scenario:** a mail synced just before the keystroke, not yet in the list, is in the preview. The user sees "4 Mails" above a list of 3.

### N2: Protection relies on the server keeping the IMAP UID guarantee
- **Severity:** Note, no action.
- **Location:** `src\MailWarden.Infrastructure\Storage\SqliteMailCommandQueue.cs:148` and `:161`, `src\MailWarden.Infrastructure\Commands\CommandExecutor.cs:225-252`.
- **Scenario:** a server reuses a UID under the same UIDVALIDITY, or changes UIDVALIDITY between SELECT and UID STORE, and another mail is expunged. No message identity is compared.
- **Assessment:** a hostile server can delete the mail itself, so a client check would add little. Only a non-compliant server is a real concern.
- `Ich bin mir unsicher:` MailKit may format a UID range ending at `uint.MaxValue` as `n:*`. Recalled from memory, not checked. It requires a server that assigns UID 4294967295 next to 4294967294 (`src\MailWarden.Infrastructure\Sync\MailKitCommandTransport.cs:329`).

### N3: `\Deleted` can stay on mails after a discarded command (not from this delta)
- **Severity:** Note.
- **Location:** `src\MailWarden.Infrastructure\Sync\MailKitCommandTransport.cs:181`, `:191`, `:260-272`.
- **Scenario:** a crash or lost connection comes after UID STORE, and the best-effort unmark does not run or fails. The command later ends as Failed and the user discards it. The mail keeps `\Deleted` on the server, and another client's plain EXPUNGE removes it.
- **Limit:** only UIDs the user confirmed are affected. While the command stays pending, the retry stays on the same UIDs.
- **Recommendation:** put it in `TODO.md`; it does not block this AP.

## Threat view, checked by reading

| Case | Result |
|---|---|
| Mail arrives after the preview and is synced | Not in the confirmed set: refused, nothing stored (`SqliteMailCommandQueue.cs:161`); the shell asks again with the new preview |
| Mail arrives after the commit, or is on the server but unsynced | Not in the command's UIDs, so not touched |
| Mail arrives before the preview | Counted, see N1 |
| Sync on another connection | `BEGIN IMMEDIATE` comes before the first read, so the UIDVALIDITY read, UID read and insert form one write transaction |
| UIDVALIDITY reset, sync order | The sync steps run (V1, old) → (V1, empty) → (V2, empty) → (V2, new) (`SyncEngine.cs:327-342`). No state pairs V2 with old UIDs |
| Reset during preview or dialog | The current value differs from the confirmed one, so it is refused |
| Reset after storing | Executor: `UidValidityChanged`, permanent failure, nothing sent (`CommandExecutor.cs:225-229`) |
| UIDVALIDITY 0 | Refused by the queue and by the transport's SELECT |
| UID 0 from a server | `NormalizeUids` throws inside the transaction: rollback, refusal |
| Crash before commit | Rolled back, nothing stored |
| Crash after commit, or command pending over a restart | Runs later with exactly the stored UIDs and UIDVALIDITY |
| Crash between server expunge and local effect | Re-run hits UIDs that are gone. Rows and command are cleaned up afterwards |
| Partial failure of the expunge | Retry uses the same fixed set |
| Old discard path (review.3 S1) | Removed; no command is stored before the checks. Only `triggerAccount` runs after the commit |
| Role change before execution | Role is re-read inside the transaction. At execution a changed role goes through `MoveToTrashAndExpungeAsync`, same UIDs |
| Folder removed from the server list | Foreign key cascade drops the command, so nothing is deleted |
| Stale confirmation in the shell | Each question uses its own preview; comparison is by content; ends at `MaxEmptyConfirmations` or with the error |
| Delta in `StartOnSelection` / `StartRemovalOnSelection` | Permanent delete and move still skip the follow row; only read and flag reach it, and both can be undone |

**Executor:** for this command it sends `transport.ExpungeAsync(command.Uids)` only (`CommandExecutor.cs:251-252`), after SELECT of the stored folder id and the UIDVALIDITY comparison. The transport requires UIDPLUS and PERMANENTFLAGS `\Deleted`, and sends no plain EXPUNGE or CLOSE. Stored UIDs are read back strictly ascending and non-zero; an unreadable row becomes Failed and never runs.

**Log lines: clean.** `TryGetPartlyDone` and `ShellViewModel.cs:903` write a literal source, a fixed text and `GetType().Name` only. `PartlyDoneText`, `MailArrivedText` and `EmptyFolderChangedException` are fixed texts. The `StorageException` message from `SqliteValues.Wrap` reaches neither log nor banner on this path.

## Verified how
- **By reading:** the delta plus `CommandExecutor`, `MailKitCommandTransport`, `SqliteMailCommandQueue`, `MailboxActions`, `SyncEngine` (reset path), `ClassificationPass`, `ClassificationRouting`, `ClassifyingSyncCycle`, the shell action path, and `work\AP-04b\requirement.md`.
- **Run:** only `git` (status, log, diff, rev-parse) and text search. No tests, build or GUI.
- **Not verified:** Microsoft.Data.Sqlite behaviour is from memory, not its source (`CreateCommand` joining the open transaction, `deferred: false` meaning `BEGIN IMMEDIATE`, rollback on dispose). Server behaviour for UIDs that no longer exist was not exercised.

## Public entry points
None, and the delta adds none. The local OAuth loopback redirect during login is intended and is not touched by this delta.

# secure.4 (2026-11-01; written down by the planner, the role has no write access)

Delta: commits `3f1d0f0` and `cbcb564` (X1-X4). `tests\` not read.

**Result: 0 Critical, 0 High, 1 Medium, 3 Low, 4 Notes.** Nothing can expunge a folder other than the one clicked. `json_each` is parameterised. Exclusion cannot hide mail permanently.

## Findings

### S4-M1: A mail's `title` attribute shows fake text at the mouse, possibly over the real hint
- **Severity:** Medium. Since X4 the hint is the only display of the target, and it is placed where the browser's own tooltip appears.
- **Location:** `src\MailWarden.Infrastructure\Mime\MailHtmlSanitizer.cs:80`, `src\MailWarden.App\MainWindow.xaml.cs:34-35`, `src\MailWarden.App\MailWebViewHost.cs:178`.
- **Scenario:** `<a href="https://evil.example.test/" title="https://www.bank.example.test/login">`, also with `title` on a `<span>` inside the link. After the hover delay the browser shows the fake address beside the real hint; a long `title` makes it large.
- `Ich bin mir unsicher:` that the WebView2 tooltip draws above the XAML hint (from memory, not run).
- **Recommendation:** remove `title` from `AllowedAttributes`; test with an artificial mail.

### S4-L1: Bar off is not tested, and nothing catches a hint that never appears
- **Severity:** Low as long as the open test passes. If `StatusBarTextChanged` does not fire with the bar off, link targets are invisible and clicks still open, which would be High.
- **Location:** `src\MailWarden.App\MailWebViewHost.cs:177-178`, `:190-196`.
- **Scenario:** case 32 ran with the bar on, so it does not prove the event fires with it off.
- **Recommendation:** manual case before merge: bar off, link text differs from target, hint shows the true target; include Tab onto a link.

### S4-L2: In a narrow window the end of a long host name is cut off
- **Severity:** Low. Needs a window narrower than about 616 px; inferred, not run.
- **Location:** `src\MailWarden.App\MainWindow.xaml:290`, `:297`; `src\MailWarden.App\MainWindow.xaml.cs:349`, `:359`. No minimum window size exists.
- **Recommendation:** before `Measure`, `LinkHint.MaxWidth = Math.Min(600, RootGrid.ActualWidth - 2 * LinkHintEdgeDistance)`.

### S4-L3: The question names the role, not the folder that was clicked
- **Severity:** Low. The right folder is emptied in every case; only the wording falls short of X2.
- **Location:** `src\MailWarden.Presentation\UiText.cs:220-226`, `src\MailWarden.Presentation\NavigationViewModel.cs:263-264`, `src\MailWarden.Core\Accounts\FolderRoleResolver.cs:38`.
- **Scenario:** a folder the server flags `\Trash`: the user clicks "Rechnungen" and reads "Papierkorb von Konto 2 leeren: 340 Mails". Needs a wrong server flag or a hostile server.
- **Recommendation:** add the cleaned, shortened folder name to the question; changes the rule "never a folder name from the server".

### Notes
- **S4-N1:** a mailto tail above the 500-character limit is cut (`src\MailWarden.Presentation\MailDisplayText.cs:96`); it only opens a compose window that shows the recipients. No action.
- **S4-N2:** Unicode hosts up to 500 characters show what the browser reports (`MailDisplayText.cs:80-83`). `Ich bin mir unsicher:` whether `StatusBarText` applies Chromium's spoof check. Add a look-alike host in the artificial `.test` domain to the manual case.
- **S4-N3:** `ToPendingAction` maps UIDs to ids without comparing UIDVALIDITY (`src\MailWarden.Infrastructure\Composition\MailboxActions.cs:212-224`), so a new mail with a reused UID is hidden, now also from the number, until the executor fails the command. Not new.
- **S4-N4:** secure.3 L1 is unchanged. A right click also lets the user empty a Junk folder whose list is not on screen, so only the count is confirmed; X2 accepts that.

## Verified how
- **By reading:** the two commits and the code they reach, `requirement.md`, `impl-notes.md`, earlier reviews. The folder target is an immutable view taken from the item raising the event; `FindEmptyTargetAsync` re-reads folders and needs exactly one Trash or Junk candidate; only `folder.Id` is used afterwards. The fix.4 transaction is untouched since `6e6428d`. The click is decided separately from the hint by `WebContentPolicy.DecideNavigation`. Hidden ids exist only in memory; only Pending commands count after a restart.
- **Run:** only `git` and read-only text commands. No tests, build or GUI.
- **Not verified:** WebView2 at runtime (event with bar off, tooltip stacking, content of `StatusBarText`), the tree flyout, the `json_each` filter on SQLite (the tester's run covers it).
- **Long but not permanent:** network or sign-in failures keep a command Pending without limit (`CommandExecutor.cs:162`); rows and number stay hidden until it runs. Other failures become Failed after 5 attempts.
