STEP-RESULT: done (fix.3; manual cases 28–30 still pending – only Dennis runs the AuthProbe)

# AP-02a – Implementation notes (Builder)

Branch `feat/ap-02a-accounts-auth`. Build: `dotnet build MailWarden.sln -warnaserror` → 0 warnings, 0 errors
(after every change of this step).

## fix.3 (2026-10-31)
Basis: requirement.md "Planner decisions … (fix.3)" A19/A20, `review.md` review.2 (SHOULD 1, NIT 3/4). Commit
`024526f`. Build `-warnaserror` (solution incl. test projects, compile only) green. Nothing under `tests\` read.

| # | Change | File:line | Verified by (builder probe, not a tester run) |
|---|---|---|---|
| A19 | `ReauthenticateAsync` discards the wrong grant via `SignOutBestEffortAsync` (`CancellationToken.None`, swallows only `AuthenticationFailedException`) → a failing sign-out no longer replaces AccessDenied; method doc unchanged. | `Core\Accounts\AccountService.cs:150-154` | A19 / 25c: fake signs in `other@example.test` (not stored), SignOut throws `AuthenticationFailedException(credential_store_unavailable)` → AccessDenied (code null), SignOut called once, account unchanged; SignOut throwing `InvalidOperationException` → propagates (as documented). 25, 25a still PASS |
| A20 | Comments say exactly what is swallowed (only `AuthenticationFailedException`; cancellation/other exceptions propagate or replace the result) and that a grant may remain on disk, encrypted but valid at the provider (orphaned-grant TODO, A18). `DpapiCredentialStore` remarks: one instance per directory, shared by all token providers (AP-04 composition root). | `AccountService.cs:184-188` (removal), `:194-197` (helper); `Security\DpapiCredentialStore.cs:15-19` | build only (comments/doc) |

- Getestet: probe `_scratch\ap02a-probe\` 48/48 PASS (all previous cases + A19). No network, no browser.
- Ungetestet: cancellation by the caller between sign-in and cleanup in re-auth (fake ignores the token; the
  account lookup before it already throws on a cancelled token) – the cleanup itself uses `CancellationToken.None`
  (code review only).
- Offene Fragen: keine. (review.2 NIT 2 – lookup between sign-in and cleanup – belongs to the A18 orphaned-grant
  TODO; not changed.)

## fix.2 (2026-10-31)
Basis: requirement.md "Planner decisions … (fix.2)" A9–A18, `review.md` (review.1), `security-review.md` (secure.1).
Commit `89dc243`. Build `-warnaserror` (solution incl. the merged test projects, compile only – no test run) green.
No AP-01 file changed (`FileEmlStore` untouched), nothing under `tests\` read, `PLAN.md` unchanged.

| # | Change | File:line | Verified by (builder probe, not a tester run) |
|---|---|---|---|
| A9 | ⚠️ Reads open with `FileShare.Read \| FileShare.Delete` (`ReadSharedAsync`); temp cleanup best effort (`DeleteTemporaryFile`, never hides the original exception). **Deviation/addition:** the share flag alone does **not** reach the goal – measured on Windows 11 26200 (`_scratch\share-check\`): `File.Move(overwrite)` onto a target held open with Read\|Delete sharing → `UnauthorizedAccessException`. `File.Replace` works there but was rejected: it has a window without target (parallel reader got `null` = "no grant") and 36/40 concurrent writers failed. Implemented instead: `File.Move` (atomic) + one `SemaphoreSlim` per store instance that serialises read/write/delete (review SHOULD 1 alternative). Doc: one instance per directory. | `Security\DpapiCredentialStore.cs:32-34` (lock), `:53-77` (read), `:100-127` (write), `:130-146` (delete), `:186-200` (`ReadSharedAsync`), `:202-219` (`DeleteTemporaryFile`), remarks `:13-19` | A9a (40 concurrent writers + 8 readers against a sequential 200-write loop on one key: no IO error, every read complete; info: an **external** held handle still blocks the replace → documented), A9c (120 mixed read/write/delete ops), A9b (target blocked by a directory → original `UnauthorizedAccessException`, no `.tmp` left), cases 1–7 unchanged |
| A10 | `IOException`/`UnauthorizedAccessException` of the credential store → `AuthenticationFailedException(Unknown)`, code `credential_store_unavailable`, inner kept, own sentence. Google: read, write, sign-out; Microsoft: cache load and save callbacks (MSAL passes the callback exception through unchanged – observed). `CredentialStoreException` mapping unchanged; `ITokenProvider` doc unchanged. | `Auth\AuthFailures.cs:15-16, 55-79`; `Auth\GoogleTokenProvider.cs:127-138, 297-304, 345-352`; `Auth\MicrosoftTokenProvider.cs:201-205, 242-245`; class docs | A10 (directory in place of `google.<hash>.bin` / `msal.cache.bin`: Google silent + sign-out, Microsoft silent + sign-out → Unknown/`credential_store_unavailable`, inner IO type) |
| A11 | `RemoveAccountAsync`: `AuthenticationFailedException` from `SignOutAsync` is swallowed, account is deleted; other exceptions propagate (account stays). | `Core\Accounts\AccountService.cs:162-188` | A11 (26a fake Configuration → deleted, no exception; fake `InvalidOperationException` → propagates, account stays; real `MicrosoftTokenProvider` without config and real `GoogleTokenProvider` with inaccessible grant file → both accounts deleted), 26, A8 |
| A12 | Add: address stored under another provider → fresh grant signed out (best effort, `CancellationToken.None`), `AuthenticationFailedException(AccessDenied)`, code `address_in_use`, store unchanged; same provider → existing account (unchanged). Re-auth: A3 guard only for a stored account **of the same provider**. | `AccountService.cs:15-18` (message/code), `:74-90` (add), `:147-155` (re-auth) | A12 (22a: Google account `shared@…`, MS fake signs in `SHARED@…` → AccessDenied/`address_in_use`, MS SignOut called, Google not, store unchanged; same-provider re-add returns existing; MS re-auth signing in the stored Google address → MS grant signed out), 21, 25, 25a |
| A13 | Storing a new account fails → fresh grant signed out (best effort, `CancellationToken.None`, an `AuthenticationFailedException` of the sign-out is swallowed), store exception rethrown unchanged. | `AccountService.cs:92-102`, helper `:190-203` | A13 (real in-memory SQLite with a `BEFORE INSERT` trigger that aborts → `StorageException` rethrown, SignOut called, store empty; SignOut failing too → still the `StorageException`) |
| A14 | Google: random `state` (32 bytes, `Base64Url`, 43 chars) set on the request, checked first with `CryptographicOperations.FixedTimeEquals` → else Unknown/`state_mismatch`; redirect `error` kept as code only if `[a-z_]{1,64}`, else null (mapping then Unknown). | `Auth\GoogleTokenProvider.cs:35-38` (constants), `:152-176` (flow), `:211-243` (helpers) | A14 via reflection on the internal helpers (format, randomness, match/mismatch/null/empty/prefix/case, sanitising of 13 values). The real browser round trip is **untested** (case 28/30) |
| A15 | Message without address: "Not a valid email address (expected local@domain without whitespace)." | `Providers\ProviderProfiles.cs:79-83` | A15 (4 invalid inputs; message contains neither input nor its domain), 14 |
| A16 | Disconnect after successful IMAP/SMTP check: failures ignored, caller cancellation still thrown. | `Providers\ConnectionProbe.cs:126-127, 143-144, 147-164` | A16 via reflection on `DisconnectIgnoringFailuresAsync` (IO/Timeout/OCE-without-caller-cancel ignored; caller cancel thrown; delegate called). Real servers untested (case 28) |
| A17 | `MicrosoftTokenProvider` remarks: one instance per credential store (singleton), last writer wins otherwise. NIT 9: `MissingCodeError` → `MissingAuthorizationCodeCode`. NIT 8 not changed (contract sentence stays). | `Auth\MicrosoftTokenProvider.cs:16-29`; `Auth\GoogleTokenProvider.cs:35` | build only (doc/rename) |
| A18 | nothing done (TODO items for the planner). | – | – |

### Getestet / ungetestet (fix.2)
- **Getestet (builder probe `_scratch\ap02a-probe\`, 47/47 PASS, 2026-10-31):** all earlier cases 1–27, 25a, A1–A8,
  G-silent, MS-silent, CP plus A9a, A9b, A9c, A10, A11, A12, A13, A14, A15, A16 as listed above. No network, no
  browser. AuthProbe rebuilt (0 warnings), not started.
- **Ungetestet:** state round trip with the real Google redirect (whether Google echoes `state` – it should per
  RFC 6749 – is only visible in case 28/30); disconnect against real servers; behaviour of an **external** process
  reading a credential file while MailWarden writes (replace is denied → the writing token provider reports
  `credential_store_unavailable`); MSAL save callback with an IO failure (needs a real sign-in; only the load
  callback was observed).

### Offene Fragen (fix.2)
- `Security\DpapiCredentialStore.cs:32-34` – A9 deviates in its mechanism (in-process lock instead of relying on
  the share flag, which Windows does not honour for `File.Move`). Planner: accept? Cross-process concurrency (e.g.
  AuthProbe and app on the same data folder) stays unprotected; documented as "one instance per directory".
- `Core\Accounts\AccountService.cs:15-18` – `address_in_use` is a MailWarden code in `ProviderErrorCode` (as decided);
  message wording "This address is already used by a MailWarden account of another provider." – fine for AP-04?
- A18 items are not in `TODO.md` yet (decision said "→ TODO.md"; builder was told "only take note") – planner adds
  them.

## Done / remaining
| Part | Status |
|---|---|
| Core contract (`ICredentialStore`, `CredentialStoreException`, `AuthFailureReason`, `AuthenticationFailedException`, `AccessToken`, `ITokenProvider`, `ServerFolderFlags`, `FolderClassification`, `FolderRoleResolver`, `AccountService`) | implemented, probe-verified (see below) |
| `DpapiCredentialStore` | implemented, probe-verified |
| `AuthConfiguration`, `AuthErrorMapping`, `MicrosoftTokenProvider`, `GoogleTokenProvider` | implemented; non-interactive paths probe-verified, browser paths untested (cases 28–30) |
| `ProviderProfiles`, `MailKitFolderFlags`, `ConnectionProbe` (+ records) | implemented; mapping and argument paths probe-verified, network part untested (case 28) |
| Planner decisions A1–A8 | done (table below) |
| Non-interactive probe (cases 1–27 + A-decisions) `_scratch\ap02a-probe\` | done, 37/37 PASS |
| `_scratch\auth-probe\` AuthProbe console (cases 28–30) | built (0 warnings), **not started with a browser**; usage below |

## Planner decisions 2026-10-31 – implementation
| # | Change | Where | Verified by |
|---|---|---|---|
| A1 | `ListKeysAsync`: null → ArgumentNullException, `""` → all keys, non-empty prefix validated with the key rules | `DpapiCredentialStore.cs:132-141`; doc `ICredentialStore.cs:36-44` | probe A1, 3, 5, 27 |
| A2 | unchanged: malformed / non-object / non-string config → Configuration at `Load`, Provider GenericImap, message = file + position, no content, no inner JsonException | `AuthConfiguration.cs:104-128, 155-158` | probe 18 (three malformed variants, message checked for content leak) |
| A3 | unchanged: wrong account on re-auth is signed out only if not itself a stored account | `AccountService.cs:116-124` | probe 25, 25a |
| A4 | Interactive sign-in timeout, Microsoft and Google: optional trailing ctor parameter `TimeSpan? interactiveSignInTimeout = null` (null = 5 min, ≤ 0 or > int.MaxValue ms → ArgumentOutOfRangeException). Expiry → `AuthenticationFailedException(Unknown)`, code `sign_in_timeout`, own sentence "… sign-in timed out because the browser did not return to MailWarden in time."; caller cancellation → `OperationCanceledException` with the caller's token | helper `Auth\InteractiveSignInTimeout.cs:23-74` (internal); `AuthFailures.cs:13, 42-50`; `MicrosoftTokenProvider.cs:47-62, 101`; `GoogleTokenProvider.cs:45-60, 112` | probe A4 (via reflection on the internal helper: timeout, timeout with non-OCE library exception, caller cancel, pass-through of auth failures/results; ctor default/custom/invalid). The real browser wait is **not** verified (case 30, see usage) |
| A5 | Google key = `"google." + Convert.ToHexStringLower(SHA256(UTF-8(address.Trim().ToLowerInvariant())))` (71 chars) | `GoogleTokenProvider.cs:176-188`; class doc `:14-23` | probe A5 (expected hash, length, normalisation, no `a@b.c`/`a_b.c` collision, 213-char address; `SignOutAsync` deletes exactly the hashed key), G-silent |
| A6 | Doc only: keys effectively case-insensitive, callers use lower-case keys | `ICredentialStore.cs:3-13`; `DpapiCredentialStore.cs:13-16` | probe A6 (documents behaviour: `Case.Key` readable as `case.key`) |
| A7 | **Was ordinal → fixed**: Microsoft `Archive`, `Notes`, `Outbox`, `Scheduled`, `Synchronisierungsprobleme` (+ children prefix) now `OrdinalIgnoreCase`; `Screener`/`Quarantine` stay exact (rule 4) | `FolderRoleResolver.cs:19-21, 45-46, 102-118` | probe A7 (`OUTBOX`, `notes`, `SCHEDULED`, `archive`, `SYNCHRONISIERUNGSPROBLEME/Konflikte`; `screener` → None; Google unaffected) |
| A8 | unchanged: local grant deletion only; no token provider → delete without sign-out | `AccountService.cs:134-148` | probe 26, A8 |

### Additional fix found by the probe
- `MicrosoftTokenProvider.cs:193-202`: a `msal.cache` that decrypts but is no valid MSAL cache made silent calls fail
  with `Unknown` / `json_parse_failed`. Now treated like an undecryptable cache (deviation 5): silent →
  `InteractionRequired` / `token_cache_unreadable`, interactive sign-in overwrites it. Probe MS-silent.

## Files
| File | Content |
|---|---|
| `src\MailWarden.Core\Accounts\ICredentialStore.cs`, `CredentialStoreException.cs` | credential store contract (A1/A6 doc) |
| `src\MailWarden.Core\Accounts\AuthFailureReason.cs`, `AuthenticationFailedException.cs`, `AccessToken.cs`, `ITokenProvider.cs` | auth contract; `AccessToken.ToString()` without `Value` |
| `src\MailWarden.Core\Accounts\ServerFolderFlags.cs`, `FolderClassification.cs`, `FolderRoleResolver.cs` | folder role rules 1–6 (A7) |
| `src\MailWarden.Core\Accounts\AccountService.cs` | add / token / re-auth / remove |
| `src\MailWarden.Infrastructure\MailWarden.Infrastructure.csproj` | packages Google.Apis.Auth 1.78.0, MailKit 4.18.2, Microsoft.Identity.Client 4.92.0, System.Security.Cryptography.ProtectedData 10.0.14 |
| `src\MailWarden.Infrastructure\Security\DpapiCredentialStore.cs` | `<dir>\<key>.bin`, DPAPI CurrentUser + entropy `MailWarden.Credentials.v1`, temp `<key>.<guid>.tmp` + `File.Move(overwrite)` |
| `src\MailWarden.Infrastructure\Auth\AuthConfiguration.cs` | file + env override, `Require*()`, `ToString()` shows only set/missing |
| `src\MailWarden.Infrastructure\Auth\AuthErrorMapping.cs` | code tables as in the contract |
| `src\MailWarden.Infrastructure\Auth\AuthFailures.cs` (internal) | one fixed English sentence per provider × reason; timeout sentence + code |
| `src\MailWarden.Infrastructure\Auth\InteractiveSignInTimeout.cs` (internal, **new**) | default 5 min, validation, linked-token runner |
| `src\MailWarden.Infrastructure\Auth\MicrosoftTokenProvider.cs` | MSAL consumers, `http://localhost`, cache in key `msal.cache`, timeout |
| `src\MailWarden.Infrastructure\Auth\GoogleTokenProvider.cs` | PKCE installed-app flow, loopback receiver, ID-token validation, hashed key, timeout |
| `src\MailWarden.Infrastructure\Providers\ProviderProfile.cs`, `ProviderProfiles.cs`, `MailKitFolderFlags.cs`, `ConnectionProbe.cs` | profiles, detection, flag mapping, probe |

Nothing under `tests\`; `PLAN.md` and `requirement.md` unchanged; no AP-01 file changed.

### Interface changes in this step (S3 trigger: `src\MailWarden.Core\**\I*.cs`)
- `ICredentialStore.cs`: XML doc only (A1 prefix rules, A6 case note) – no signature change.
- Public ctors `MicrosoftTokenProvider` / `GoogleTokenProvider`: new optional trailing parameter
  `TimeSpan? interactiveSignInTimeout = null` (source-compatible with the contract ctor).

## How Google obtains the address
`GoogleTokenProvider.SignInInteractiveAsync` → `InteractiveSignInTimeout.RunAsync` → `SignInWithBrowserAsync`:
`PkceGoogleAuthorizationCodeFlow` (no DataStore) → `LocalServerCodeReceiver` (loopback, system browser,
`prompt=consent select_account`, `access_type=offline`, optional `login_hint`) → code exchange with PKCE verifier →
`GoogleJsonWebSignature.ValidateAsync(id_token, Audience = [clientId])` → `payload.Email` non-empty and
`EmailVerified == true`, else `AuthenticationFailedException(Unknown, "email_not_verified" / "invalid_id_token")`. The
hint is never used as the address. The ID token is removed before the token response is stored.

## Deviations / decisions the contract leaves open (status after planner decisions)
1. Re-auth guard (`AccountService.cs:116-124`) → accepted as A3.
2. Remove without token provider (`AccountService.cs:142`) → accepted as A8.
3. Malformed config at `Load`, Provider GenericImap (`AuthConfiguration.cs:155-158`) → accepted as A2.
4. `ListKeysAsync` prefix rules → changed per A1.
5. Unreadable `msal.cache` → `InteractionRequired` / `token_cache_unreadable` (now also for decryptable garbage,
   `MicrosoftTokenProvider.cs:193-202`); unreadable Google grant → `InteractionRequired` / `stored_grant_unreadable`.
6. `ConnectionProbe.RunAsync` throws `ArgumentException` when `token.Username` ≠ account address
   (`ConnectionProbe.cs:55`); IMAP and SMTP always both run; both failing → one sentence joined by `;`.
7. Cancellation: library exceptions raised while the (linked) token is cancelled become `OperationCanceledException`;
   in the interactive path every non-auth exception after cancellation is mapped (caller → OCE, timeout →
   `sign_in_timeout`, `InteractiveSignInTimeout.cs:59-72`).
8. `AuthConfiguration` trims values; empty/whitespace values count as missing.
9. `FolderRoleResolver.Resolve(…, null, …)` throws `ArgumentNullException` (a subclass of `ArgumentException`; contract
   "null/empty → ArgumentException" holds; tests should not assert the exact type for null).

## Verified vs. untested
**Verified (builder probe `_scratch\ap02a-probe\`, `dotnet run`, 2026-10-31, 37/37 PASS – not a tester run):**
cases 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 25a, 26, 27 with a
hand-written fake `ITokenProvider` (probe only), real `DpapiCredentialStore` in temp folders below the probe's `bin`,
real in-memory SQLite stores; plus A1, A4, A5, A6, A7, A8 and:
- G-silent: Google silent path from a stored token without network – fresh token returned (no network), no grant /
  stale without refresh token → InteractionRequired, stale with refresh token but missing config → Configuration,
  invalid JSON / undecryptable file → InteractionRequired `stored_grant_unreadable`.
- MS-silent: Microsoft without client id → Configuration naming `MAILWARDEN_MS_CLIENT_ID`; dummy client id + empty
  cache → InteractionRequired (`DeserializeMsalV3([])` works); undecryptable and decryptable-garbage cache →
  InteractionRequired `token_cache_unreadable`; sign-out on empty/unreadable cache = no-op.
- CP: `ConnectionProbe` argument paths without network (token user mismatch → ArgumentException, GenericImap →
  NotSupportedException, null → ArgumentNullException).
- AuthProbe non-interactive paths (temp data root under `_scratch\`, removed afterwards): usage, `list` on an empty
  root, unknown account, unknown provider, `add ms` / `add google` without configuration → Configuration error before
  any browser start (exit 2).

**Untested:**
- Cases 28–30 (real browser sign-in, real IMAP/SMTP, cached second run, cancel/deny) – Dennis, AuthProbe below.
- `ConnectionProbe` network part (endpoints are fixed in `ProviderProfiles`, so no unreachable-host run was possible
  without changing code) – covered by case 28.
- The 5-minute timeout against the real MSAL/Google loopback listener (only the helper is verified); which exception
  the libraries throw on cancellation is irrelevant for the mapping but not observed.
- Google token refresh with a real refresh token (`RefreshTokenAsync` with null DataStore), `IsStale` window.

## AuthProbe usage for Dennis
Opens the system browser – start it yourself in your own terminal. All data stays below `_scratch\` of the worktree
(git-ignored). The AuthProbe never prints token values.

```powershell
cd K:\Projekte\Windows-Tools\MailWarden\.worktrees\feat-ap-02a
$probeData = "$PWD\_scratch\auth-probe-data"
New-Item -ItemType Directory -Force $probeData | Out-Null

# Client configuration – EITHER environment variables for this terminal (values from your app registrations):
$env:MAILWARDEN_MS_CLIENT_ID = '<Microsoft application id>'
$env:MAILWARDEN_GOOGLE_CLIENT_ID = '<Google client id>'
$env:MAILWARDEN_GOOGLE_CLIENT_SECRET = '<Google client secret>'
# OR a file "$probeData\auth-config.json" with the keys microsoftClientId, googleClientId, googleClientSecret.

dotnet build _scratch\auth-probe
function AuthProbe { dotnet run --no-build --project _scratch\auth-probe -- $probeData @args }

# Case 28 – sign in (browser opens; 5-minute limit; Ctrl+C cancels), then probe each account
AuthProbe add ms <first hotmail.de address>
AuthProbe add ms <second hotmail.de address>
AuthProbe add google <googlemail address>
AuthProbe list
AuthProbe probe <address>        # once per account

# Case 29 – new terminal (set the variables again or use auth-config.json), define the function again, then:
AuthProbe probe <address>        # once per account – must NOT open a browser
AuthProbe list                   # "Credential files: N (all .bin)"
Select-String -Path "$probeData\credentials\*" -Pattern 'refresh_token','access_token','@' -SimpleMatch   # expect no output

# Case 30 – cancel / deny
AuthProbe add ms                 # click "Cancel"/"Abbrechen" on the Microsoft page (or deny consent)
AuthProbe add google             # click "Cancel"/"Abbrechen" on the Google consent page
# Optional: close the browser tab instead → after 5 minutes "Code: sign_in_timeout"; or press Ctrl+C → "Cancelled".

# Clean-up
AuthProbe remove <address>       # once per account; afterwards only msal.cache.bin may remain (other MS accounts)
Remove-Item -Recurse -Force $probeData
```

What to check:
- **28**: each `probe` prints `IMAP ok: True, SMTP ok: True` and no `Error`. Folder table (role / visibility):
  Outlook.com – `Inbox` Inbox, `Sent` Sent, `Junk` Junk, `Drafts` Drafts, `Deleted` Trash, `Archive` Archive (children
  None visible), `Notes`/`Outbox`/`Scheduled`/`Synchronisierungsprobleme…` None hidden. Gmail – `INBOX` Inbox,
  `[Gmail]` None hidden, `[Gmail]/All Mail` All hidden, `[Gmail]/Spam` Junk, `[Gmail]/Bin`/`Papierkorb` Trash,
  `[Gmail]/Sent Mail` Sent, `[Gmail]/Drafts` Drafts, `[Gmail]/Important`/`Starred` None hidden, `Junk-E-Mail` **None
  visible**. Account line shows the address you actually signed in with (not the hint).
- **29**: no browser; line `Token (silent, no browser)`; credential files are `msal.cache.bin` plus one
  `google.<64 hex>.bin` per Google account, all `.bin`, `Select-String` finds nothing.
- **30**: output `Sign-in failed:` with `Reason: UserCancelled` or `AccessDenied`, a readable one-sentence message,
  exit code 2, no stack trace; `list` shows no new account.
- `$LASTEXITCODE`: 0 ok, 1 usage/unknown account, 2 sign-in failed, 3 probe failed (IMAP/SMTP), 130 Ctrl+C.

Note: the AuthProbe and its data live in the worktree; removing the worktree deletes them (including the encrypted
tokens).

## Open questions
- `MicrosoftTokenProvider.cs:96-110` – Ich bin mir unsicher, which MSAL error code the Microsoft "Cancel" button
  produces (`access_denied` vs. `authentication_canceled`); both map to a case-30-conforming reason. Case 30 shows it.
- `GoogleTokenProvider.cs:176-188` – Google sign-out still does not revoke the grant server-side (A8: later AP).
- Role prompt says "keine Commits", coordinator/project pipeline requests local commits → followed the coordinator.
