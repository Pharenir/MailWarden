# MailWarden

Windows mail client for personal use: several accounts (Outlook.com / Gmail / any IMAP) in one unified inbox, with a
transparent four-level spam classifier and user-defined rules. All move/delete actions are executed on the server via
IMAP, so other devices see the same state.

MailWarden is developed and used by a single person and is not offered as a service.
Privacy policy: `PRIVACY.md` · Terms of service: `TERMS.md`.

Status: early development (see `PLAN.md`). Implemented so far: solution skeleton, local storage layer (SQLite
database and EML file store), the core of the spam classifier (a library in `MailWarden.Core`, not yet connected to
any mail) and an empty WinUI 3 main window. There is no mail functionality yet: no account setup, no
synchronisation, no inbox view; the classifier does not sort or move any mail yet.

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
- Tests: `dotnet test` runs the unit tests of the storage layer and the spam classifier core. They use temporary folders and artificial data only.
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
| `logs\` | log files |

On start the app creates the data folder itself. The database, `mail\` and `logs\` are created by the storage layer
as soon as it is used; the app does not use it yet, so a fresh start leaves the folder empty.

## Project structure
```
src\MailWarden.Core            domain model, storage interfaces and the spam classifier core (no I/O, no packages)
src\MailWarden.Infrastructure  SQLite stores and EML file store
src\MailWarden.App             WinUI 3 app (main window only)
tests\MailWarden.Core.Tests             unit tests for the spam classifier core
tests\MailWarden.Infrastructure.Tests   unit tests for the storage layer
```

## Environment variables
Only `MAILWARDEN_DATA_DIR` (see above) is evaluated. It is optional. Credentials will be stored encrypted with
Windows DPAPI in the data folder once account setup exists. See `.env.example`.

## License
[PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0), see `LICENSE`:
free to use, change and share for noncommercial purposes; selling or other commercial use is not permitted.

## AI usage
AI (Claude by Anthropic) was used to assist with the code in this project.
The application icon (`assets/mailwarden-512.png`) was created with Claude Design. No other generative AI was used
to create artwork, music or sounds.
