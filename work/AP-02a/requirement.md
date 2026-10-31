---
id: AP-02a
slug: accounts-auth
title: Credential store (DPAPI), OAuth sign-in for Microsoft and Google, provider profiles, folder role resolution, account service
test-scope: S2
test-filter: "Category=feature:accounts|Category=smoke"
base: feat/ap-01-skeleton-storage (builder) / test/ap-01-skeleton-storage (tester)
status: ready (2026-10-31)
---

# AP-02a – Accounts and authentication (without UI)

## Goal
Turn the proven AP-00 spike into production code: encrypted credential storage, OAuth token providers for Microsoft
(MSAL) and Google, provider profiles (servers, hidden folders), the mapping of server folders to `FolderRole`, an
account service that adds/removes accounts and hands out access tokens, and a connection probe (IMAP + SMTP login).
The UI for this comes with AP-04 ("AP-02b").

## Context
- AP-00 spike (`_scratch\auth-spike\AuthSpike\Program.cs` in the **main checkout**
  `K:\Projekte\Windows-Tools\MailWarden`, git-ignored; read-only reference for the Builder) proved: MSAL public
  client with authority `consumers`, redirect `http://localhost`, scopes
  `https://outlook.office.com/IMAP.AccessAsUser.All` and `https://outlook.office.com/SMTP.Send`; Google installed-app
  flow with scope `https://mail.google.com/`; XOAUTH2 against `outlook.office365.com:993` / `smtp-mail.outlook.com:587`
  and `imap.gmail.com:993` / `smtp.gmail.com:587`; token caches DPAPI-encrypted.
- AP-00 findings: Outlook.com does **not** advertise SPECIAL-USE/XLIST but returns `\Sent \Junk \Trash \Drafts` in
  LIST; it has no `\Archive` flag (folder named `Archive`). Outlook.com extra folders without mail relevance: `Notes`,
  `Outbox`, `Scheduled`, `Synchronisierungsprobleme` (+ children). Gmail advertises SPECIAL-USE; `[Gmail]` is a
  `\NonExistent` container; `[Gmail]/All Mail` = `\All`, `[Gmail]/Important`, `[Gmail]/Starred` are virtual; there is
  a user folder `Junk-E-Mail` that is **not** the spam folder. The Google 403 in "Testing" status ended without a
  clean error line in the spike → mapped error required here.
- AP-01 contract: `Account`, `MailProvider`, `FolderRole`, `IAccountStore` (namespace `MailWarden.Core.Model` /
  `.Storage`). Do not change AP-01 files except `MailWarden.Infrastructure.csproj` (package references).
- Data folder: `DataPaths.Root` (AP-01). Credentials live in `<Root>\credentials\`, auth configuration in
  `<Root>\auth-config.json`.

## Non-goals
- No UI, no sync, no message download (AP-03), no sending (AP-05).
- No Gmail API / Graph usage; IMAP/SMTP only.
- No automatic deletion of the account's local mail files on removal (AP-03 owns the mail data; noted as open point).
- No generic IMAP password accounts yet (`GenericImap` is rejected with NotSupportedException where a provider is
  required).

## Packages (approved in PLAN.md)
Infrastructure: `Microsoft.Identity.Client`, `Google.Apis.Auth`, `MailKit`, `System.Security.Cryptography.ProtectedData`
(latest stable). Nothing else.

## Contract (binding)

### `MailWarden.Core.Accounts`
```csharp
/// Secret storage. Keys: 1–128 chars of [A-Za-z0-9._-]; anything else → ArgumentException.
public interface ICredentialStore
{
    Task<byte[]?> ReadAsync(string key, CancellationToken ct = default);          // missing → null
    Task WriteAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct = default);   // atomic, overwrites
    Task DeleteAsync(string key, CancellationToken ct = default);                 // missing → no-op
    Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct = default);  // ordinal, sorted
}

/// Stored secret cannot be decrypted or is corrupt. Never contains secret bytes in its message.
public sealed class CredentialStoreException : Exception { ctor(string message, Exception? inner = null) }

public enum AuthFailureReason { UserCancelled, AccessDenied, InteractionRequired, Configuration, Network, Unknown }

/// All sign-in/token failures surface as this type. Message: one English sentence for the user, never a token,
/// secret or full exception dump. ProviderErrorCode: raw code from MSAL/Google (e.g. "access_denied"), may be null.
public class AuthenticationFailedException : Exception
{
    public AuthenticationFailedException(MailProvider provider, AuthFailureReason reason, string message,
        string? providerErrorCode = null, Exception? inner = null);
    public MailProvider Provider { get; }
    public AuthFailureReason Reason { get; }
    public string? ProviderErrorCode { get; }
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresOn, string Username);
// ToString() must not contain Value (override; tested).

public interface ITokenProvider
{
    MailProvider Provider { get; }
    /// Silent only. No cached grant → AuthenticationFailedException(Reason = InteractionRequired).
    Task<AccessToken> GetAccessTokenSilentAsync(string username, CancellationToken ct = default);
    /// Opens the system browser. Username in the result is the account actually signed in (not the hint).
    Task<AccessToken> SignInInteractiveAsync(string? loginHint, CancellationToken ct = default);
    /// Removes every cached grant of this user. Unknown user → no-op.
    Task SignOutAsync(string username, CancellationToken ct = default);
}

/// Server-side folder flags in provider-neutral form (mapped from MailKit by Infrastructure).
[Flags]
public enum ServerFolderFlags
{
    None = 0, Inbox = 1, Sent = 2, Drafts = 4, Junk = 8, Trash = 16, Archive = 32, All = 64, Flagged = 128,
    Important = 256, NoSelect = 512, NonExistent = 1024
}

public sealed record FolderClassification(FolderRole Role, bool Hidden);

public static class FolderRoleResolver
{
    /// Rules in this order:
    /// 1. NoSelect or NonExistent → (None, Hidden).
    /// 2. Flags Inbox→Inbox, Sent→Sent, Drafts→Drafts, Junk→Junk, Trash→Trash, Archive→Archive, All→(All, Hidden).
    ///    Flagged or Important without one of the above → (None, Hidden) (Gmail virtual folders).
    /// 3. Name "INBOX" (case-insensitive, IMAP rule) → Inbox.
    /// 4. App-owned folders, exact top-level names: "Screener" → Screener, "Quarantine" → Quarantine.
    /// 5. Microsoft only: top-level "Archive" → Archive; "Notes", "Outbox", "Scheduled", "Synchronisierungsprobleme"
    ///    and its children ("Synchronisierungsprobleme/…") → (None, Hidden). The server "Outbox" is NEVER
    ///    FolderRole.Outbox (that role is the local send queue).
    /// 6. Everything else → (None, visible). In particular a user folder named "Junk-E-Mail", "Spam" or "Sent" without
    ///    flags stays None – roles never come from names except rules 3–5.
    /// separator: the server's hierarchy delimiter ('/' for both providers). fullName null/empty → ArgumentException.
    public static FolderClassification Resolve(MailProvider provider, string fullName, char separator, ServerFolderFlags flags);
}

/// Orchestrates account lifecycle on top of IAccountStore and the token providers.
public sealed class AccountService
{
    /// tokenProviders: at most one per MailProvider (duplicate → ArgumentException).
    public AccountService(IAccountStore accountStore, IEnumerable<ITokenProvider> tokenProviders);

    /// Interactive sign-in; creates the Account (new Guid, DisplayName = EmailAddress = signed-in username) or, if an
    /// account with that address exists (case-insensitive), returns the existing one unchanged (re-consent).
    /// GenericImap or provider without ITokenProvider → NotSupportedException.
    public Task<Account> AddAccountAsync(MailProvider provider, string? loginHint, CancellationToken ct = default);

    /// Silent token for a stored account. Unknown id → ArgumentException. InteractionRequired propagates.
    public Task<AccessToken> GetAccessTokenAsync(Guid accountId, CancellationToken ct = default);

    /// Interactive re-sign-in for an existing account; the signed-in username must equal the account address
    /// (case-insensitive), otherwise AuthenticationFailedException(AccessDenied, "signed in as other account") and the
    /// new grant is signed out again.
    public Task<AccessToken> ReauthenticateAsync(Guid accountId, CancellationToken ct = default);

    /// SignOutAsync on the provider, then IAccountStore.DeleteAsync. Unknown id → no-op.
    public Task RemoveAccountAsync(Guid accountId, CancellationToken ct = default);
}
```

### `MailWarden.Infrastructure.Security`
```csharp
/// One file per key: <directory>\<key>.bin, DPAPI CurrentUser with fixed additional entropy
/// "MailWarden.Credentials.v1" (UTF-8). Atomic write (temp file + move). Undecryptable file → CredentialStoreException.
public sealed class DpapiCredentialStore(string directory) : ICredentialStore
```

### `MailWarden.Infrastructure.Auth`
```csharp
/// Client configuration. Load order: environment variables MAILWARDEN_MS_CLIENT_ID, MAILWARDEN_GOOGLE_CLIENT_ID,
/// MAILWARDEN_GOOGLE_CLIENT_SECRET override values from <data root>\auth-config.json
/// ({"microsoftClientId": "...", "googleClientId": "...", "googleClientSecret": "..."}). Missing file is allowed.
/// A provider whose values are missing is only an error when it is used: Require*() →
/// AuthenticationFailedException(Configuration) naming the missing key (not its value).
public sealed record AuthConfiguration(string? MicrosoftClientId, string? GoogleClientId, string? GoogleClientSecret)
{
    public static AuthConfiguration Load(string authConfigPath, Func<string, string?> getEnvironmentVariable);
    public string RequireMicrosoftClientId();
    public (string ClientId, string ClientSecret) RequireGoogleClient();
}

/// MSAL public client (authority consumers, redirect http://localhost, scopes as in Context). Token cache serialised
/// into ICredentialStore key "msal.cache".
public sealed class MicrosoftTokenProvider(AuthConfiguration configuration, ICredentialStore credentialStore) : ITokenProvider

/// Google installed-app flow, scopes "https://mail.google.com/", "openid", "email". The signed-in address is taken from
/// the validated ID token (not from the hint). Token stored in ICredentialStore key "google.<lower-case address>"
/// (characters outside the key alphabet replaced by '_').
public sealed class GoogleTokenProvider(AuthConfiguration configuration, ICredentialStore credentialStore) : ITokenProvider

/// Pure mapping functions, unit-testable.
public static class AuthErrorMapping
{
    /// MSAL error codes: "authentication_canceled" → UserCancelled; "access_denied" → AccessDenied;
    /// "invalid_grant", "interaction_required", "user_null", "no_tokens_found", "no_account_for_login_hint" →
    /// InteractionRequired; "invalid_client", "unauthorized_client", "invalid_request" → Configuration;
    /// MsalServiceException with HttpRequestException inner or "request_timeout"/"service_not_available" → Network;
    /// else Unknown.
    public static AuthFailureReason FromMsalErrorCode(string? errorCode);
    /// Google OAuth error values: "access_denied" → AccessDenied (covers the 403 of unpublished apps);
    /// "invalid_grant" → InteractionRequired; "invalid_client", "unauthorized_client", "redirect_uri_mismatch" →
    /// Configuration; "user_cancelled"/"canceled" → UserCancelled; else Unknown.
    public static AuthFailureReason FromGoogleError(string? error);
}
```

### `MailWarden.Infrastructure.Providers`
```csharp
public enum TransportSecurity { SslOnConnect, StartTls }
public sealed record ServerEndpoint(string Host, int Port, TransportSecurity Security);
public sealed record ProviderProfile(MailProvider Provider, ServerEndpoint Imap, ServerEndpoint Smtp);

public static class ProviderProfiles
{
    public static ProviderProfile Microsoft { get; }   // outlook.office365.com:993 SslOnConnect / smtp-mail.outlook.com:587 StartTls
    public static ProviderProfile Google { get; }      // imap.gmail.com:993 SslOnConnect / smtp.gmail.com:587 StartTls
    /// GenericImap → NotSupportedException.
    public static ProviderProfile For(MailProvider provider);
    /// Domain (case-insensitive) hotmail.*, outlook.*, live.*, msn.com, windowslive.com → Microsoft;
    /// gmail.com, googlemail.com → Google; else null. Invalid address → ArgumentException.
    public static MailProvider? DetectFromAddress(string emailAddress);
}

/// Maps MailKit FolderAttributes to ServerFolderFlags (all flags listed there, nothing else).
public static class MailKitFolderFlags { public static ServerFolderFlags From(MailKit.FolderAttributes attributes); }

public sealed record FolderProbe(string FullName, ServerFolderFlags Flags, FolderClassification Classification);
public sealed record ConnectionProbeResult(bool ImapOk, bool SmtpOk, IReadOnlyList<FolderProbe> Folders, string? Error);

/// Logs in via XOAUTH2 to IMAP (lists all folders with flags + FolderRoleResolver) and SMTP (auth only, no mail).
/// Network/auth errors are returned in Error (one English sentence, no token), never thrown; cancellation is thrown.
public sealed class ConnectionProbe { public Task<ConnectionProbeResult> RunAsync(Account account, AccessToken token, CancellationToken ct = default); }
```

## Expected behaviour (basis for the tests)
Feature tag `feature:accounts`. Interactive OAuth and real servers are **manual** (marked M). Automated tests use temp
directories, the real AP-01 SQLite stores (in-memory) and a hand-written fake `ITokenProvider` – no mocks of
`ICredentialStore` or the stores.

| # | Case | Expected |
|---|---|---|
| 1 | `DpapiCredentialStore` write → read | identical bytes; file `<dir>\<key>.bin` exists; its bytes do **not** contain the plain secret |
| 2 | Overwrite same key | new value returned; no `*.tmp` left |
| 3 | Read / delete missing key | null / no-op |
| 4 | Keys `../x`, `a/b`, `a:b`, `""`, 129 chars, `null` | ArgumentException (ArgumentNullException for null) on every method |
| 5 | `ListKeysAsync("google.")` with keys `google.a`, `google.b`, `msal.cache` | `["google.a", "google.b"]` |
| 6 | File content replaced with random bytes | `ReadAsync` → CredentialStoreException, message without the bytes |
| 7 | Second `DpapiCredentialStore` instance on the same directory | reads what the first wrote |
| 8 | `FolderRoleResolver`, Microsoft list from AP-00 (Inbox flag, `Sent`/`Junk`/`Drafts`/`Deleted` with flags, `Archive` + children without flags, `Notes`, `Outbox`, `Scheduled`, `Synchronisierungsprobleme/Konflikte`) | Inbox, Sent, Junk, Drafts, Trash(Deleted), Archive (top-level only; `Archive/Steuer` → None visible), Notes/Outbox/Scheduled/Synchronisierungsprobleme* → None Hidden |
| 9 | Google list from AP-00 (`INBOX` with Inbox flag, `[Gmail]` NonExistent, `[Gmail]/All Mail` All, `[Gmail]/Spam` Junk, `[Gmail]/Bin` Trash, `[Gmail]/Sent Mail` Sent, `[Gmail]/Drafts` Drafts, `[Gmail]/Important` Important, `[Gmail]/Starred` Flagged, `Junk-E-Mail` none, `Auto` none) | Inbox; `[Gmail]` None Hidden; All Hidden; Junk; Trash; Sent; Drafts; Important/Starred None Hidden; `Junk-E-Mail` **None visible**; `Auto` None visible |
| 10 | `Outbox` on Microsoft | never FolderRole.Outbox |
| 11 | `Screener`, `Quarantine` top-level (both providers) → Screener / Quarantine; `Archive/Screener` → None | |
| 12 | Name `inbox` without flags | Inbox; `Spam` / `Sent` without flags (Google) → None |
| 13 | `Resolve` with empty name | ArgumentException |
| 14 | `ProviderProfiles.DetectFromAddress`: `a@hotmail.de`, `a@Outlook.com`, `a@live.de`, `a@msn.com` → Microsoft; `a@googlemail.com`, `a@GMAIL.com` → Google; `a@example.test` → null; `no-at` → ArgumentException | |
| 15 | `ProviderProfiles.For(GenericImap)` → NotSupportedException; Microsoft/Google endpoints exactly as in the contract | |
| 16 | `MailKitFolderFlags.From(Inbox | Junk | NoSelect)` → `Inbox | Junk | NoSelect`; `HasChildren` alone → None | |
| 17 | `AuthErrorMapping` for every listed code; unknown/null → Unknown | |
| 18 | `AuthConfiguration.Load`: file only; env only; env overrides file; missing file → all null; `RequireMicrosoftClientId()` with null → AuthenticationFailedException(Configuration), message names `MAILWARDEN_MS_CLIENT_ID`; malformed JSON → AuthenticationFailedException(Configuration) | |
| 19 | `AccessToken.ToString()` | does not contain the token value |
| 20 | `AccountService.AddAccountAsync` with fake provider returning username `user_a@example.test` | account stored, Id ≠ Guid.Empty, DisplayName = EmailAddress = username |
| 21 | Add again, provider returns `USER_A@example.test` | same account returned, store count unchanged |
| 22 | `AddAccountAsync(GenericImap)` and provider without registered token provider | NotSupportedException |
| 23 | Two token providers for the same MailProvider | ctor ArgumentException |
| 24 | `GetAccessTokenAsync(unknown id)` → ArgumentException; fake throws InteractionRequired → propagates unchanged | |
| 25 | `ReauthenticateAsync` where fake signs in `other@example.test` | AuthenticationFailedException(AccessDenied); fake's SignOut was called for `other@example.test`; account unchanged |
| 26 | `RemoveAccountAsync` | fake SignOut called with the account address, account gone from store; unknown id → no-op, no SignOut |
| 27 | **smoke**: temp data root → `DpapiCredentialStore` + in-memory SQLite + fake provider → add account → get token → remove account → store empty, temp root removable | |
| 28 M | `AuthProbe` console (Builder, under `_scratch\`, git-ignored) using the real providers: add both hotmail.de accounts and the googlemail account, run `ConnectionProbe` | ImapOk + SmtpOk; folder classification matches cases 8/9 |
| 29 M | Second run | no browser; token from cache (credentials folder contains only `.bin` files, no plain JSON) |
| 30 M | Cancel the browser login / deny consent | AuthenticationFailedException with UserCancelled / AccessDenied and a readable message; probe exits cleanly |

Positive controls: case 1 must fail if the store writes plain text; case 9 must fail if roles are derived from the
name `Junk-E-Mail`; case 25 must fail if the mismatched grant is not signed out.

## Done criteria
- Builder: files under `src\MailWarden.Core\Accounts\`, `src\MailWarden.Infrastructure\{Security,Auth,Providers}\`,
  package references in `MailWarden.Infrastructure.csproj` only; build `-warnaserror` green; the `AuthProbe` console
  for cases 28–30 under `_scratch\auth-probe\` reading `auth-config.json` from a data root given as argument;
  `work\AP-02a\impl-notes.md` (STEP-RESULT, files, deviations, verified vs. untested, open questions).
- Tester: tests in `tests\MailWarden.Infrastructure.Tests\Accounts\` (existing project from AP-01; add package
  references only if needed, none expected), `TestSupport\FakeTokenProvider.cs`, `work\AP-02a\test-plan.md`,
  `tests\test-map.json` entries for the new source folders → `["@feature:accounts"]`.
- Planner: merge, S2 via Tester, review + **security review** (credentials, tokens, auth – trigger in the security
  profile), Dennis runs cases 28–30.
