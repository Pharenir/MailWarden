---
id: AP-04b
slug: ui-actions
title: WinUI - actions through the queue, read toggle, search, window size, link tooltip
test-scope: S3
test-filter: "Category=feature:ui|Category=feature:sync|Category=feature:storage|Category=smoke"
base: chore/ap-06-04b-requirements (builder and tester)
status: ready (2026-11-01)
---

# AP-04b - actions in the app

## Goal
Dennis can work with his mail: mark read and unread, flag, delete, move, empty a folder, search. All server actions go through the command queue (AP-03c), as the project rule demands. Runs in parallel with AP-06. Accounts and
attachments (AP-02b, opening and saving) follow as **AP-04c**, because they need their own security review; this split is the planner's.

## Decisions by Dennis (2026-11-01)
- Delete like Outlook: Del → Trash, Shift+Del → permanently, "Ordner leeren" for Trash and Junk (AP-03c).
- Selecting a mail marks it as read only with a **switch** in the settings, default off.
- Link target as a tooltip at the mouse instead of bottom left.
- The window size and position must be remembered; so far it is not stored at all (finding of his test).

## Context
- `IMailCommandQueue`: `EnqueueSetFlagsAsync`, `EnqueueMoveAsync`, `EnqueueMoveToTrashAsync`, `EnqueueDeletePermanentlyAsync`, `EnqueueEmptyFolderAsync`, `ListAsync`, `DiscardAsync`. Everything changes locally only after the server has confirmed it.
- `IMessageStore.SearchAsync`, `IMailboxReadModel`, `IMailboxController`, `ShellViewModel`, `MessageListViewModel`, `ReadingPaneViewModel`, `UiSettings`, `UiSettingsFile`, `MailWebViewHost`, `MainWindow`.
- Deferred to this AP in `TODO.md`: confirmation of "Ordner leeren" shows the snapshot count (secure N2 of AP-03c); note when Shift+Del only reached the Trash (Q1); show and discard failed commands.
- Project rule: no account names or addresses of Dennis in the repo; only `Konto 1`, `Konto 2`, `Konto 3`.

## Non-goals
- No account UI, no attachments, no compose. No schema change (AP-06 owns v6). No change to `SyncEngine`, `CommandExecutor`, `Infrastructure\Classification` (AP-06 works there).
- No rules, no "Warum?" view (AP-08). No new package.

## Contract (binding)

### D1 - actions (`MailWarden.Infrastructure.Composition`)
```csharp
public sealed record PendingAction(long CommandId, Guid AccountId, MailCommandKind Kind, MailCommandStatus Status, IReadOnlyList<long> MessageIds, string? LastError);
public interface IMailboxActions
{
    Task SetSeenAsync(IReadOnlyCollection<MailMessage> messages, bool seen, CancellationToken ct = default);
    Task SetFlaggedAsync(IReadOnlyCollection<MailMessage> messages, bool flagged, CancellationToken ct = default);
    Task MoveToTrashAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken ct = default);
    Task DeletePermanentlyAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken ct = default);
    Task MoveAsync(IReadOnlyCollection<MailMessage> messages, long targetFolderId, CancellationToken ct = default);
    /// Number of local mails right now; the UI shows it in the confirmation.
    Task<int> CountForEmptyAsync(long folderId, CancellationToken ct = default);
    Task EmptyFolderAsync(long folderId, CancellationToken ct = default);
    Task<IReadOnlyList<PendingAction>> ListPendingAsync(CancellationToken ct = default);
    Task DiscardAsync(long commandId, CancellationToken ct = default);
}
```
- Implementation on its own connection, serialised. Messages are grouped per folder, at most 1,000 UIDs per command; UIDVALIDITY from the folder read now.
- After each successful enqueue: `TriggerAccount` per account.
- A mail already in the Trash: `MoveToTrashAsync` is refused by the queue, so the UI asks and uses `DeletePermanentlyAsync`.
- A queue rejection (`ArgumentException`, `InvalidOperationException`) surfaces as `MailActionException` with a fixed text; nothing partial stays hidden: commands already stored stay.
- `MessageIds` of `PendingAction`: resolved from the command's folder and UIDs; a UID no longer stored is skipped. `LastError` is shown only as a fixed text, see D5.

### D2 - view models
```csharp
public sealed class ShellViewModel
{
    // new
    public ICommand MarkReadCommand { get; }
    public ICommand MarkUnreadCommand { get; }
    public ICommand ToggleFlagCommand { get; }
    public ICommand DeleteCommand { get; }             // Del; asks when the mail is in Trash
    public ICommand DeletePermanentlyCommand { get; }  // Shift+Del; always asks
    public ICommand EmptyFolderCommand { get; }        // only Trash and Junk; asks with the count
    public ICommand MoveCommand { get; }               // parameter: target folder id
    public bool MarkReadOnSelect { get; set; }         // saved; default false
    public IReadOnlyList<PendingActionRow> FailedActions { get; }
    public ICommand DiscardFailedActionCommand { get; }
    public event EventHandler<ConfirmationRequest>? ConfirmationRequested;
}
public sealed record ConfirmationRequest(string Text, TaskCompletionSource<bool> Answer);
public sealed class MessageListViewModel { public IReadOnlyList<MessageRowViewModel> SelectedRows { get; set; } /* multi-selection */ }
public sealed class SearchViewModel : ObservableObject
{
    public string Query { get; set; }
    public bool IsSearching { get; }
    public Task SearchAsync(CancellationToken ct = default);   // empty or blank: back to the previous view
}
```
- **Selection:** actions work on `SelectedRows`. Reading pane shows the row when exactly one is selected; otherwise `Empty`.
- **After an action:** rows of a pending move or delete disappear from the list at once (hidden by id from `ListPendingAsync`, also across reloads, including in other views for the same message) and come back when the command failed or was discarded. Flag and read state of a row show the wanted state at once. The server stays the truth: `ReloadAsync` after the command applies it. The next row is selected after a delete (as in Outlook), and the reading pane shows it.
- **Confirmation** through `ConfirmationRequested`: permanent delete with the count; empty folder with the count from `CountForEmptyAsync` and the words of the folder role; never a sender or subject in the text. No answer or false: nothing is enqueued.
- **Read on select:** with the switch on, showing a mail for at least 1.5 s (`TimeProvider`) marks it as read; leaving earlier does not. With the switch off, selecting changes nothing on the server (case 48 of AP-04a stays true).
- **Move:** target list from the account's folders; a target of another account is refused before the queue, and multi-selection over several accounts disables move.
- **Failed actions:** `FailedActions` holds status `Failed`, with fixed text per kind. Discarding brings the rows back. A banner source `Actions` names the count.
- **Shift+Del without COPYUID (Q1 of AP-03c):** the mail may only reach the Trash. When a command `DeletePermanently` is done and a mail with the same `MessageId` is present in the Trash after the next reload, the banner says so once. Best effort, documented.

### D3 - search
`IMailboxReadModel` gets `SearchAsync(string text, int limit, ct)`. `SearchViewModel` runs it (limit 500) and shows results in the list like a view, with the account label; navigation selection stays but is not highlighted as active. Newest wins (stale guard). An invalid or empty text does not throw. Search is local (FTS). Rows respect the hiding of D2. Search text goes to no log.

### D4 - settings and window
`UiSettings` gets `bool MarkReadOnSelect` (default false), `double? WindowWidth`, `double? WindowHeight`, `int? WindowX`, `int? WindowY`, `bool WindowMaximized`. Keys `markReadOnSelect`, `windowWidth`, `windowHeight`, `windowX`, `windowY`, `windowMaximized`; range width and height 400..20000, X and Y -20000..20000. `App` applies them on start and stores them on close and when resizing ends. A position no longer on any monitor is ignored (placed by the system). `MainWindow` shows a switch in a small settings flyout. The DPI of a monitor must not change the size on restart beyond rounding.

### D5 - keyboard and menu
Del, Shift+Del, Ctrl+Q (read), Ctrl+U (unread) in the message list; a toolbar with Read, Unread, Flag, Delete, Move (menu), Empty (only Trash and Junk); a context menu on rows with the same. The list allows multi-selection (Ctrl, Shift). Keys do not fire while the search box or a dialog has the focus. `ContentDialog` answers `ConfirmationRequested`.

### D6 - link tooltip
`CoreWebView2.StatusBarTextChanged` drives a tooltip at the mouse, cleaned with `SafeDisplayText.Shorten(text, 500)`. `IsStatusBarEnabled = false`. If the tooltip cannot be placed at the mouse over the WebView2, keep the status bar and say so in the notes instead of building something fragile: the target must stay visible. Pure logic (which text, when empty) in the view model: `ReadingPaneViewModel.HoverLinkText`.

## Expected behaviour (fakes, real queue and SQLite for D1)
| # | Case | Expected |
|---|---|---|
| 1 | D1 per action, one folder; several folders; several accounts | one command per folder, correct kind, target, UIDVALIDITY; trigger per account |
| 2 | 2,500 mails | commands of at most 1,000 UIDs, all covered |
| 3 | Move to the Trash from the Trash; unknown folder; target of another account | `MailActionException` with fixed text; nothing stored for this group |
| 4 | `CountForEmptyAsync`, `EmptyFolderAsync` on Trash and Junk; on the Inbox | count equals the local mails; only then a command; rejected |
| 5 | `ListPendingAsync`: pending and failed, UID gone | ids resolved; gone UID skipped |
| 6 | `DiscardAsync` | command gone |
| 7 | Delete command, one row | row gone at once, next row selected, reading pane shows it; list reload keeps it hidden |
| 8 | Failed delete; discard | row back after discard, `FailedActions` count, banner, fixed text |
| 9 | Permanent delete, no and yes | no: no command, row stays; yes: command |
| 10 | Del on a mail in the Trash | asks, then permanent |
| 11 | Empty folder, count in the text, no sender or subject in any confirmation (marker) | per D2 |
| 12 | Mark read and unread on 3 rows, flag toggle | state at once, command, server truth after reload |
| 13 | Read on select off | no command, ever |
| 14 | Read on select on: 1.4 s then leave; 1.6 s; mail already read | none; one; none |
| 15 | Move: target menu, other account, multi-selection across accounts | per D2 |
| 16 | `SelectedRows` 0, 1, 2 | reading pane Empty, row, Empty; commands enabled accordingly |
| 17 | Search: result, empty text back to the view, stale result, syntax characters, hidden row | per D3 |
| 18 | Settings: new keys, range, missing keys default, round trip; position off every monitor (pure helper) | per D4 |
| 19 | `HoverLinkText`: set, cleared, bidi, 10,000 characters | cleaned and at most 501 characters |
| 20 | Action during `Dispose` | nothing after dispose, no critical log line |
| 21 | Manual, Dennis, copy + test mails on `Konto 2` and `Konto 3` only (planner names them): read, flag, delete, permanent, move, empty Junk with his own check; webmail matches | as stated |
| 22 | Manual, Dennis: window size and position after a restart, also maximised; tooltip at the mouse; keys | as stated |

## Done when
Build `-warnaserror`; S3 green; review APPROVE; security without Critical or High (trigger: actions that delete from the UI). Documenter: `README.md`, PLAN rows, `work\manual-tests.md` with a safe order for cases 21 and 22, TODO.

## Decisions fix.2 (planner, 2026-11-01, after verify.1 PASS 2300/2300, review.1 BLOCK, secure.1 0/0/1/5)
Binding; they change D1, D2, D6.
- **U1 (review B1).** Every action works on a copy of the selection taken when the command executes, passed into the action; rows that are hidden by then are filtered out at the start. A second Del before the first row is hidden does nothing rather than hitting the next row. This holds for read, unread, flag, delete, permanent delete and move.
- **U2 (secure M1).** `EmptyFolderAsync` and the preview refuse with `MailActionException` while `queue.ListAsync` holds a pending `Move` or `MoveToTrash` for this folder: a mail on its way out must not be deleted.
- **U3 (secure N1, changes D1).** `CountForEmptyAsync` becomes `Task<EmptyFolderPreview> PreviewEmptyAsync(long folderId, CancellationToken ct)` with `public sealed record EmptyFolderPreview(int Count, uint HighestUid)`. `EmptyFolderAsync(long folderId, EmptyFolderPreview confirmed, CancellationToken ct)` refuses with `MailActionException` when the local highest UID is now above `confirmed.HighestUid` (a mail arrived that Dennis has not seen); the UI then asks again. Lower is fine.
- **U4 (secure N2, N3).** A rejection after some groups were stored says "teilweise ausgeführt" through a fixed text of its own. Confirmations name the number of accounts when more than one is involved; the empty-folder confirmation names the cleaned account label of the folder, because it is his own account, not foreign data.
- **U5 (review S1, secure N5, changes D6).** The status bar **stays on** for now (`IsStatusBarEnabled = true`) and the tooltip comes on top, so the target can never be invisible. Case 22 decides about switching the bar off. `HoverLinkText`: for an absolute http or https target longer than the limit, the host comes first (`Uri.Host`), then the cut text.
- **U6 (review S2).** The state of "Ordner leeren" is updated after a search starts and ends.
- **U7 (review S3).** Nothing is stored after `Dispose`, also when it happens during an await inside an action; the check is right before enqueuing.
- **U8 (review S4, secure N4).** Key repeat is ignored (`KeyStatus.WasKeyDown`): holding Del acts once.
- **U9 (review DPI).** Restoring the window: move first, then set the size, so a monitor with another scale cannot grow it on each start. Focus returns to the list after a row was removed.
- **Into `TODO.md`:** repeated identical flag commands; `SyncListSelection` on Ctrl+A; read timer versus manual unread in the same window; `ToPendingAction` ignoring UIDVALIDITY; `InvalidOperationException` text; a reason enum instead of German texts in Infrastructure.

### Additional test cases (fix.2)
| # | Case | Expected |
|---|---|---|
| 23 | Action blocked on the gate, selection changes to another row, then it runs | the row selected at the keystroke is acted on, never the new one; permanent delete with a pending confirmation likewise |
| 24 | Del twice fast on one row | one command, the next row untouched |
| 25 | Pending `Move` from Junk (also after a failed attempt in backoff); preview and empty | `MailActionException`; nothing stored; positive control without it |
| 26 | `EmptyFolderAsync`: mail arrives after the preview; a mail disappears after the preview | refused, UI asks again with the new count; goes ahead |
| 27 | Rejection after one stored group | banner says partly done |
| 28 | Confirmations: selection over 2 accounts; empty folder | number of accounts; account label; no sender or subject |
| 29 | `HoverLinkText` over 500 characters with a host; with user info | host first; no foreign host in front |
| 30 | Search started and ended in the Trash | `CanEmptyFolder` follows |
| 31 | `Dispose` during an await in delete | nothing stored |
| 32 | Manual, Dennis (replaces part of 22): link with a text other than its target shows the true target in the tooltip **and** the status bar | both visible, bar off only after this |

## Decisions fix.4 (planner, 2026-11-01, after verify.3 PASS 2336/2336, review.3 APPROVE with S1-S3)
This is round 4 of 4; anything left afterwards goes to Dennis.

Decided in fix.3 (recorded here because they were only in `impl-notes.md`): V1 no empty after new mail; V2 partly-done report on a storage failure, type name only in the log; V3 read and flag go to the follow row, delete, permanent delete and move skip it; V4 no `ActionRowsRemoved` after `Dispose`, `EndAction` in `finally`. Actions run one after the other in keystroke order, so read on the follow row waits for the running delete (accepted, not a defect).

Why this round: "Ordner leeren" is the only irreversible path of this AP. review.3 S1 (a failing discard leaves an unconfirmed command) and S2 (count and highest UID do not identify what was confirmed) can each expunge a mail the user never counted.

- W1: "Ordner leeren" deletes only what was confirmed. `EmptyFolderPreview` carries `UidValidity` and the set of UIDs, plus the count for the dialog.
- W2: Check and insert are one write transaction in `SqliteMailCommandQueue`: same UIDVALIDITY, and the local UIDs are a subset of the confirmed ones, read inside the transaction. On a mismatch nothing is stored and the caller gets the existing "mail arrived" refusal. Any failure rolls back and stores nothing.
- W3: The second check and the `DiscardAsync` call in `MailboxActions.EmptyFolderAsync` go away; the transaction replaces them. `DiscardAsync` itself stays for its other callers.
- W4: The shell compares previews by content (UIDVALIDITY and UID set), not by record equality. Same preview after a refusal: stop with the error as today. Different preview: ask again, up to `MaxEmptyConfirmations`.
- W5: review.3 S3: one helper for the partial-failure rule in `MailboxActions` and `ShellViewModel`.
- W6: `PartlyDoneText` no longer says "abgelehnt", because a storage failure refuses nothing.

### Additional test cases (fix.4)
| # | Situation | Expected |
|---|---|---|
| W-a | Confirmed {10, 20}, local {15, 20} (same count, same highest UID) | Refused, no command stored |
| W-b | UIDVALIDITY changed, local UIDs a subset of the confirmed ones | Refused, no command stored |
| W-c | Local UIDs a proper subset of the confirmed ones | Command holds exactly the local UIDs |
| W-d | Nothing local any more | No command, no failure |
| W-e | Insert fails (SQLite trigger) | Nothing stored, error reaches the caller |
| W-f | Refused, new preview differs | Dialog asks again with the new count |
| W-g | Refused, new preview equal in content | Stops with the error, no further question |

## Decisions after Dennis's manual test (planner, 2026-11-01)
Dennis tested cases 21, 22, 32: everything as expected, with one wish: "Ordner leeren" should also appear on a right click on the folder.

- X1: Right click (and context key) on a folder in the tree offers "Ordner leeren" when, and only when, that entry can be emptied: exactly one Trash or Junk folder behind its view. Other entries show no menu, not an empty one.
- X2: The action targets the folder that was right-clicked, not the selected view. The selection does not change. It takes the existing path: `EmptyFolderAsync(view)` with the question, the confirmed UID set and the refusal of fix.4. An active search does not block it, because the question names folder, count and account.
- X3 (Dennis 2026-11-01, replaces the first version): The unread number in the tree drops the moment the mails leave the view, for delete, permanent delete, move and empty. If the action fails and the rows come back, the number comes back too. For an empty folder whose mails are not in the current list, the number drops as soon as the command is stored. Read and unread are not part of this.

### Additional test cases (X)
| # | Situation | Expected |
|---|---|---|
| X-a | Trash and Junk of one account | Entry can be emptied |
| X-b | Inbox, Sent, Archive, a custom folder, account node, tree parent | Entry cannot be emptied |
| X-c | Group Junk with one account that has this folder (the tree has no group entry for Trash; for Trash the rule is tested on `MailView.Group(FolderRole.Trash)`) | Entry can be emptied |
| X-d | Group Junk or Trash with two accounts having this folder | Entry cannot be emptied |
| X-e | View of Konto 2 Inbox selected, empty requested for Junk of Konto 3 | Question names Konto 3 and its count; the command holds the UIDs of that Junk; the current list and selection stay |
| X-f | Empty requested while a search is active | Same path, command stored |
| X-g | Folder without local mails | No question, no command |
| X-h | Manual, Dennis: right click on Junk of Konto 3 and on Posteingang; context key | Menu only on Junk |

- X4 (Dennis 2026-11-01, after case 32): The status bar of the reading pane is off (`IsStatusBarEnabled = false`). The tooltip at the mouse remains the only display of the link target.

### Additional test cases (X3)
| # | Situation | Expected |
|---|---|---|
| X-i | Unread mail in a folder, Del | Number of the folder and of its group view drop before the command runs |
| X-j | Same with a refused action | Number returns to the old value |
| X-k | Read mail, Del | Number unchanged |
| X-l | Empty folder with unread mails beyond the loaded page, or not in the current list | Number 0 once the command is stored |
| X-m | The command has run | Number stays correct, not subtracted twice |

## Decisions after review.5 and secure.4 (planner, 2026-11-01)
- Y1 (secure.4 M1): `title` leaves the allowed attributes of the sanitizer. With the status bar off, the hint at the mouse is the only display of a link target, and a mail must not place its own text there.
- Y2 (secure.4 L2): the link hint fits a narrow window: `MaxWidth` is the smaller of 600 and the window width minus both edge distances.
- Y3 (review.5 S1): in the role view of one account the filter uses `f.account_id` instead of `m.account_id`, so the recount of X3 takes the moment it should. Counts stay the same.
- Y4: secure.4 L3 (the question names the role, not the folder name) goes to `TODO.md`; the rule "never a folder name from the server" stays for now.
- Y5: Status bar off needs its own manual proof: bar off, link text differs from the target, the hint shows the true target. Without it no merge.

### Additional test cases (Y)
| # | Situation | Expected |
|---|---|---|
| Y-a | `<a href="https://evil.example.test/" title="https://www.bank.example.test/login">`, also `title` on a `<span>` inside the link | No `title` in the sanitized HTML, the `href` stays |
| Y-b | Same counts as before for folder, role and account plus role views, with and without excluded ids, on SQLite | Equal to a count over the same rows |
| Y-c | Manual, Dennis: link with a different text, status bar off, window wide and narrow | Hint shows the true target, wholly readable |

Y3 kept (planner, 2026-11-01). Measured on the real driver with 180,000 artificial mails, the recount of the role entries goes from about 319 ms to 55 ms when most mail lies in normal folders, which is how Dennis's mailbox is distributed, and from about 320 ms to 450-500 ms when most mail lies in the role folders. Opening an empty role view goes from about 500 ms to 0. If the number is still felt as late, one `GROUP BY folder_id` query for the whole tree is the next step (`TODO.md`).
