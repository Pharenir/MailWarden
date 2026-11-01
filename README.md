# MailWarden

Windows mail client for personal use: several accounts (Outlook.com / Gmail / any IMAP) in one unified inbox, with a
transparent four-level spam classifier and user-defined rules. All move/delete actions are executed on the server via
IMAP, so other devices see the same state.

MailWarden is developed and used by a single person and is not offered as a service.
Privacy policy: `PRIVACY.md` · Terms of service: `TERMS.md`.

Status: early development (see `PLAN.md`). Implemented so far: solution skeleton, local storage layer (SQLite
database and EML file store), the core of the spam classifier (a library in `MailWarden.Core`, calibrated against real
message headers and with a settings file loader; not yet connected to any mail), account and sign-in library (OAuth for
Outlook.com and Gmail, encrypted token storage, provider profiles with a connection check), the one-way sync from the
server to the local store (folders, messages, flags, deletions; read-only towards the server) and a WinUI 3 user interface with
actions and search (see "Using the app"). The sync itself never changes anything on the server. It fetches missing messages in batches (50 by default), reports progress per folder (number, role,
processed/missing; no names), reconnects within a pass if the connection was lost (at most 3 times) and does not sync
the Gmail folder "Important". A command queue (local to server: flags, move, trash, delete, empty folder; see
"Command queue") and a background service with schedule and IDLE (see "Background service") are implemented as
libraries; the app starts the background service. The app works on mail through the command queue; accounts are
still added with the AuthProbe tool, and attachments cannot be opened or saved yet (AP-04c). The spam filter runs after every sync in the app, but by
default it only records its decisions and moves no mail (see "Spam filter").

## Using the app
The app shows the mail of all accounts and keeps it current through the background service. You can mark mail as
read or unread, flag it, delete it, move it, empty Trash and Junk and search. Every action changes the server through
the command queue (see "Command queue"). Opening a message does not mark it as read unless you switch that on.

- **Accounts:** they cannot be added in the app yet. Add them with the AuthProbe tool (see `docs\auth-setup.md`).
  Without accounts the window shows a notice.
- **Navigation (left, always):** six group views over all accounts (Posteingang, Gesendet, Postausgang, Screener,
  Junk, Quarantäne) with the number of unread messages. Below, each account has the same six views and its other
  folders as a tree. Until the classifier is connected, Screener and Quarantäne stay empty.
- **Message list:** newest first, with sender, subject, date, a preview and the account. Unread messages are bold;
  icons mark attachments and flags. It loads 100 messages at a time and more when you scroll to the end. New mail
  appears after the next sync without any action. "Aktualisieren" starts a sync at once and retries accounts that
  wait after an error.
- **Selecting:** click a message; Ctrl and Shift select several. The actions apply to all selected messages. With
  none or several selected, the reading pane stays empty.
- **Reading pane:** subject, Von, An, Cc, Datum and the names of the attachments, then the message. Attachments can
  only be listed, not opened or saved yet. A message that is not downloaded yet says so.
- **Actions (toolbar and right-click menu):** "Gelesen", "Ungelesen", "Kennzeichnen" (toggles the flag), "Löschen",
  "Verschieben" and, in Trash and Junk only, "Ordner leeren". Keys in the message list: Ctrl+Q read, Ctrl+U unread,
  Del delete, Shift+Del delete permanently. The keys do nothing while the search box or a dialog has the focus, and
  holding a key acts only once. Messages being moved or deleted disappear from the list at once; read and flag state
  change at once. After a delete the next message is selected. If the server does not accept a command, the
  info bar names it and "Fehlgeschlagene Aktionen" lists it; "Verwerfen" removes the entry and the messages come back.
- **Delete:** Del moves messages to the Trash. In the Trash it asks first and deletes permanently. Shift+Del always
  asks ("Bitte bestätigen", button "Endgültig löschen") and names only the number of messages (and of accounts, if
  several), never a sender or subject. If a server only moved a permanently deleted message to the Trash, the info
  bar says so; delete it there once more.
- **Move:** "Verschieben" offers the folders of the account. Messages of several accounts cannot be moved together,
  and a folder of another account is refused.
- **Empty folder:** "Ordner leeren" works in Trash and Junk only. The confirmation names the account, the folder and
  the number of messages. Only the messages you were shown are deleted. If a message arrived since (or the folder
  changed on the server), nothing is deleted, you get a notice and are asked again with the new number. It is also
  refused while messages are still being moved out of that folder.
- **Search:** the box "Suchen" searches all local mail (at most 500 hits), shown in the list like a view with the
  account. Empty text returns to the previous view. Search stays on this computer.
- **Settings:** the button "Einstellungen" has the switch "Beim Anzeigen als gelesen markieren" (off by default). When
  it is on, a message shown for at least 1.5 seconds is marked as read; leaving earlier does not.
- **Window:** size, position and maximised state are stored and restored at the next start. A stored position that
  is no longer on any monitor is ignored.
- **Layout:** "Ansicht wechseln" puts the list and the reading pane side by side or the list above the reading
  pane. The parts can be resized with the splitters. Layout, sizes, theme, window and the switch above are kept in
  `ui-settings.json` in the data folder.
- **Design:** System, Hell or Dunkel changes the app. Mail content is always shown on a light background.
- **Info bar:** shows notices, for example when the settings file is invalid (the defaults apply), a list cannot be
  loaded, an action failed or was only partly done, or the spam filter has a problem (see "Spam filter").

Safe display of mail:
- HTML is cleaned before it is shown. Scripts, forms and similar elements are removed.
- In Junk and Quarantine pictures inside the mail are blocked as well, until you click the button below.
- Pictures and other content from the internet are blocked. The button "Bilder laden" shows how many were blocked
  and loads them for this message only; the next message is blocked again. Loading pictures tells the sender that you
  opened the mail.
- Links open only in your default browser (web links and mailto), never inside the app. When the mouse is over a
  link, a tooltip shows the real target (long targets start with the host). The status bar of the web view stays on
  as well for now.
- Control and direction-changing characters are removed from names, subjects and folder names.
- If a message is built unusually (very large, deeply nested or slow to prepare), it is shown in a simplified form or
  as text only. A notice above the message says so. Pictures and links of such a message may be missing.

If the app cannot start (invalid `MAILWARDEN_DATA_DIR`, data folder already in use, database from a newer version),
it shows a window "MailWarden kann nicht starten" with the reason. Critical errors are logged in
`logs\mailwarden.log` (type names and fixed texts only, no mail content).

## Spam filter
After every sync of an account the running app classifies the new mail in Inbox and Junk. Every decision is stored
with all reasons. Mail that was already there when a folder was first synced is never sorted automatically.

- **Default: record only.** Without the file `classification-policy.json` (`mode` `recordOnly`) the filter only
  records its decisions. It moves and deletes nothing.
- **Mode `act`:** only if you set `"mode": "act"` in `classification-policy.json`. Then new mail is moved to Screener,
  Junk or Quarantine through the command queue, soon after it arrived. It pauses while the address book is empty.
  The address book cannot be filled yet (AP-06), so `act` does not move anything yet.
- **Brake:** the key `maxMovesPerPass` (whole number 1 to 1000, default 100) limits the moves of one folder in one
  pass. If more mails would be moved, **none** of them is moved. They stay in their folder and are not decided again.
- **Notice in the app:** the info bar shows "Spamfilter seit dem Start: nicht bewertet N, von der Bremse
  zurückgehalten M. Diese Mails bleiben in ihrem Ordner." The numbers are sums since the app started and stay
  until you close the app. If `act` is set but the address book is empty, a second notice says that the filter waits
  for the address book.
- **Invalid settings file:** if `classification-policy.json` or `classifier-settings.json` is invalid, the app does
  not start. It shows the window "MailWarden kann nicht starten" with the file name. Correct or remove the file and
  start again. The files are read once at start; changes apply after a restart.
- **Errors in the log:** if a pass fails, `logs\mailwarden.log` gets one line with the type name. The account then
  waits like after any other error (see "Background service"). Counts of problems are logged as well, without mail data.

The filter works on the local copy. On the server it only creates the folders Screener and Quarantine and sends the
move commands, both only in mode `act`. It has not been tried against a real mail server in the running app yet.
Details: `docs\classifier.md`.

## Command queue
Actions that change the server (flags, move, move to Trash, permanent delete, empty folder) are never executed
directly. They are stored as commands and executed later; the local store changes only after the server has confirmed.
Invalid commands are rejected when queued.

Deleting works like in Outlook:
- Del moves a message to Trash.
- Shift+Del deletes it permanently (inside Trash and Junk, Del is permanent already).
- "Empty folder" is available for Trash and Junk only and removes permanently just the messages known locally at that
  moment; mail that arrives later stays.

Safety rules:
- There is never an untargeted expunge: only the exact messages of a command are deleted permanently.
- Every command remembers the folder's UIDVALIDITY. If it changed on the server, the command fails without touching
  the server.

Errors: after a network failure the command is tried again later. If the server rejects it, it is retried with growing
delay (1, 2, 4 ... at most 60 minutes) and marked "failed" after 5 attempts. A sign-in failure aborts the run without
counting an attempt. The sync stays read-only towards the server.

The app queues the actions you start (see "Using the app"); the background service executes the queue. Note: until
a command is confirmed by the server, the app shows the wanted state, but the stored state is still the old one.

## Background service
A service keeps all accounts current without user action. The app starts it with the window and stops it when the window closes.

- **Cycle:** per account, first the queued commands are executed, then the account is synced. Accounts run
  independently and in parallel; one account never blocks the others.
- **Schedule:** a new cycle every 15 minutes.
- **New mail:** the service keeps an IDLE connection (read-only) on the inbox and renews it every 9 minutes. A change
  in the inbox (new mail, deleted mail, changed flags) starts a cycle after about 2 seconds. If a server has no IDLE,
  only the schedule applies. Other folders follow the schedule only.
- **Errors:** after a network or server error the account waits 1, 2, 4, 8, 16, then 30 minutes before the next try
  (success resets this). If a new sign-in is needed, the account waits until you retry; it does not retry by itself.
  Other accounts keep running.
- **Status per account:** Running, Idle (ready), WaitingAfterError, NeedsSignIn, Stopped, with time of the last
  success, time of the next run and progress. It contains no error messages, names or mail content, only the error
  type.
- **One process per data folder:** the service holds the lock file `mailwarden.lock` in the data folder. A second
  instance on the same folder does not start and reports that the data folder is in use. The file disappears when
  the service stops; after a crash the lock is released automatically.
- **Stopping:** the service ends running cycles and closes the connections. With a dead connection this can take up
  to about 15 seconds.

## Requirements
- Windows 11
- .NET 9 SDK (`dotnet --version` shows 9.0.x); Visual Studio is not required
- WebView2 runtime (ships with Windows 11)
- Microsoft account: own app registration (see `docs\auth-setup.md`, created in AP-00)
- Google account: own Google Cloud project with OAuth desktop client (see `docs\auth-setup.md`, created in AP-00)

## Build & run
Run all commands from the project folder.

```
dotnet build MailWarden.sln -warnaserror
dotnet test MailWarden.sln
dotnet run --project src\MailWarden.App
```

- Build: the first build restores NuGet packages and needs internet access.
- Tests: `dotnet test` runs the unit tests of the storage layer, the account library, the sync (with a fake mail server connection), the spam classifier core, the HTML cleaning and the view models of the user interface (the window itself is checked by hand). They use temporary folders and artificial data only; no real server is contacted.
- Start: `dotnet run` opens the main window (see "Using the app"). Alternatively start the built
  `src\MailWarden.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\MailWarden.App.exe`. The app is
  self-contained; no separate Windows App SDK runtime has to be installed.

## Data folder
All data lives in one folder:
- default: `data\` next to `MailWarden.App.exe` (during development below `src\MailWarden.App\bin\...`)
- override: set the environment variable `MAILWARDEN_DATA_DIR` to a fully qualified path (for example
  `D:\MailWardenData`). A relative path stops the app at startup.

Layout:

| Entry | Content |
|---|---|
| `mailwarden.db` | SQLite database (schema version 5): accounts, folders, message metadata, full-text search index, sync state per folder (initial-window limit and change counter; every message missing locally is fetched on the next sync), command queue, contacts, classification decisions with their reasons. An older database is upgraded automatically on start. |
| `mail\` | one `.eml` file per message, stored per account and folder |
| `credentials\` | DPAPI-encrypted sign-in tokens, one `.bin` file per entry |
| `auth-config.json` | optional OAuth client configuration (see Environment variables) |
| `ui-settings.json` | optional window settings (layout, theme, sizes, window size and position, read-on-select switch); written by the app when you change them |
| `mailwarden.lock` | lock file while the app runs (one process per data folder) |
| `classifier-settings.json` | optional spam classifier settings (thresholds, lists); read once at start, an invalid file stops the start (see `docs\classifier.md`) |
| `classification-policy.json` | optional policy of the classification pass (`mode`: `recordOnly` (default) or `act`; `quarantineRetentionDays`: 0 = never delete; `maxMovesPerPass`: 1 to 1000, default 100); read once at start, an invalid file stops the start (see "Spam filter" and `docs\classifier.md`) |
| `logs\` | log file `mailwarden.log` (critical errors only) |

On start the app creates the data folder, the database and the lock file itself. `mail\` appears with the first
synced message and `logs\` with the first logged error. Without accounts nothing is synced.

## Project structure
```
src\MailWarden.Core            domain model, storage interfaces, account service, sync interfaces (Sync) and the
                               spam classifier core and the cleaning of displayed text (no I/O, no packages)
src\MailWarden.Infrastructure  SQLite stores, EML file store, OAuth sign-in (Microsoft, Google), DPAPI credential
                               store, provider profiles and connection check, IMAP connection and sync engine
                               (Sync), message reading and HTML cleaning (Mime), runtime that opens
                               the stores and starts the background service (Composition), critical log
src\MailWarden.Presentation    view models, views and settings of the user interface (no WinUI reference)
src\MailWarden.App             WinUI 3 app: main window, wiring of the view models, WebView2 for the message
tests\MailWarden.Core.Tests             unit tests for the spam classifier core
tests\MailWarden.Infrastructure.Tests   unit tests for the storage layer, the account library, the sync, message
                                        reading and HTML cleaning
tests\MailWarden.Presentation.Tests     unit tests for the view models and settings
```

## Environment variables
All variables are optional overrides; see `.env.example`.

| Variable | Purpose |
|---|---|
| `MAILWARDEN_DATA_DIR` | data folder (see above) |
| `MAILWARDEN_MS_CLIENT_ID` | client ID of your Microsoft app registration |
| `MAILWARDEN_GOOGLE_CLIENT_ID` | client ID of your Google OAuth desktop client |
| `MAILWARDEN_GOOGLE_CLIENT_SECRET` | client secret of that Google client |

The three client values can alternatively be placed in `auth-config.json` in the data folder; environment variables
take precedence. Details: `docs\auth-setup.md`. Tokens are stored encrypted with Windows DPAPI in `credentials\` in
the data folder.

## License
[PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0), see `LICENSE`:
free to use, change and share for noncommercial purposes; selling or other commercial use is not permitted.

## AI usage
AI (Claude by Anthropic) was used to assist with the code in this project.
The application icon (`assets/mailwarden-512.png`) was created with Claude Design. No other generative AI was used
to create artwork, music or sounds.
