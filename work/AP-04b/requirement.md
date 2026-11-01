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
