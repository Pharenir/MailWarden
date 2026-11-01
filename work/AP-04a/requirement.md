---
id: AP-04a
slug: ui-read
title: WinUI shell – runtime composition, group views, message list, safe reading pane, switchable layout (read-only)
test-scope: S3
test-filter: "Category=feature:ui|Category=feature:storage|Category=feature:sync|Category=smoke"
base: chore/ap-04a-07c-requirements (builder and tester)
status: ready (2026-11-01)
---

# AP-04a – WinUI shell, read-only

## Goal
The app becomes usable for reading: it starts the background service, shows all accounts in the group views and per
account, lists messages and renders the selected message safely. It **never changes anything on the server by itself**
(no flags, no moves; the service only executes commands that already are in the queue).

AP-04b follows with the account UI (AP-02b), write actions, search and attachments.

## Decisions by Dennis (2026-11-01)
- AP-04a and AP-07c run in parallel.
- HTML cleaning uses the NuGet package **HtmlSanitizer** (mganss; it brings AngleSharp). Use the latest stable version
  and add it to `MailWarden.Infrastructure`. No other new package without asking.
- Layout: navigation always on the left; the message list and reading pane are switchable between **side by side** and
  **list above reading pane**. The choice is remembered.

## Context
- `MailWarden.App` is an empty AP-01 window (`src\MailWarden.App\App.xaml.cs`, `MainWindow.xaml`).
- The composition to move into production code: `_scratch\service-probe\Program.cs`, class `AccountPipelines`
  (own SQLite connection per account cycle and per IDLE watcher, shared token providers and credential store).
- `SqliteDatabase` holds one connection and is not thread-safe.
- `MailboxService` (`src\MailWarden.Infrastructure\Background\MailboxService.cs`), `IMessageStore.ListAsync(MessageQuery)`,
  `IFolderStore`, `IAccountStore`, `IEmlStore`, `MimeBodyText.Extract`.
- Hidden server folders are never stored locally, so all stored folders can be shown.
- Items deferred to this AP, all in `TODO.md`: review O1/O2/G3 of AP-03d, status filter against the account list,
  AP-04 start error for an invalid `MAILWARDEN_DATA_DIR`, control/bidi characters in displayed text, EML read into
  memory, logging rule (type names only, never exception text).

## Non-goals
- No write action of any kind: marking read, delete, move, empty folder. Opening a message does **not** set `\Seen`.
- No account UI, no interactive sign-in (`NeedsSignIn` is only shown; accounts still come from the AuthProbe).
- No search, no opening or saving of attachments (their names are listed only), no compose, no threads, no tray icon,
  no drag and drop, no keyboard-shortcut set.
- `<style>` elements are removed for now. Inline `style` attributes stay. Dennis judges the look in the manual test.
- No dark rendering of mail content: the reading document always has a light background (PLAN, risks).

## Contract (binding)
Layers: `Core` ← `Infrastructure` ← new `MailWarden.Presentation` ← `App`. `MailWarden.Presentation` targets
`net9.0-windows`, has **no WinUI reference** and no new package; its small `ObservableObject` and command classes are
written here. Tests go to the new `tests\MailWarden.Presentation.Tests` (tag `feature:ui`, added by the tester).
UI texts are German and live in one class `UiText`. Keep `MailWarden.sln` without BOM.

### D1 – display text (`MailWarden.Core.Text`)
```csharp
public static class SafeDisplayText
{
    /// Removes control characters and bidi controls from untrusted text before it is displayed.
    /// CR, LF and TAB become one space each. Removed: other Cc characters, U+200E, U+200F, U+061C, U+202A–U+202E,
    /// U+2066–U+2069. Other text is unchanged, no trimming. Null → ArgumentNullException.
    public static string Clean(string text);
}
```
Every server- or mail-derived string shown by a view model (subject, sender, recipients, folder names, attachment
names, preview, account name) goes through it.

### D2 – message content (`MailWarden.Infrastructure.Mime`)
```csharp
public sealed record MailAttachmentInfo(string FileName, string ContentType, long? Size);
public sealed record MailContent(string Subject, string From, IReadOnlyList<string> To, IReadOnlyList<string> Cc,
    DateTimeOffset? Date, string? Html, string PlainText,
    IReadOnlyDictionary<string, string> InlineImages, IReadOnlyList<MailAttachmentInfo> Attachments);

public static class MailContentReader
{
    /// Parses a complete EML from the stream into memory; the stream is not disposed. Afterwards nothing refers to it.
    /// Exception: MailContentException (no message text of the mail) when the stream cannot be parsed as a message.
    public static Task<MailContent> ReadAsync(Stream eml, CancellationToken ct = default);
}
```
- `Html`: the raw HTML body, null when there is none. Only for D3, never displayed directly.
- `PlainText`: text body; when only HTML exists, the text from `MimeBodyText.Extract`.
- `InlineImages`: Content-ID without angle brackets → `data:` URI. Only `image/png`, `image/jpeg`, `image/gif`,
  `image/webp`; each at most 5 MB, together at most 25 MB; others are skipped. Never `image/svg+xml`.
- `Attachments`: non-inline parts and inline parts that are not in `InlineImages`. `FileName` is cleaned with D1 and is
  never used as a path. A missing name becomes empty.
- `To`, `Cc` and `From` are "Name <address>" or only the address, cleaned with D1.

### D3 – sanitizer and document (`MailWarden.Infrastructure.Mime`)
```csharp
public sealed record SanitizedHtml(string Html, int BlockedRemoteResourceCount);
public static class MailHtmlSanitizer
{
    public static SanitizedHtml Sanitize(string html, IReadOnlyDictionary<string, string> inlineImages, bool allowRemoteImages);
}
public sealed record ReadingDocument(string Html, int BlockedRemoteResourceCount);
public static class ReadingDocumentBuilder
{
    public static ReadingDocument Build(MailContent content, bool allowRemoteImages);
}
```
Sanitizer allow-list rules, on any input (never throws on malformed HTML; null → `ArgumentNullException`):
- Removed entirely: `script`, `style`, `iframe`, `frame`, `object`, `embed`, `applet`, `form`, `input`, `button`,
  `select`, `textarea`, `link`, `meta`, `base`, `svg`, `math`, `video`, `audio`, `source`, `template`.
- Attributes: every `on*` removed; `target`, `srcset`, `srcdoc`, `background`, `ping`, `formaction` removed.
- `a href`: only `http`, `https`, `mailto` stay; other schemes (`javascript:`, `data:`, `file:`, `vbscript:`, relative)
  lose the attribute; link text stays.
- `img src`:
  - `cid:x` with `x` in `inlineImages` → that data URI; unknown `cid:` → image removed.
  - `data:` only with the D2 image types; otherwise removed.
  - `http`/`https`: kept when `allowRemoteImages`, otherwise `src` removed and counted in `BlockedRemoteResourceCount`.
  - other schemes → removed.
- Inline `style`:
  - `url(...)` with `http`/`https` follows the same rule and count as remote images.
  - Other `url(...)`, `expression(...)`, `behavior`, `-moz-binding`, `position: fixed` are removed.

Document:
- A complete HTML5 document with `<meta charset="utf-8">`, a light fixed background and text colour, and this CSP meta
  tag, exactly: `default-src 'none'; img-src data:` + ` http: https:` only when `allowRemoteImages` +
  `; style-src 'unsafe-inline'; font-src data:; form-action 'none'; base-uri 'none'`.
- With `content.Html` non-null: its sanitized content. The count comes from the sanitizer.
- Without HTML: `PlainText` HTML-encoded in a `white-space: pre-wrap` block, count 0.

### D4 – WebView2 policy (`MailWarden.Presentation`), pure and testable
```csharp
public enum NavigationDecision { Allow = 1, Block = 2, OpenExternally = 3 }
public static class WebContentPolicy
{
    public const string DocumentUri = "https://mail.mailwarden.invalid/message.html";
    /// DocumentUri (exact) → Allow. Absolute http/https/mailto without user info → OpenExternally. Everything else
    /// (other schemes, relative, unparseable, http/https with user info such as https://bank.test@evil.test) → Block.
    public static NavigationDecision DecideNavigation(string uri);
    /// DocumentUri → true. http/https: only when isImageRequest && allowRemoteImages. Everything else false.
    public static bool AllowRequest(string uri, bool isImageRequest, bool allowRemoteImages);
}
```
WebView2 wiring in `App` (not unit-testable; checked by the security review and Dennis):
- The user data folder is `<data>\webview2`, and InPrivate is used if the WinUI control allows it (say so in the notes
  if not).
- Settings: `IsScriptEnabled = false`, `IsWebMessageEnabled = false`, `AreHostObjectsAllowed = false`,
  `AreDevToolsEnabled = false`, `AreDefaultContextMenusEnabled = false`, `IsPasswordAutosaveEnabled = false`,
  `IsGeneralAutofillEnabled = false`, `IsStatusBarEnabled = true` (hover shows the link target), no zoom persistence.
- All requests go through `WebResourceRequested` with filter `*`. The document comes from memory at `DocumentUri`
  (no `NavigateToString`, it has a 2 MB limit). What `AllowRequest` rejects gets a 403.
- `NavigationStarting` and `NewWindowRequested` use `DecideNavigation`. `OpenExternally` cancels the navigation and
  opens the system handler for that URI with `Windows.System.Launcher`. No download is ever accepted
  (`DownloadStarting` is cancelled).

### D5 – storage addition (`IMessageStore`)
```csharp
/// Number of messages without \Seen matching Role/AccountId/FolderId of the query; Limit and Offset are ignored.
Task<int> CountUnreadAsync(MessageQuery query, CancellationToken ct = default);
```
Same filter meaning as `ListAsync`. No schema change in AP-04a; AP-07c owns schema v5.

### D6 – runtime (`MailWarden.Infrastructure.Composition`)
```csharp
public sealed class DataFolderInUseException : Exception { }

public sealed class MailWardenRuntime : IAsyncDisposable
{
    /// Takes the data-folder lock BEFORE opening or migrating the database (review O2 of AP-03d), loads the auth
    /// configuration from <root>\auth-config.json + environment, creates one DpapiCredentialStore in <root>\credentials,
    /// one provider of each kind, and does not start the service. Failures after the lock release it again.
    /// Throws DataFolderInUseException when the lock is held; the database file is then untouched.
    public static Task<MailWardenRuntime> OpenAsync(DataPaths paths, CancellationToken ct = default);

    public DataPaths Paths { get; }
    public MailboxService Service { get; }
    public IMailboxReadModel ReadModel { get; }
    /// A new, initialised connection of its own; owned and disposed by the runtime.
    public Task<SqliteDatabase> OpenDatabaseAsync(CancellationToken ct = default);
    /// Stops the service, disposes all connections, releases the lock. Idempotent.
    public ValueTask DisposeAsync();
}
```
- Own connection per flow (review O1): one for the service's account list, one per account cycle, one per IDLE watcher,
  one for the read model.
- `MailboxService` gets an additional optional constructor parameter `DataFolderLock? heldLock = null`. When given, the
  service neither acquires nor releases the lock. The existing behaviour and its tests stay unchanged.
- `IMailboxReadModel` is declared in `Infrastructure.Composition` and serialises access to its one connection.
```csharp
public interface IMailboxReadModel
{
    Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MailFolder>> ListFoldersAsync(Guid accountId, CancellationToken ct = default);
    Task<IReadOnlyList<MailMessage>> ListMessagesAsync(MessageQuery query, CancellationToken ct = default);
    Task<int> CountUnreadAsync(MessageQuery query, CancellationToken ct = default);
    /// Null when the message has no EML (EmlPath null or file missing). The EML is read completely into memory, so no
    /// reader stays open while the sync overwrites the file (TODO AP-03/AP-04).
    Task<MailContent?> ReadContentAsync(MailMessage message, CancellationToken ct = default);
}
```

### D7 – critical log (`MailWarden.Infrastructure.Logging`)
```csharp
public interface ICriticalLog { void Write(string level, string source, string message); }
public sealed class CriticalFileLog : ICriticalLog
{
    public CriticalFileLog(string logDirectory, TimeProvider timeProvider);
}
```
- Appends one line to `<logDirectory>\mailwarden.log`: `ISO8601 | LEVEL | source | message`.
- CR and LF in any field become a space. It is safe across threads and never throws; a write failure is swallowed.
- Callers pass exception **type names** and fixed texts only: never `Exception.Message`, `ToString()`, or mail or
  account data.

### D8 – views (`MailWarden.Presentation`)
```csharp
public sealed record MailView(FolderRole? Role, Guid? AccountId, long? FolderId)
{
    public static MailView Group(FolderRole role);                      // over all accounts
    public static MailView GroupForAccount(FolderRole role, Guid accountId);
    public static MailView Folder(Guid accountId, long folderId);
    public MessageQuery ToQuery(int limit, int offset);
    /// Inbox, Sent, Outbox, Screener, Junk, Quarantine; in this order.
    public static IReadOnlyList<FolderRole> GroupRoles { get; }
}
public sealed record FolderTreeNode(string Name, MailFolder? Folder, IReadOnlyList<FolderTreeNode> Children);
public static class FolderTree
{
    /// Splits FullName at '/'. Missing parents are nodes with Folder null (not selectable). Names cleaned with D1.
    /// Siblings ordered with OrdinalIgnoreCase.
    public static IReadOnlyList<FolderTreeNode> Build(IEnumerable<MailFolder> folders);
}
```
Labels from `UiText`: Posteingang, Gesendet, Postausgang, Screener, Junk, Quarantäne.

### D9 – settings (`MailWarden.Presentation`)
```csharp
public enum ReadingPaneLayout { SideBySide = 1, ListAbove = 2 }
public enum AppTheme { System = 0, Light = 1, Dark = 2 }
public sealed record UiSettings(ReadingPaneLayout Layout, AppTheme Theme, double NavigationWidth, double ListSize)
{
    public static UiSettings Default { get; }   // SideBySide, System, 280, 420
}
public interface IUiSettingsStore { UiSettings Load(); void Save(UiSettings settings); }
public sealed class UiSettingsException : Exception { }
public sealed class UiSettingsFile : IUiSettingsStore
{
    public const string FileName = "ui-settings.json";
    public UiSettingsFile(string path);
}
```
- Keys `layout` (`"sideBySide"`, `"listAbove"`), `theme` (`"system"`, `"light"`, `"dark"`), `navigationWidth`, `listSize`
  (both numbers, 150–2000).
- A missing file gives defaults, and missing keys their defaults.
- Invalid JSON, an unknown key, a wrong type or a value out of range → `UiSettingsException` naming the file name and
  the key, without values.
- `Save` writes atomically (temp file + move) as UTF-8 without BOM.

### D10 – view models (`MailWarden.Presentation`)
```csharp
public interface IUiDispatcher { void Post(Action action); }
/// Implemented in the composition by a thin adapter over MailboxService.
public interface IMailboxController
{
    event EventHandler<AccountSyncStatus>? StatusChanged;
    IReadOnlyList<AccountSyncStatus> CurrentStatus { get; }
    void TriggerAccount(Guid accountId);
    void RetryAccount(Guid accountId);
}

public sealed class ShellViewModel : ObservableObject
{
    public ShellViewModel(IMailboxReadModel readModel, IMailboxController controller, IUiDispatcher dispatcher,
        IUiSettingsStore settings, ICriticalLog log, TimeProvider timeProvider);
    public NavigationViewModel Navigation { get; }
    public MessageListViewModel Messages { get; }
    public ReadingPaneViewModel Reading { get; }
    public ReadingPaneLayout Layout { get; set; }      // change raises PropertyChanged and saves
    public AppTheme Theme { get; set; }                // change raises PropertyChanged and saves
    public double NavigationWidth { get; set; }        // saves
    public double ListSize { get; set; }               // saves
    public string? Banner { get; }                     // fixed German text; null when nothing to report
    public ICommand ToggleLayoutCommand { get; }
    public ICommand RefreshCommand { get; }
    public Task InitializeAsync(CancellationToken ct = default);
}

public sealed class NavigationItem : ObservableObject
{
    public string Label { get; }
    public MailView? View { get; }                     // null: not selectable (account node without view, tree parent)
    public int UnreadCount { get; }                    // 0 when View is null
    public Guid? AccountId { get; }
    public string? StatusText { get; }                 // account nodes: fixed German text for the sync state
    public IReadOnlyList<NavigationItem> Children { get; }
}
public sealed class NavigationViewModel : ObservableObject
{
    public ObservableCollection<NavigationItem> Items { get; }
    public NavigationItem? SelectedItem { get; set; }  // selecting an item with View null is ignored
    public Task RefreshAsync(CancellationToken ct = default);
}

public sealed class MessageRowViewModel
{
    public MailMessage Message { get; }
    public string Sender { get; }      // cleaned FromName, else cleaned FromAddress
    public string Subject { get; }     // cleaned; empty → UiText "(Kein Betreff)"
    public string Preview { get; }     // cleaned TextPreview
    public string DateText { get; }    // local time: today "HH:mm", same year "dd.MM.", otherwise "dd.MM.yyyy"
    public string AccountLabel { get; } // cleaned account DisplayName, else email address
    public bool IsUnread { get; }      // no \Seen
    public bool IsFlagged { get; }
    public bool HasAttachments { get; }
}
public sealed class MessageListViewModel : ObservableObject
{
    public const int PageSize = 100;
    public ObservableCollection<MessageRowViewModel> Items { get; }
    public MessageRowViewModel? SelectedRow { get; set; }
    public bool HasMore { get; }
    public bool IsLoading { get; }
    public Task LoadAsync(MailView view, CancellationToken ct = default);
    public Task LoadMoreAsync(CancellationToken ct = default);
    public Task ReloadAsync(CancellationToken ct = default);
}

public enum ReadingState { Empty = 1, Loading = 2, Ready = 3, NotDownloaded = 4, Error = 5 }
public sealed class ReadingPaneViewModel : ObservableObject
{
    public ReadingState State { get; }
    public string Subject { get; }
    public string From { get; }
    public string To { get; }
    public string Cc { get; }
    public string DateText { get; }
    public IReadOnlyList<string> AttachmentNames { get; }
    public string? DocumentHtml { get; }               // output of ReadingDocumentBuilder
    public int BlockedRemoteResourceCount { get; }
    public bool CanLoadRemoteImages { get; }           // Ready and count > 0 and not yet allowed
    public ICommand LoadRemoteImagesCommand { get; }
    public Task ShowAsync(MailMessage? message, CancellationToken ct = default);
}
```
Behaviour:
- **Navigation items:** first the six group items (`MailView.Group`), each with its unread count over all accounts.
  Then one node per account (label: cleaned DisplayName, else address; `View` null; `StatusText` from its state). Its
  children are the six role views for that account (`GroupForAccount`) followed by the folder tree of folders whose
  role is none of the six (Drafts, Trash, Archive, ordinary folders). Default selection after initialising: the group
  Inbox.
- **List loading:** a selection change calls `LoadAsync`, and a selected row calls `Reading.ShowAsync`. Selecting no
  row or another view shows `Empty`.
- **Newest wins:** an older pending `LoadAsync` or `ShowAsync` result must never overwrite a newer one (stale result
  guard).
- **Paging:** `PageSize` per page, newest first; `HasMore` is true when the last page was full. Offset comes from the
  loaded count.
- **Reload:** loads as many rows as are currently shown (at least one page), keeps the selection by message id when the
  row still exists and otherwise clears it.
- **Reading:** a remote-image approval holds only for the current message and resets on the next one.
  `LoadRemoteImagesCommand` rebuilds `DocumentHtml` with `allowRemoteImages: true` without reading the EML again and
  sets `CanLoadRemoteImages` false.
- **Reading errors:** null content → `NotDownloaded`. Any exception except cancellation → `Error`, a fixed text and a
  critical log line with the type name.
- **Status:**
  - `StatusChanged` arrives on a thread-pool thread and is marshalled with `IUiDispatcher.Post`.
  - Status of unknown accounts is ignored.
  - A progress-only update (`Progress` non-null) changes `StatusText` only and does not reload.
  - A transition to `Idle` reloads navigation and list; several transitions while one reload runs coalesce into one
    more.
  - A handler exception is written to the critical log with its type name (G3 of AP-03d) and never propagates.
- **Refresh:** per account `RetryAccount` in `NeedsSignIn` or `WaitingAfterError`, otherwise `TriggerAccount`.
- **Settings:** a `UiSettingsException` from `Load` → defaults plus `Banner` text. A failed `Save` → `Banner` text and a
  critical log line. Neither stops the app.
- **Empty accounts:** navigation shows the group items; `Banner` tells that accounts are added with the AuthProbe until
  AP-04b.

### D11 – app (XAML, code-behind only wires)
- `App.OnLaunched`:
  1. Resolve `MAILWARDEN_DATA_DIR` or `<exe>\data`. Failures here and from `OpenAsync` (invalid path, data folder in
     use, schema newer than the build) show a simple error window with a fixed German text instead of ending silently,
     and write a critical log line when a log folder is available.
  2. Open the runtime, then create the shell, then start the service.
  3. Closing the window disposes the runtime.
- `Application.UnhandledException` writes a critical log line with the type name.
- Main window: a three-part layout with resizable parts, using a small splitter written here (no toolkit package).
  - Navigation is always on the left (tree).
  - `Layout` switches list and reading pane between columns and rows without recreating the WebView2.
  - A toolbar has the layout toggle, theme choice and refresh; an info bar shows `Banner`.
- Unread rows are bold; each row shows sender, subject, date, preview, account label and icons for attachment and flag.
- The list loads more when scrolled near the end.
- Reading header with subject, from, to, cc, date, attachment names and a "Bilder laden" button with the blocked count;
  below it the WebView2 per D4. `NotDownloaded`, `Error` and `Empty` show text instead of the WebView2.
- Theme: `RequestedTheme` on the root element.

## Expected behaviour
Test cases 1–41 use fakes for the read model, controller, dispatcher and settings store; synthetic HTML and EML under
`tests\fixtures\` with artificial addresses (`sender_a@example.test`). Case 42 and later are manual, by Dennis.

| # | Case | Expected |
|---|---|---|
| 1 | `Clean` with RTLO, LRI, plain letters, emoji, CR/LF/TAB, BEL | bidi and BEL gone, CR/LF/TAB → space, letters and emoji unchanged; null → ArgumentNullException |
| 2 | Read multipart/alternative EML | Html and PlainText set, Date, From, To, Cc filled |
| 3 | Read text-only EML | Html null, PlainText set |
| 4 | Read html-only EML | PlainText from the HTML text |
| 5 | Inline PNG with Content-ID | in InlineImages as data URI, not in Attachments |
| 6 | Inline SVG; image over 5 MB; images together over 25 MB | not in InlineImages; SVG and oversized listed in Attachments |
| 7 | Attachment with path-like and bidi file name | FileName cleaned, content not interpreted |
| 8 | Unparsable stream | MailContentException without message content |
| 9 | Sanitize each element from the "removed entirely" list | element gone, also nested and in mixed case |
| 10 | `onclick`, `onerror`, `target`, `srcset`, `background`, `formaction` | attributes gone |
| 11 | Links: http, https, mailto vs javascript, JaVaScRiPt with whitespace, data, file, relative | first three kept, others lose href and keep the text |
| 12 | `img` with cid known / unknown, data png, data svg, data html, https, file | as D3, count only for https |
| 13 | `allowRemoteImages` true vs false with 3 remote images | kept and count 0 vs removed and count 3 |
| 14 | Inline style with remote `url()`, `expression()`, `behavior`, `position: fixed`, harmless colour | per D3; colour stays |
| 15 | Malformed or truncated HTML, empty string, very deep nesting (5,000 levels) | no exception |
| 16 | Build with HTML, images blocked and allowed | CSP meta exactly as D3 in both variants; sanitized content in the body |
| 17 | Build with plain text containing `<script>` | encoded, not an element |
| 18 | `DecideNavigation`: document, http, https, mailto, https with user info, javascript, data, file, about:blank, relative, garbage | per D4 |
| 19 | `AllowRequest`: document; image with and without allow; non-image with allow; file; data | per D4 |
| 20 | `CountUnreadAsync`: role over accounts, account, folder; limit ignored | correct counts, seen messages not counted |
| 21 | `MailView` factories and `ToQuery` | fields as named, limit and offset passed through |
| 22 | `FolderTree.Build` with `A/B/C`, `A`, `Z`, a missing parent, bidi in a name | tree, parent node with Folder null, cleaned names, sibling order |
| 23 | UiSettings: missing file, partial file, each invalid kind, round trip, no BOM | per D9; exception names the key and has no value |
| 24 | Runtime lock: second `OpenAsync` on the same root | DataFolderInUseException; the database file is unchanged (size and time) |
| 25 | Runtime open and dispose, then open again | works; lock file gone after dispose; dispose twice is fine |
| 26 | Service with a given `heldLock` | starts without taking the lock; stop leaves it held |
| 27 | Two runtime connections write 200 messages each in parallel | no exception, all rows present (O1) |
| 28 | Critical log: fields with CR/LF; two threads; missing or unwritable directory | one line per entry in the format; no exception |
| 29 | Navigation with 2 accounts | six groups, two account nodes with role views and remaining folders, no role folder twice; default Inbox |
| 30 | Selecting a tree parent (View null) | selection unchanged |
| 31 | Rows: name vs address, empty subject, unread, flagged, attachment, date texts today, this year, older (fake clock) | per D10 |
| 32 | Paging: 250 messages | 100, then 200, then 250 rows; HasMore false at the end; no duplicates |
| 33 | Reload keeps or clears the selection | per D10 |
| 34 | Slow first `LoadAsync` finishes after a second one | second result stays |
| 35 | Slow first `ShowAsync` finishes after a second one | second message stays |
| 36 | Reading states: no selection, EML missing, reader throws (marker text) | Empty, NotDownloaded, Error; marker never in any text or log |
| 37 | Blocked images then `LoadRemoteImagesCommand`; then the next message | document rebuilt without a second read; approval reset |
| 38 | Status: progress only, unknown account, Running → Idle, burst of Idle | no reload, ignored, reload, coalesced |
| 39 | Refresh with one account each in Idle, NeedsSignIn, WaitingAfterError | Trigger, Retry, Retry |
| 40 | Layout and theme change; save fails; load fails | property change and save; banner + log; defaults + banner |
| 41 | No read model or controller call changes server state: the fake controller records only Trigger and Retry; the read model has no write member | reflection and recorded calls confirm it |
| 42 | Manual: start with `MAILWARDEN_DATA_DIR` on a **copy** of probe-data | window opens, three accounts, counts plausible, no crash; closing ends the process |
| 43 | Manual: layout toggle and sizes | both layouts work; choice and sizes kept after restart; navigation stays left |
| 44 | Manual: light, dark and system theme | shell changes; mail stays readable on a light background |
| 45 | Manual: newsletter with remote images | blocked, count shown; "Bilder laden" shows them for this mail only; hovering a link shows its target; a click opens the default browser |
| 46 | Manual: new mail arrives while running | appears in the Inbox within seconds, unread count rises |
| 47 | Manual: second app instance; invalid `MAILWARDEN_DATA_DIR` | error window with text, no silent exit |
| 48 | Manual: after reading mails, the same mails are still unread in webmail | nothing changed on the server |

## Done when
- Build `-warnaserror` green; S3 green (new project, `I*.cs`, csproj).
- Review APPROVE and security review without Critical or High (triggers: HTML cleaning, WebView2 configuration, paths
  and names from foreign data, logging).
- Documenter: `README.md` (first use of the app, data folder, layout), PLAN rows AP-04a, `work\manual-tests.md` part for
  cases 42–48, TODO deferred items.
- Dennis has checked the manual cases 42–48.
