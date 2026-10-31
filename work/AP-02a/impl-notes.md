STEP-RESULT: partial

# AP-02a – Implementation notes (Builder)

Branch `feat/ap-02a-accounts-auth`. Build: `dotnet build MailWarden.sln -warnaserror` → 0 warnings, 0 errors.
Session ended by the coordinator before the probe phase: the whole contract is implemented and builds, but **no probe
was run** and the **AuthProbe console was not written**. Nothing below is verified at runtime.

## Done / remaining
| Part | Status |
|---|---|
| Core contract (`ICredentialStore`, `CredentialStoreException`, `AuthFailureReason`, `AuthenticationFailedException`, `AccessToken`, `ITokenProvider`, `ServerFolderFlags`, `FolderClassification`, `FolderRoleResolver`, `AccountService`) | implemented, builds, unverified |
| `DpapiCredentialStore` | implemented, builds, unverified |
| `AuthConfiguration`, `AuthErrorMapping`, `MicrosoftTokenProvider`, `GoogleTokenProvider` | implemented, builds, unverified |
| `ProviderProfiles`, `MailKitFolderFlags`, `ConnectionProbe` (+ records) | implemented, builds, unverified |
| Non-interactive probe (cases 1–27) under `_scratch\` | **not done** |
| `_scratch\auth-probe\` AuthProbe console (cases 28–30) | **not done** – next session: console with `<dataRoot> add <ms\|google> [hint] \| list \| probe <id\|email> \| remove <id\|email>`, `DataPaths`, `SqliteDatabase.ForFile`, `DpapiCredentialStore(<root>\credentials)`, `AuthConfiguration.Load(<root>\auth-config.json, Environment.GetEnvironmentVariable)`, Ctrl+C → cancellation, prints only non-secret info; csproj must set `GenerateDocumentationFile=false` (Directory.Build.props applies to `_scratch` too) |

## Files
| File | Content |
|---|---|
| `src\MailWarden.Core\Accounts\ICredentialStore.cs`, `CredentialStoreException.cs` | credential store contract |
| `src\MailWarden.Core\Accounts\AuthFailureReason.cs`, `AuthenticationFailedException.cs`, `AccessToken.cs`, `ITokenProvider.cs` | auth contract; `AccessToken.ToString()` without `Value` |
| `src\MailWarden.Core\Accounts\ServerFolderFlags.cs`, `FolderClassification.cs`, `FolderRoleResolver.cs` | folder role rules 1–6 |
| `src\MailWarden.Core\Accounts\AccountService.cs` | add / token / re-auth / remove |
| `src\MailWarden.Infrastructure\MailWarden.Infrastructure.csproj` | packages Google.Apis.Auth 1.78.0, MailKit 4.18.2, Microsoft.Identity.Client 4.92.0, System.Security.Cryptography.ProtectedData 10.0.14 (latest stable on nuget.org 2026-10-31) |
| `src\MailWarden.Infrastructure\Security\DpapiCredentialStore.cs` | `<dir>\<key>.bin`, DPAPI CurrentUser + entropy `MailWarden.Credentials.v1`, temp `<key>.<guid>.tmp` + `File.Move(overwrite)` |
| `src\MailWarden.Infrastructure\Auth\AuthConfiguration.cs` | file + env override, `Require*()`, `ToString()` shows only set/missing |
| `src\MailWarden.Infrastructure\Auth\AuthErrorMapping.cs` | code tables as in the contract |
| `src\MailWarden.Infrastructure\Auth\AuthFailures.cs` (internal) | one fixed English sentence per provider × reason |
| `src\MailWarden.Infrastructure\Auth\MicrosoftTokenProvider.cs` | MSAL consumers, `http://localhost`, cache in key `msal.cache` via async cache callbacks |
| `src\MailWarden.Infrastructure\Auth\GoogleTokenProvider.cs` | PKCE installed-app flow, loopback receiver, ID-token validation, key `google.<addr>` |
| `src\MailWarden.Infrastructure\Providers\ProviderProfile.cs` | `TransportSecurity`, `ServerEndpoint`, `ProviderProfile` |
| `src\MailWarden.Infrastructure\Providers\ProviderProfiles.cs`, `MailKitFolderFlags.cs`, `ConnectionProbe.cs` | profiles, detection, flag mapping, probe (+ `FolderProbe`, `ConnectionProbeResult`) |

Nothing under `tests\`; `PLAN.md` and `requirement.md` unchanged.

## How Google obtains the address
`GoogleTokenProvider.SignInInteractiveAsync`: `PkceGoogleAuthorizationCodeFlow` (no DataStore) → `LocalServerCodeReceiver`
(loopback, system browser, `prompt=consent select_account`, `access_type=offline`, optional `login_hint`) → code exchange
with PKCE verifier → `GoogleJsonWebSignature.ValidateAsync(id_token, Audience = [clientId])` (signature against Google
certs, issuer, audience, expiry) → `payload.Email` must be non-empty and `EmailVerified == true`, else
`AuthenticationFailedException(Unknown, code "email_not_verified" / "invalid_id_token")`. The hint is never used as the
address. The ID token is removed before the token response is stored (JSON via `NewtonsoftJsonSerializer`, encrypted by
the credential store).

## Deviations / decisions the contract leaves open
1. `AccountService.ReauthenticateAsync` (`AccountService.cs:119`): the grant of the wrong account is signed out **only
   if that address is not itself a stored account** – otherwise a legitimate fresh grant of another MailWarden account
   would be destroyed. Case 25 (`other@example.test` not stored) is unaffected. Reviewer: accept or revert to literal.
2. `AccountService.RemoveAccountAsync` (`AccountService.cs:142`): account without registered token provider is deleted
   without sign-out (removal never blocked).
3. `AuthConfiguration.Load` with malformed JSON / non-object / non-string value throws at **Load** time with
   `Provider = GenericImap` (`AuthConfiguration.cs:157`) – the file is shared by both providers, the contract ctor needs
   one. Message names file and line/byte, not the content; no inner `JsonException` (its message may quote content).
4. `DpapiCredentialStore.ListKeysAsync` validates the prefix with the key rules (`DpapiCredentialStore.cs:134`), so
   `""`/`null` prefix → ArgumentException/ArgumentNullException (case 4 "every method").
5. Unreadable `msal.cache` (`MicrosoftTokenProvider.cs:169`): silent → `InteractionRequired` (code
   `token_cache_unreadable`); interactive sign-in proceeds and overwrites the cache; sign-out is a no-op.
   Unreadable Google grant → `InteractionRequired` (code `stored_grant_unreadable`).
6. `ConnectionProbe.RunAsync` throws `ArgumentException` when `token.Username` ≠ account address
   (`ConnectionProbe.cs:55`, programming error); IMAP and SMTP are always both run; with both failing, Error is one
   sentence joined by `;` (`ConnectionProbe.cs:90`). Error texts are own wording only (no server/library messages).
7. Cancellation: MSAL/Google exceptions raised while `ct` is cancelled are rethrown as `OperationCanceledException`.
   Credential-store IO errors (not crypto) propagate unwrapped.
8. `AuthConfiguration` trims values; empty/whitespace values (file or env) count as missing.

## Verified vs. untested
- Verified: build `-warnaserror` green only.
- Untested: all cases 1–30. Specific risks to check: MSAL `DeserializeMsalV3(empty)` behaviour, MailKit
  `GetFoldersAsync` returning `INBOX` and Gmail `[Gmail]` with `NonExistent`, Google `RefreshTokenAsync` with a null
  DataStore (relies on the library skipping storage), `IsStale` refresh window.

## AuthProbe usage for Dennis
Not available yet (see "Done / remaining").

## Open questions
- `AuthConfiguration.cs:157` – which `Provider` for a malformed shared config file (chosen: GenericImap)?
- `AccountService.cs:119` – keep the guard for deviation 1?
- `GoogleTokenProvider.cs:102` – interactive sign-in has no own timeout; a Google error page that never redirects
  (e.g. 403 of an unpublished app) waits until the caller cancels. UI (AP-04) should offer cancel / a timeout.
- `GoogleTokenProvider.cs:157` – key collisions (`a@b.c` vs `a_b.c`) and addresses > 121 chars (key limit 128) are not
  handled; Google sign-out does not revoke the grant server-side.
- `DpapiCredentialStore` keys differing only in case map to the same file (NTFS case-insensitive).
- Role prompt says "keine Commits", coordinator/project pipeline requests local commits → followed the coordinator.
