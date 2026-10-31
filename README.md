# MailWarden

Windows mail client for personal use: several accounts (Outlook.com / Gmail / any IMAP) in one unified inbox, with a
transparent four-level spam classifier and user-defined rules. All move/delete actions are executed on the server via
IMAP, so other devices see the same state.

MailWarden is developed and used by a single person and is not offered as a service.
Privacy policy: `PRIVACY.md` · Terms of service: `TERMS.md`.

Status: early development (see `PLAN.md`). Implemented so far: solution skeleton, local storage layer (SQLite
database and EML file store), account and sign-in library (OAuth for Outlook.com and Gmail, encrypted token storage,
provider profiles with a connection check) and an empty WinUI 3 main window. The account library has no user interface
yet; account setup in the app comes with AP-04. There is no synchronisation, no inbox view and no spam classifier yet.

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
- Tests: `dotnet test` runs the unit tests of the storage layer. They use temporary folders and artificial data only.
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
| `mailwarden.db` | SQLite database: accounts, folders, message metadata, full-text search index |
| `mail\` | one `.eml` file per message |
| `credentials\` | DPAPI-encrypted sign-in tokens, one `.bin` file per entry |
| `auth-config.json` | optional OAuth client configuration (see Environment variables) |
| `logs\` | log files |

On start the app creates the data folder itself. The database, `mail\` and `logs\` are created by the storage layer
as soon as it is used; the app does not use it yet, so a fresh start leaves the folder empty.

## Project structure
```
src\MailWarden.Core            domain model, storage interfaces, account service (no I/O, no packages)
src\MailWarden.Infrastructure  SQLite stores, EML file store, OAuth sign-in (Microsoft, Google), DPAPI credential
                               store, provider profiles and connection check
src\MailWarden.App             WinUI 3 app (main window only)
tests\MailWarden.Infrastructure.Tests   unit tests for the storage layer
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
