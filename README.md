# MailWarden

Windows mail client for personal use: several accounts (Outlook.com / Gmail / any IMAP) in one unified inbox, with a
transparent four-level spam classifier and user-defined rules. All move/delete actions are executed on the server via
IMAP, so other devices see the same state.

MailWarden is developed and used by a single person and is not offered as a service.
Privacy policy: `PRIVACY.md` · Terms of service: `TERMS.md`.

Status: planning (see `PLAN.md`). No application code yet.

## Requirements
- Windows 11
- .NET 9 SDK
- WebView2 runtime (ships with Windows 11)
- Microsoft account: own app registration (see `docs\auth-setup.md`, created in AP-00)
- Google account: own Google Cloud project with OAuth desktop client (see `docs\auth-setup.md`, created in AP-00)

## Build & run
```
dotnet build MailWarden.sln
dotnet run --project src\MailWarden.App
```

## Environment variables
None required. Credentials are stored encrypted with Windows DPAPI in the data folder (`<install>\data\` by default,
override with `MAILWARDEN_DATA_DIR`). See `.env.example`.

## License
[PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0), see `LICENSE`:
free to use, change and share for noncommercial purposes; selling or other commercial use is not permitted.

## AI usage
AI (Claude by Anthropic) was used to assist with the code in this project.
No generative AI was used to create artwork, music or sounds.
