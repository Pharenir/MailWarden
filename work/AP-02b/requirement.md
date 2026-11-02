---
id: AP-02b
slug: account-ui
title: Account UI
test-scope: S2 during the rounds, S3 before the merge
test-filter: Category=feature:accounts
status: DRAFT, after AP-04c
---

# AP-02b: account UI (draft)

Split from AP-04c by Dennis (2026-11-02), follows after the attachments. Not started.

## What exists (read on `main`, 966df28)
- `AccountService` has `AddAccountAsync`, `ReauthenticateAsync`, `RemoveAccountAsync` (`Core\Accounts\AccountService.cs`), used only by the AuthProbe. `MailWardenRuntime` builds the token providers but keeps no `AccountService` (`Composition\MailWardenRuntime.cs:164-168`). `MailboxService.AccountsChangedAsync` starts and stops loops at run time (`Background\MailboxService.cs:288`).
- Removing an account cascades in the database (`SchemaMigrations.cs`), but **EML files stay on disk**: `IEmlStore` deletes single files only.
- **Contacts have no link to an account:** `contacts` has a unique address and a source, no account (`SchemaMigrations.cs:67`). `sender_list` is to be checked for the same.

## Behaviour (proposal)
| # | Behaviour |
|---|---|
| B1 | A window or panel "Konten": every account with address, provider, status from `AccountSyncStatus` |
| B2 | Konto hinzufügen: Microsoft or Google, the browser opens, cancelling leaves nothing behind and is not an error. The new account appears in the tree and syncs without a restart (`AccountsChangedAsync`) |
| B3 | Neu anmelden, offered when the status is `NeedsSignIn`; a different account in the browser is refused with a clear message |
| B4 | Verbindung testen with the existing `ConnectionProbe`; the result names IMAP and SMTP, no server text |
| B5 | Konto entfernen, decision of Dennis: an **additional confirmation** (address, number of local mails) and then **all local data of this account** goes: sign-in, loop stopped, database cascade, EML files, decision log, also the contacts that came only from this account. **Nothing on the server changes** |
| B6 | Missing or wrong `auth-config.json`: a clear message instead of a crash; the rest of the app keeps working |
| B7 | Logging rule from AP-02a: of an `AuthenticationFailedException` only provider, reason, code; never `ToString()`. Tokens never leave `ICredentialStore` |
| B8 | Points from `TODO.md` this part closes: the totals of the hint after a removal, cleanup of orphaned grants and `*.tmp` at the start, `DataPaths.CredentialsDirectory`/`AuthConfigPath`, the shared token providers in one `AccountService` |

Security review required (sign-in, token cache, deleting).

## Decisions of Dennis (2026-11-02)
- Removing an account deletes all local data of the account after an additional confirmation (B5).

## Open
- Contacts: to delete those of one account the origin must be recorded (a new table contact to account, schema change, S3). A contact seen in two accounts stays until the last one goes. Contacts collected before that change have no origin: proposal, they stay, and the question says so. To be decided by Dennis. Moves a part of the AP-08 point "Kontoentfernung und die Kontakte des Kontos" here.
- Revoking at Google: not "local data" and "nothing on the server changes", so left out for now; the point stays in `TODO.md`.
