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
server to the local store (folders, messages, flags, deletions; read-only towards the server) and an empty WinUI 3 main
window. The sync is a library only: it has no user interface and it never changes anything on the server. It fetches missing messages in batches (50 by default), reports progress per folder (number, role,
processed/missing; no names), reconnects within a pass if the connection was lost (at most 3 times) and does not sync
the Gmail folder "Important". A command queue (local to server: flags, move, trash, delete, empty folder; see
"Command queue") and a background service with schedule and IDLE (see "Background service") are implemented as
libraries as well. Account setup in the app comes with AP-04; there is no inbox view
yet, and the classifier does not sort or move any mail yet.

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

Not available yet: there is no user interface for this (AP-04). The background service executes the queue.

## Background service
A service keeps all accounts current without user action. It is a library; the app does not start it yet (AP-04).

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
- Tests: `dotnet test` runs the unit tests of the storage layer, the account library, the sync (with a fake mail server connection) and the spam classifier core. They use temporary folders and artificial data only; no real server is contacted.
- Start: `dotnet run` opens the main window (currently empty). Alternatively start the built
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
| `mailwarden.db` | SQLite database (schema version 4): accounts, folders, message metadata, full-text search index, sync state per folder (initial-window limit and change counter; every message missing locally is fetched on the next sync), command queue. An older database is upgraded automatically on start. |
| `mail\` | one `.eml` file per message, stored per account and folder |
| `credentials\` | DPAPI-encrypted sign-in tokens, one `.bin` file per entry |
| `auth-config.json` | optional OAuth client configuration (see Environment variables) |
| `classifier-settings.json` | optional spam classifier settings (thresholds, lists); the loader exists, the app does not read the file yet (see `docs\classifier.md`) |
| `logs\` | log files |

On start the app creates the data folder itself. The database, `mail\` and `logs\` are created by the storage layer
as soon as it is used; the app does not start a sync yet, so a fresh start leaves the folder empty.

## Project structure
```
src\MailWarden.Core            domain model, storage interfaces, account service, sync interfaces (Sync) and the
                               spam classifier core (no I/O, no packages)
src\MailWarden.Infrastructure  SQLite stores, EML file store, OAuth sign-in (Microsoft, Google), DPAPI credential
                               store, provider profiles and connection check, IMAP connection and sync engine
                               (Sync), message text extraction for search and preview (Mime)
src\MailWarden.App             WinUI 3 app (main window only)
tests\MailWarden.Core.Tests             unit tests for the spam classifier core
tests\MailWarden.Infrastructure.Tests   unit tests for the storage layer, the account library and the sync
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
