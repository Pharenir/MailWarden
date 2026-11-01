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

## Decisions author.0 (planner, 2026-11-01, questions of the tester)
- `MailContentReader`: only an input that cannot be parsed as a message (at least the empty stream) gives
  `MailContentException`. An `IOException` from the stream propagates unchanged; the reading pane turns it into `Error`.
- `UiSettingsFile(string path)`: `path` is the path of the file, not of its folder.
- `SelectedItem` and `SelectedRow` setters start the load without the caller being able to await it. `LoadAsync`,
  `ShowAsync`, `ReloadAsync`, `LoadMoreAsync`, `InitializeAsync` can be awaited directly.
- Cancellation in `ShowAsync`: the `OperationCanceledException` of the caller's token propagates. The state does not
  become `Error` and nothing is logged.
- Sanitizer: with `allowRemoteImages`, `background-image: url(https://...)` stays in an inline `style`. Configure the
  allowed CSS properties accordingly.
- Very deep nesting (case 15, 5,000 levels) must not end the process: no stack overflow in parsing, sanitizing or
  serializing. If the library cannot guarantee it, the sanitizer limits the depth before parsing and returns a safe
  result; state which in the notes.
- The `HtmlSanitizer` reference has no `PrivateAssets`, so its AngleSharp dependency flows to the test projects.
- Tests check only that `StatusText` is non-empty, differs by state and contains no exception text, and that the
  empty-accounts banner contains "AuthProbe". The exact German wording stays free.

## Decisions fix.2 (planner, 2026-11-01, after verify.1 PASS 1349/1349, review.1 BLOCK, secure.1 1 High)
Binding; they change D1, D3, D4, D9, D10. Planner checks: the queue of the probe-data copy is empty (review P1), so a first
start executes nothing on the server.

### Must fix
- **K1 (review B1).** The info bar must close again when `Banner` becomes null. `x:Bind` function bindings are not called
  for null (`MainWindow.g.cs:1644-1651`), so set `IsOpen` and `Message` in code-behind from `PropertyChanged`, once also in
  the constructor because a settings load error sets `Banner` before that.
- **K2 (secure H1).** A mail with about 1,000 nested parts overflows the stack: the process ends without any catch, and
  again after each start. Use `MimeParserDefaults.Create()` (`Infrastructure\Mime`, already on this branch) at **every**
  parse in this repo: `SyncEngine` and `MailContentReader`, with `MimeMessage.LoadAsync(options, stream, persistent, ct)`.
  Search for other `MimeMessage.Load` and `MimeParser` uses.
- **K3 (review S1).** A replaced selected row must not clear the reading pane or the image approval: compare by message
  id, not by object (`MainWindow.xaml.cs:229-235`).
- **K4 (review S2, S3).** Closing the window:
  - `ShellViewModel.Dispose` cancels running loads and ends the reload loop.
  - The read model closes through its gate before the runtime disposes the connections.
  - `OnLaunched` checks `isShuttingDown` before `Service.StartAsync` and in its `catch`.
  - `ReleaseAfterFailedStartAsync` calls `CloseReadingView` and `Close` inside its `try`.
  - `MailWebViewHost` has an `isClosed` flag, so a late `ShowAsync` causes no critical log line.
- **K5 (secure M1).** `MailHtmlSanitizer` keeps a budget per document for inserted inline images: 34,000,000 characters of
  data URI in total. Over the budget the image is removed. `IsAllowedDataImage` runs once per inline image. An
  `OutOfMemoryException` in the reading pane ends in `Error`, never in `Loading`.
- **K6 (secure M2).** `HtmlNestingLimiter` also limits run time: a start tag with more than 256 attributes is dropped, a
  `style` value over 64 KB is dropped (`data:` in `src` excepted), and tag text has a total budget. Target: the worst inputs
  of the security probes (100,000 attributes or `style` declarations) finish in under 2 s; measure again and state the
  numbers. `LoadRemoteImagesCommand` builds off the UI thread with the same generation guard as `ShowAsync`.
- **K7 (secure M3).**
  - New in `SafeDisplayText`: `public static string Shorten(string text, int maxCharacters)` cleans, collapses runs of
    whitespace to one space, trims, cuts without splitting a surrogate pair and appends "…" when cut.
  - View models use it: sender 100, subject 500, To and Cc 2,000, preview 200.
  - At most 50 attachment names, then one entry "und N weitere" from `UiText`.
  - XAML: `MaxLines` on subject and from lines, the header area has a `MaxHeight` with a `ScrollViewer`, so the WebView2
    stays reachable.

### Should fix
- **K8 (review S4).** Theme selection without `DisplayMemberPath` on a private record: `ComboBoxItem` with `Content` and
  `Tag`, or an `ItemTemplate` with `x:Bind` on a public type.
- **K9 (review S5, P4, changes D9).** Each layout keeps its own list size. `UiSettings(Layout, Theme, NavigationWidth,
  ListWidth, ListHeight)`, defaults `SideBySide`, `System`, 280, 420, 320; keys `listWidth` and `listHeight` replace
  `listSize` (same range 150–2000; `listSize` is now an unknown key). `ShellViewModel.ListWidth` and `ListHeight` replace
  `ListSize`. When applying, the code-behind limits the list so that the reading pane keeps at least 200 px.
- **K10 (builder question 1, changes D3).** `form` loses only the tag and its content stays; `input`, `button`, `select`,
  `textarea` stay removed. Otherwise a mail wrapped in a form appears empty.
- **K11 (builder question 2, secure N1, changes D4).** New overload
  `DecideNavigation(string uri, bool isUserInitiated)`: when not initiated by the user, `OpenExternally` becomes `Block`;
  `Allow` and `Block` are unchanged. `OnNavigationStarting` and `OnNewWindowRequested` use it. Case 45 confirms that a real
  click reports `true`.
- **K12 (secure N2).** New `WebContentPolicy.SanitizeMailto(string uri)`: keeps only the parameters `to`, `cc`, `bcc`,
  `subject`, `body`; names compared case-insensitively. `Launcher` gets the result.
- **K13 (secure N3).** `AllowRequest` also rejects `localhost`, loopback and private or link-local IP literals (IPv4 and
  IPv6), even with approval. The document gets `<meta name="referrer" content="no-referrer">`; the CSP text stays exactly
  the same. Changing the message calls `CookieManager.DeleteAllCookies()` on the WebView2.
- **K14 (secure N4, changes D1).** `Clean` also turns U+2028 and U+2029 into a space and removes U+00AD, U+200B,
  U+2060–U+2064, U+206A–U+206F, U+FEFF and U+E0000–U+E007F. U+200C and U+200D stay (scripts and emoji sequences). Write the
  characters as `\u` escapes in the source, not as invisible literals (review S7).
- **K15 (review P2).** A transition to `WaitingAfterError` or `NeedsSignIn` also reloads, so stored results of an
  interrupted pass become visible. Coalescing as before.
- **K16 (review S6).** Remove the duplicates:
  - `AtomicFile` in `Infrastructure.Storage` (temp file + move, delete of the temp file best effort), used by
    `UiSettingsFile`; `FileEmlStore` and `DpapiCredentialStore` stay for now.
  - One subject rule; one status dictionary; one public constant for the log level in `Infrastructure.Logging`.

NITs of review.1: builder's choice, except `HorizontalContentAlignment=Stretch` for list items and the `heldLock` note in
the `StartAsync` summary. Builder question 3 (image with `attachment` and Content-ID): stays as it is.

### Additional test cases (fix.2)
| # | Case | Expected |
|---|---|---|
| 49 | Generated EML with 1,023 nested `multipart/mixed`, and with nested `message/rfc822`: `MailContentReader` and a sync pass with `FakeMailServer` | no process end, the message is stored and readable |
| 50 | `MimeParserDefaults.Create()` | depth 32; a new instance each call |
| 51 | One 1 MB inline image referenced 300 times | document at most about 34,000,000 characters of data URI; references beyond the budget removed; no exception |
| 52 | One start tag with 100,000 attributes; 100,000 `style` declarations; positive control: a tag with 10 attributes | finishes within the test timeout; result safe; the control keeps its allowed attributes |
| 53 | `Shorten`: whitespace runs, exactly at the limit, one over, surrogate at the cut, null | per K7 |
| 54 | Row and reading pane with a 365 KB subject, a sender name with many spaces, 100,000 attachments | limits per K7; "und N weitere" |
| 55 | UiSettings with `listWidth`, `listHeight`; old key `listSize`; range; round trip; `ShellViewModel` saves each | per K9; `listSize` → exception naming the key |
| 56 | `form` with text and an input | form tag and input gone, text stays |
| 57 | `DecideNavigation(uri, isUserInitiated)` for the document, http, mailto, javascript with true and false | per K11 |
| 58 | `SanitizeMailto`: `attach`, `ATTACH`, `to`, `subject` with encoding, no parameters, not a mailto | per K12 |
| 59 | `AllowRequest` with approval: `localhost`, `127.0.0.1`, `10.x`, `172.16.x`, `192.168.x`, `169.254.x`, `[::1]`, `[fe80::1]`; positive control: public host | false; true |
| 60 | Document contains the referrer meta; CSP exact as before | per K13 |
| 61 | `Clean` with U+2028, U+00AD, U+200B, U+FEFF, tag characters; emoji with U+200D stays | per K14 |
| 62 | Status transition to `WaitingAfterError` and `NeedsSignIn` | one reload each, coalesced |
| 63 | `Dispose` during a slow reload | no further read-model call after the running one, no critical log line |
| 64 | `LoadRemoteImagesCommand` on a slow rebuild, then the next message | the old result does not overwrite the new message |
| 65 | `OutOfMemoryException` from the reader | state `Error`, log line with the type name |

## Decisions fix.3 (planner, 2026-11-01, after verify.2 PASS 1488/1488, review.2 BLOCK, secure.2 0/0/3/3)
Binding; they change D2, D3, D10, K6, K7, K12, K13. Two independent reviews show that budgets alone do not hold: the
pre-scan and the parser disagree about raw text. So the root cause is removed (L1), and a backstop covers what a scan cannot
foresee (L3). Accepted as they are: bodies below depth 32 stay opaque (with a notice, L5); inline images over about 24.3 MB
lose the last image; a public name resolving to a private address after "Bilder laden".

### Must fix
- **L1 (review B2, secure M1).** The limiter removes raw-text elements so that the parser never enters a raw-text state:
  `script`, `style`, `title`, `textarea`, `iframe`, `noembed`, `noframes`, `noscript`, `svg`, `math` with their content up to
  the first matching end tag (without an end tag: the rest of the input, as the parser would). `xmp`, `plaintext`, `listing`:
  only the tag. In held attribute values **every** `<` counts. A comment with more than 20,000 `<` inside is not skipped but
  scanned normally. The D3 list of removed elements does not change; this only happens earlier.
- **L2 (secure M2, 154 KB mail → over 5 GB).** The limiter keeps a per-document counter: start tags of `a`, `b`, `big`,
  `code`, `em`, `font`, `i`, `nobr`, `s`, `small`, `strike`, `strong`, `tt`, `u` count up, only their own end tag counts down.
  Above 64 the start tag is dropped. The open-tag estimate is not used for this.
- **L3 (backstop).** `ReadingPaneViewModel` builds the document with at most one running build at a time and a time limit of
  5 s (constant, injectable through `TimeProvider`-based waiting so tests need no real wait). On timeout, or while another
  build is still running, it shows the document built from `PlainText` with the notice from L4. A later result of a timed-out
  build is ignored. Selecting the same mail again does not start a second build while one runs.
- **L4 (review P6).** `SanitizedHtml` and `ReadingDocument` get a last member `bool WasReduced`: true when the limiter dropped
  anything or the image budget was hit. `ReadingPaneViewModel` gets `string? ContentNotice`, a fixed German text from
  `UiText`, and the XAML shows it above the document. Without a notice it is null.
- **L5 (secure N1).** `MailContent` gets a last member `bool HasOpaqueParts`: true when a `MimePart` of type `multipart/*` or
  `message/*` could not be parsed further (depth limit). It also sets `ContentNotice`. Such a part is not listed as an
  unnamed attachment.
- **L6 (secure M3).** `MailDisplayText.SenderLine` shows only the first mailbox of the From header, then "+N weitere" from
  `UiText`, working on the parsed mailboxes instead of searching in joined text (add what `MailContent` needs). A long address
  is cut in the middle so that the end with the domain stays visible (`aaa…@evil.test`). The name stays cut at 100.

### Should fix
- **L7 (review S8).** `DeleteAllCookies` in `try`/`catch` (type name to the log). In `OnMainWindowClosed`, a failure of
  `CloseReadingView` must not skip `runtime.DisposeAsync()`.
- **L8 (secure N2, changes K13).** `AllowRequest` also rejects hosts without a dot, the endings `.local`, `.lan`, `.internal`,
  `.home.arpa`, `.localdomain`, the host `fritz.box`, and the ranges `100.64.0.0/10`, `198.18.0.0/15`, `192.0.0.0/24`,
  `224.0.0.0/3`, `ff00::/8`, `::/96`, `::ffff:0:0/96`, `64:ff9b::/96`, `2002::/16`.
- **L9 (secure N3).** `SanitizeMailto` cuts the address part at the first `&`, and a raw `?` inside a value becomes `%3F`.
- **L10 (NITs).** `MaxHeight` of the reading header 160 (below the 200 px minimum of the reading pane); the misplaced comment
  in `MailHtmlSanitizer.cs:73-76`. Case 45 also clicks a `mailto:` link.
- **L11 (probe, planner runs it on a copy).** `_scratch\limiter-probe <dataRoot>`, read-only: for all stored mails with HTML it
  builds the reading document and prints only counts: mails, with `WasReduced`, with `HasOpaqueParts`, longest and p95 build
  time, largest HTML size. No subjects, addresses, domains or folder names. It answers review P5.

### Additional test cases (fix.3)
| # | Case | Expected |
|---|---|---|
| 66 | `<script><!--</script>`, `<style><!--</style>`, `<title><!--</title>`, `<textarea><!--</textarea>`, each + 100,000 `<div>` + `-->` | done within the test timeout, no dangerous element, `WasReduced` true |
| 67 | `<script><a title="</script><div` + 100,000 attributes; `<script><img src="data:</script>` + 100,000 `<div>` + `">`; `<style><a x="</style>` + `x</a>` × 380,000; + `<1` × 900,000; + `<!---->` × 280,000 | done within the test timeout, no dangerous element |
| 68 | `<p>` + 1,000 × `<b aI=1>` + 18,000 × `</p><p>x` | done within the test timeout without a large memory rise (GC allocated bytes per thread as a guard) |
| 69 | Positive controls: 500 balanced `<b>x</b>`; comment with `<` inside a normal mail; `alt="<b>Logo</b>"`; `<!--[if !mso]><!-- -->` | unchanged, `WasReduced` false |
| 70 | `script` or `svg` without an end tag, then text | nothing after it survives as markup; no exception |
| 71 | `xmp`, `plaintext`, `listing` around text | tag gone, no element; no exception |
| 72 | A slow build (fake via an internal seam, or a large document with a very short limit): timeout; selecting again during a run; a late result | plain-text document + notice; no second build; the late result is ignored |
| 73 | `WasReduced`: over the budget, image budget hit, normal mail | true, true, false; `ContentNotice` set or null |
| 74 | Body at depth 40 below harmless text | `HasOpaqueParts` true, notice set, not listed as an attachment; depth 5 false |
| 75 | `SenderLine`: long name + address; two mailboxes; long name then address only; name containing `<a@b>` with a long real address; address over 100 characters without a name | first mailbox + "+1 weitere"; the real domain always at the end of the address part; never the second mailbox's address as the sender |
| 76 | `AllowRequest` with approval: `router`, `fritz.box`, `nas.local`, `x.home.arpa`, `100.64.0.1`, `224.0.0.1`, `[ff02::1]`, `[::127.0.0.1]`, `[2002:c0a8:101::]`; positive control: `images.example.test`, `8.8.8.8` | false; true |
| 77 | `SanitizeMailto`: `mailto:a@example.test&attach=c:/y`; `mailto:a@example.test?subject=x?attach=c:/y` | no `attach` parameter left; `?` inside the value encoded |

## Decisions fix.4 (planner, 2026-11-01, last round in the budget; after verify.3 1618/1623, review.3 BLOCK, secure.3 0/0/2/2)
Binding; they change L1–L6 and D3. Three rounds show that predicting the parser from outside keeps finding new gaps
(`<!--!>`, end tags the parser ignores). So the limit moves **into the parser**, where nothing has to be predicted (M1). The
pre-scan stays for what happens inside a single token. Real data (`limiter-probe`, 2,664 mails): none reduced, longest build
882 ms, largest HTML 313,586 characters, so the budgets below leave plenty of room.

### Must fix
- **M1 (secure M2, also the backstop for secure M1).** Hard budget during parsing. `MailHtmlSanitizer` parses with its own
  AngleSharp parser and sanitizes the resulting document; a callback counts every created element (`HtmlParserOptions.OnCreated`
  or the equivalent of the installed version) and aborts above **100,000 elements** or after **3 s** (`Stopwatch`). On abort
  the result is the sanitized text without markup: `SanitizedHtml` holds the encoded plain text and `WasReduced` is true. First
  check with the probes that the callback also fires for reconstructed formatting elements (`fmtObject`, `fmtTable`,
  `formatBomb`). If it does not, or the API does not exist: stop and report with the evidence; do not invent a replacement.
- **M2 (secure M1).** Comment end: search `--!>` from `start + 4`, `-->` from `start + 2`, as the parser does. A tag is dropped
  when one attribute value is over 64 KB and contains `<`, also for `data:`.
- **M3 (review B3).** Same mail during its own build: keep waiting for the same task, no second build, its own time limit, and
  no text version unless that limit really passed. A running rebuild with a different `allowRemoteImages` counts as another
  mail. Text version and notice appear only after a real timeout.
- **M4 (verify.3).** Corrections in the product:
  - An opaque `message/rfc822` part below the depth limit is not listed as an attachment.
  - `SenderLine`: the name keeps up to 100 characters, the address up to 60 next to it (a name of exactly 100 stays unchanged).
- **M5 (review S9, secure N1, changes L1).**
  - `svg` and `math`, self-closing or without an end tag: only the tag goes, the content is scanned normally.
  - `noscript`, `textarea`, `iframe`, `noembed`, `noframes` without an end tag: the rest goes as before, and `WasReduced` is true.
  - With an end tag: unchanged and no notice. `style`, `script`, `title` never cause a notice.

### Should fix
- **M6 (secure N2).** A `Multipart` without child parts but with non-empty content (no or unusable boundary) counts as opaque
  (`HasOpaqueParts`).
- **M7 (review NIT).** `HasOpaqueParts` only for `multipart/*`, `message/rfc822` and `message/partial`. Other `message/*` parts
  are normal attachments.
- **M8 (review NIT).** `a` and `nobr` do not count for the formatting limit of L2: the parser closes an open one itself.

### Decided by the planner, no product change
- The plain-text document keeps the strict CSP even after "Bilder laden" (`img-src data:`): text has no images. This is a test
  change (D3 note).
- **Stop rule for this AP.** After this round, findings of Medium or below in the pre-scan go to `TODO.md` as long as the parser
  budget of M1 holds and a hostile mail costs at most its budget. Anything High or a breach of M1 goes to Dennis, because the
  round budget is used up.

### Additional test cases (fix.4)
| # | Case | Expected |
|---|---|---|
| 78 | `<!--!><a x="-->` + `<div` with 100,000 attributes + `">`; same with `<!---!>`; same with `<a src="data:-->` | done within the test timeout, no dangerous element |
| 79 | `<div>` + 1,000 × `<span><b aI=1><object></b></object></span>` + `</div>` + 16,000 × `<p>x</p>`; same with `<table><tr><td>` instead of `<object>` | done within the test timeout, allocation guard as in case 68, `WasReduced` true |
| 80 | Over 100,000 elements through plain nesting-free markup | text stays without markup, `WasReduced` true, no dangerous element |
| 81 | Positive controls: `<!---->`, `<!-->`, `<!----!>` stay comments; 500 balanced `<b>x</b>`; 2,000 `<a>` links; 20,000 ordinary tags; base64 `src` over 64 KB | unchanged, `WasReduced` false |
| 82 | Same mail selected again during its build (blocked builder, clock not advanced), then the build ends | the HTML document, no notice, one build |
| 83 | Same mail selected again, clock past the limit | text version with notice |
| 84 | Rebuild with images running, same mail selected | no document with the wrong approval |
| 85 | `<p>A</p><svg/><p>B</p>`, `<p>A</p><math/><p>B</p>`, `<p>A</p><svg><p>B</p>`; `<svg><title>x</title></svg>` with end tag | B stays; no notice |
| 86 | `<p>A</p><noscript>B`, `<textarea>B`: without an end tag; with an end tag | A stays, `WasReduced` true; `WasReduced` false |
| 87 | `multipart/mixed` without a boundary and with text; with a boundary that never occurs | `HasOpaqueParts` true, notice set |
| 88 | `message/feedback-report` part; opaque `message/rfc822` at depth 40 | listed as an attachment, no notice; not listed, notice |
| 89 | `SenderLine` with a name of exactly 100, 101 characters | unchanged and address visible; cut and address visible |
| 90 | 200 unclosed `<a name=x>` followed by a link | the link stays |

### Revision of M1 (planner, 2026-11-01, after the builder's probe)
The probe of the builder shows that `OnCreated` does not fire for reconstructed formatting elements (`formatBomb` 64 x 100: 168
callbacks, 6,568 elements), so M1 as written does not hold. A failure of the callback was the case in which the builder had to
stop, and it did. Replacement, same idea, measured per token:
- **M1a.** First a probe: does `HtmlParserOptions.OnToken` fire for every token during tree building, and does an exception from
  it end the parser cleanly? Only if both hold, the budget is: allocated bytes of the parsing thread
  (`GC.GetAllocatedBytesForCurrentThread`) at most **256 MB** or at most **3 s**, checked per token. On abort the result is as in
  M1: encoded plain text, `WasReduced` true.
- **M1b.** If it does not hold: M2-M8 are implemented without a parser budget, secure M2 stays open, and Dennis decides about the
  remaining risk.
- M2-M8 are independent and are implemented either way.
- **Case 80** changes: over the pre-scan limits the text stays and `WasReduced` is true; "without markup" applies only to an abort
  of the parser budget. Cases 78 and 79 expect the abort or the pre-scan limit, whichever comes first.
- **M7** changes: `message/partial` is a normal attachment; opaque is only a part the parser left unparsed at the depth limit
  (`multipart/*`, `message/rfc822`) and M6.
