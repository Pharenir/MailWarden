# AP-01 docs notes (Documenter)

Changed: `README.md` only (Status, Requirements, Build & run, new sections Data folder and Project structure,
Environment variables). `docs\auth-setup.md` and `docs\classifier.md` are not versioned on this branch; untouched.

Open points:
1. `## AI usage` kept as already on the branch (icon made with Claude Design); it deviates from the exact standard
   sentence on purpose because of the icon. Confirm.
2. `.env.example` still says the default data folder is `%LOCALAPPDATA%\MailWarden`; code uses `<exe dir>\data`.
   Not changed by me (not a doc I own) - needs a fix.
3. README requirements still reference `docs\auth-setup.md` (created in AP-00, not on this branch). Link is dead
   until it is merged/versioned.
4. App start was never run (builder: build only); the exe path and "empty window" statement come from the csproj/code.
5. Data folder content: db/mail/logs only appear once the storage layer is used (not by the app in AP-01) - documented so.
