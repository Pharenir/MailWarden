---
id: AP-01
slug: skeleton-storage
title: Solution skeleton, Core model, SQLite + EML storage, WinUI 3 empty app
test-scope: S2
test-filter: "Category=feature:storage|Category=smoke"
status: implement.1 / author.0 (2026-10-31)
---

# AP-01 – Solution skeleton, Core model, storage

## Goal
Create the solution with the three layers from `PLAN.md` (Core ← Infrastructure ← App), the domain model, the
SQLite schema with migration and full-text search, the EML file store, and an empty WinUI 3 window that proves the
toolchain works without Visual Studio. Everything later (sync, classifier, UI) builds on these types, so the
**contract below is binding**: names, namespaces, signatures and behaviour. Deviations are reported in
`impl-notes.md`, not silently made.

## Context
- No application code exists yet. Repository root: `K:\Projekte\Windows-Tools\MailWarden`.
- `PLAN.md` – architecture, four-level classifier, group views; section "Sollverhalten AP-01".
- AP-00 findings (`docs\auth-setup.md`, local only): Outlook.com does not advertise SPECIAL-USE but returns
  `\Sent`, `\Junk`, `\Trash`, `\Drafts` in LIST; Gmail has a *user* folder named `Junk-E-Mail` that is **not** the
  spam folder. Therefore folder roles are an explicit enum stored per folder; nothing ever derives a role from a name
  inside the storage layer.
- Toolchain: .NET SDK 9.0.320, **no Visual Studio**, no `dotnet new winui` templates, Windows SDK 10.0.26100 headers
  present. The WinUI 3 project must be hand-written (csproj + App.xaml + MainWindow.xaml) and build with plain
  `dotnet build`.
- Global rules apply (`~/.claude/CLAUDE.md`): English identifiers and comments, fail fast, no placeholders,
  speaking names, docstrings on public members, UTF-8 without BOM, no `git add -A`.

## Non-goals
- No IMAP/SMTP, no OAuth, no sync, no credential storage (AP-02/AP-03).
- No classifier, rules, contacts logic beyond the `Contact` record and its table (AP-06/AP-07).
- No real UI beyond the empty window; no MVVM framework decision yet.
- No installer, no `.ico` (AP-13).
- No multi-threaded access to the database (single caller; a command queue comes with AP-03).

## Repository layout to create
```
MailWarden.sln
Directory.Build.props                 shared settings for all projects (see below)
.editorconfig
src\MailWarden.Core\MailWarden.Core.csproj                 net9.0          (no package references)
src\MailWarden.Infrastructure\MailWarden.Infrastructure.csproj  net9.0-windows  (Microsoft.Data.Sqlite)
src\MailWarden.App\MailWarden.App.csproj                   net9.0-windows10.0.19041.0, WinUI 3
tests\MailWarden.Infrastructure.Tests\...                  (Tester; xUnit; references Core + Infrastructure)
tests\test-map.json                                        (Tester)
work\AP-01\impl-notes.md                                   (Builder)
work\AP-01\test-plan.md, test-result.md                    (Tester)
```
`Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`,
`EnableNETAnalyzers=true`, `AnalysisLevel=latest`, `LangVersion=latest`, `Deterministic=true`,
`GenerateDocumentationFile=true` for `src\**` only (tests excluded via condition on project name or a second
`Directory.Build.props` under `tests\` that imports the parent and switches it off).

The Builder adds the two `src` projects and the App to the solution; test projects are added by the planner at
verify (`dotnet sln add`). The Builder must **not** create anything under `tests\`.

## Contract (binding)

### `MailWarden.Core` – namespace `MailWarden.Core.Model`
```csharp
public enum MailProvider { Microsoft, Google, GenericImap }

/// Role of a folder. Stored explicitly per folder; never derived from a name inside storage.
/// Outbox exists only locally (unsent queue). All = Gmail "All Mail" (never part of group views).
public enum FolderRole { None, Inbox, Sent, Drafts, Junk, Trash, Archive, Screener, Quarantine, All, Outbox }

[Flags]
public enum MessageFlags { None = 0, Seen = 1, Answered = 2, Flagged = 4, Deleted = 8, Draft = 16 }

public enum ContactSource { Manual, Imported, AutoCollected }

public sealed record Account(Guid Id, string DisplayName, string EmailAddress, MailProvider Provider);

/// Id == 0 means "not stored yet"; stores return a copy with the database id.
public sealed record MailFolder(long Id, Guid AccountId, string FullName, FolderRole Role, uint UidValidity);

/// Id == 0 means "not stored yet". EmlPath is relative to DataPaths.MailRoot (forward slashes), null = body not
/// downloaded. TextPreview is the plain-text start of the body, at most 200 characters (caller's responsibility).
public sealed record MailMessage(
    long Id,
    Guid AccountId,
    long FolderId,
    uint Uid,
    string? MessageId,
    string Subject,
    string FromAddress,
    string FromName,
    DateTimeOffset Date,
    MessageFlags Flags,
    bool HasAttachments,
    long Size,
    string? EmlPath,
    string TextPreview);

public sealed record Contact(long Id, string EmailAddress, string DisplayName, ContactSource Source);
```

### `MailWarden.Core` – namespace `MailWarden.Core.Storage`
```csharp
/// Base type for all storage failures that are not programming errors.
public class StorageException : Exception { ctor(string message); ctor(string message, Exception inner) }

/// Database schema is newer than this build understands. Message contains both numbers.
public sealed class SchemaVersionException : StorageException { int FoundVersion; int SupportedVersion; }

/// EML file referenced by a message does not exist. Message contains the relative path.
public sealed class EmlNotFoundException : StorageException { string RelativePath; }

public interface IAccountStore
{
    Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default);          // ordered by DisplayName
    Task<Account?> GetAsync(Guid id, CancellationToken ct = default);
    Task UpsertAsync(Account account, CancellationToken ct = default);                 // key: Id
    Task DeleteAsync(Guid id, CancellationToken ct = default);                         // cascades folders + messages; unknown id = no-op
}

public interface IFolderStore
{
    Task<IReadOnlyList<MailFolder>> ListAsync(Guid accountId, CancellationToken ct = default);   // ordered by FullName
    Task<MailFolder?> GetAsync(long id, CancellationToken ct = default);
    Task<MailFolder> UpsertAsync(MailFolder folder, CancellationToken ct = default);   // key: (AccountId, FullName); returns stored copy with Id
    Task DeleteAsync(long id, CancellationToken ct = default);                         // cascades messages; unknown id = no-op
}

/// Filter for list views. Role selects across all accounts unless AccountId narrows it; FolderId wins over Role.
public sealed record MessageQuery(
    FolderRole? Role = null,
    Guid? AccountId = null,
    long? FolderId = null,
    int Limit = 100,
    int Offset = 0);

public interface IMessageStore
{
    /// Insert or update by key (FolderId, Uid) – per D3; AccountId must equal the folder's account. bodyText feeds full-text search only (null = keep existing
    /// indexed text on update, empty on insert). Returns the stored copy with Id.
    Task<MailMessage> UpsertAsync(MailMessage message, string? bodyText, CancellationToken ct = default);
    Task<MailMessage?> GetAsync(long id, CancellationToken ct = default);
    Task<MailMessage?> GetByUidAsync(long folderId, uint uid, CancellationToken ct = default);
    /// Newest first (Date desc, then Id desc).
    Task<IReadOnlyList<MailMessage>> ListAsync(MessageQuery query, CancellationToken ct = default);
    /// Full-text search over Subject, FromAddress, FromName and indexed body text; case-insensitive; newest first.
    /// Whitespace-only text → ArgumentException.
    Task<IReadOnlyList<MailMessage>> SearchAsync(string text, int limit = 100, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);                                   // unknown id = no-op
    Task DeleteByUidsAsync(long folderId, IReadOnlyCollection<uint> uids, CancellationToken ct = default);
}

public interface IEmlStore
{
    /// "accounts/{accountId:D}/folders/{folderId}/{uid}.eml" – forward slashes, relative to the mail root.
    string RelativePathFor(Guid accountId, long folderId, uint uid);
    /// Creates directories; writes atomically (temp file + move); overwrites.
    Task WriteAsync(string relativePath, Stream content, CancellationToken ct = default);
    /// Throws EmlNotFoundException when missing. Caller disposes the stream.
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default);
    bool Exists(string relativePath);
    Task DeleteAsync(string relativePath, CancellationToken ct = default);   // missing = no-op
}
```

### `MailWarden.Infrastructure` – namespace `MailWarden.Infrastructure.Storage`
```csharp
/// Resolves everything below the data root. Rejects rooted paths and ".." segments with ArgumentException.
public sealed class DataPaths
{
    public DataPaths(string rootDirectory);
    public string Root { get; }
    public string DatabasePath { get; }     // <Root>\mailwarden.db
    public string MailRoot { get; }         // <Root>\mail
    public string LogRoot { get; }          // <Root>\logs
    public string ResolveMailPath(string relativePath);   // MailRoot + relativePath (normalised), validated
}

/// Owns one open SqliteConnection (desktop app, single caller). Runs migrations on InitializeAsync.
public sealed class SqliteDatabase : IAsyncDisposable
{
    public const int CurrentSchemaVersion = 1;
    public SqliteDatabase(string connectionString);
    public static SqliteDatabase ForFile(string databasePath);   // "Data Source=<path>", enables WAL
    public static SqliteDatabase InMemory();                      // "Data Source=:memory:"
    public SqliteConnection Connection { get; }                   // open after InitializeAsync, else InvalidOperationException
    public int SchemaVersion { get; }                             // version after InitializeAsync
    public Task InitializeAsync(CancellationToken ct = default);  // PRAGMA foreign_keys=ON; migrate 0..Current; found > Current → SchemaVersionException
}

public sealed class SqliteAccountStore(SqliteDatabase database) : IAccountStore
public sealed class SqliteFolderStore(SqliteDatabase database) : IFolderStore
public sealed class SqliteMessageStore(SqliteDatabase database) : IMessageStore
public sealed class FileEmlStore(DataPaths paths) : IEmlStore
```

### Schema v1 (SQLite)
- `schema_version(version INTEGER NOT NULL)` – exactly one row. Missing table = version 0 (fresh database).
- `accounts(id TEXT PK, display_name TEXT NOT NULL, email_address TEXT NOT NULL UNIQUE COLLATE NOCASE, provider INTEGER NOT NULL)`
- `folders(id INTEGER PK AUTOINCREMENT, account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE, full_name TEXT NOT NULL, role INTEGER NOT NULL, uid_validity INTEGER NOT NULL, UNIQUE(account_id, full_name))`
- `folders` additionally `UNIQUE(id, account_id)` (D3).
- `messages(id INTEGER PK AUTOINCREMENT, account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE, folder_id INTEGER NOT NULL, uid INTEGER NOT NULL, message_id TEXT NULL, subject TEXT NOT NULL, from_address TEXT NOT NULL, from_name TEXT NOT NULL, date TEXT NOT NULL /* ISO 8601 with offset, round-trips DateTimeOffset exactly */, flags INTEGER NOT NULL, has_attachments INTEGER NOT NULL, size INTEGER NOT NULL, eml_path TEXT NULL, text_preview TEXT NOT NULL, FOREIGN KEY (folder_id, account_id) REFERENCES folders (id, account_id) ON DELETE CASCADE, UNIQUE(folder_id, uid))` + expression indexes `(folder_id, julianday(date) DESC, id DESC)` and `(julianday(date) DESC, id DESC)` (D3/D4; contract text updated 2026-10-31)
- `messages_fts` – FTS5 virtual table `(subject, from_address, from_name, body_text)` with `rowid` = `messages.id`, maintained by `SqliteMessageStore` (insert/update/delete in the same transaction as the row). Query with `MATCH` on a sanitised term list (each whitespace-separated token quoted as `"token"*`), so user input can never break the FTS syntax.
- `contacts(id INTEGER PK AUTOINCREMENT, email_address TEXT NOT NULL UNIQUE COLLATE NOCASE, display_name TEXT NOT NULL, source INTEGER NOT NULL)` – table only, no store in AP-01.
- Migrations: ordered list `(int version, string sql)` in one class `SchemaMigrations`; each runs in a transaction and writes `schema_version` at the end.

### `MailWarden.App` (WinUI 3)
- csproj: `TargetFramework net9.0-windows10.0.19041.0`, `TargetPlatformMinVersion 10.0.17763.0`, `UseWinUI true`,
  `WindowsPackageType None`, `WindowsAppSDKSelfContained true`, `Platforms x64`, `RuntimeIdentifier win-x64`,
  packages `Microsoft.WindowsAppSDK` (latest stable 1.x) and `Microsoft.Windows.SDK.BuildTools`.
  Project reference to Infrastructure (and thereby Core).
- `App.xaml`/`App.xaml.cs`: creates `MainWindow`; `MainWindow.xaml`: title "MailWarden", a `TextBlock`
  "MailWarden – AP-01 skeleton"; theme follows Windows (default `RequestedTheme`, Mica/backdrop optional).
- On start it computes `DataPaths` from `MAILWARDEN_DATA_DIR` or `<exe dir>\data` and ensures the directory exists –
  nothing else. No database access from the UI in AP-01.
- If WinUI 3 cannot be built with the CLI after reasonable effort (max. 2 attempts with different package versions),
  stop and report in `impl-notes.md` with the exact error; do **not** switch to WPF on your own.

## Expected behaviour (basis for the tests)
Feature tag `feature:storage` unless noted. "Fresh database" = `SqliteDatabase.InMemory()` + `InitializeAsync()`.

| # | Case | Expected |
|---|---|---|
| 1 | Fresh database | `SchemaVersion == CurrentSchemaVersion`; `sqlite_master` contains `schema_version`, `accounts`, `folders`, `messages`, `messages_fts`, `contacts` |
| 2 | `InitializeAsync` on a file database that was already initialised (second `SqliteDatabase` instance on the same file) | no error, version unchanged, data preserved |
| 3 | File database containing `schema_version` = `CurrentSchemaVersion + 1` | `InitializeAsync` throws `SchemaVersionException`; `FoundVersion`/`SupportedVersion` set; message contains both numbers |
| 4 | `Connection` before `InitializeAsync` | `InvalidOperationException` |
| 5 | Account upsert → `GetAsync` returns equal record; upsert same Id with new DisplayName → updated, `ListAsync` count unchanged; `ListAsync` ordered by DisplayName | |
| 6 | Account upsert with same EmailAddress but different Id | `StorageException` (unique violation surfaces, not swallowed) |
| 7 | Folder upsert returns Id > 0; same (AccountId, FullName) again with other Role/UidValidity → same Id, values updated; same FullName under a second account → different Id | |
| 8 | Folder upsert for unknown AccountId | `StorageException` |
| 9 | Message upsert returns Id > 0; `GetAsync`/`GetByUidAsync` return a record equal to the stored copy, including `Date` with its original offset (e.g. `+02:00`) and `MessageId == null` | |
| 10 | Message upsert with same (AccountId, FolderId, Uid) and changed Flags/Subject | same Id, values updated, total count unchanged; search finds new subject, not old |
| 11 | Message upsert for unknown FolderId | `StorageException` |
| 12 | `ListAsync(Role: Inbox)` with two accounts each having an Inbox folder and a Junk folder | returns only Inbox messages of both accounts, newest `Date` first, tie → higher Id first |
| 13 | `ListAsync(Role: Inbox, AccountId: A)` | only account A's Inbox messages |
| 14 | `ListAsync(FolderId: X, Role: Junk)` where X is an Inbox folder | FolderId wins: messages of folder X |
| 15 | `ListAsync(Limit: 2, Offset: 2)` on 5 messages | items 3 and 4 of the newest-first order |
| 16 | `ListAsync(new MessageQuery())` | all messages, newest first |
| 17 | `SearchAsync` by a word in the subject, by a word only in `bodyText`, by the from address, mixed case | finds the message each time; unknown word → empty |
| 18 | `SearchAsync("  ")` / `""` | `ArgumentException` |
| 19 | `SearchAsync` with FTS syntax characters in the input (`"foo" OR bar*`, `(`) | no exception; treated as plain words |
| 20 | `DeleteAsync(messageId)` | `GetAsync` → null, `SearchAsync` no longer finds it; unknown id → no-op |
| 21 | `DeleteByUidsAsync(folderId, [2, 3])` with uids 1..4 present | exactly 1 and 4 remain; empty collection → no-op |
| 22 | `IAccountStore.DeleteAsync` | its folders and messages are gone (`IFolderStore.ListAsync` empty, `GetAsync` of a former message null); other accounts untouched |
| 23 | `IFolderStore.DeleteAsync` | its messages gone, account intact |
| 24 | `RelativePathFor(accountId, 7, 42)` | `accounts/<guid lower-case D format>/folders/7/42.eml` |
| 25 | `WriteAsync` to a new path under a temp root | directories created, `Exists` true, `OpenReadAsync` returns identical bytes; second `WriteAsync` overwrites; no `*.tmp` left behind |
| 26 | `OpenReadAsync` of a missing path | `EmlNotFoundException` with `RelativePath` set; `Exists` false; `DeleteAsync` → no-op |
| 27 | `DataPaths.ResolveMailPath("../x.eml")`, `("C:\\x.eml")`, `("accounts/../../x")` | `ArgumentException`; `ResolveMailPath("accounts/a/folders/1/2.eml")` → path below `MailRoot` with OS separators |
| 28 | `DataPaths` property values for root `T` | `DatabasePath == T\mailwarden.db`, `MailRoot == T\mail`, `LogRoot == T\logs` |
| 29 | **smoke** (`Category=smoke`): temp root → `ForFile(DatabasePath)` → init → account → Inbox folder → message with `EmlPath = RelativePathFor(...)` + `WriteAsync` → `ListAsync(Role: Inbox)` returns 1 → `OpenReadAsync(EmlPath)` readable → dispose; temp root removable afterwards (no open handles) | |
| 30 | manual (Dennis): `dotnet build MailWarden.sln -warnaserror` green; `MailWarden.App.exe` starts from `bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\`, shows window "MailWarden", follows Windows light/dark; `data\` folder created next to the exe | |

Positive control for the Tester: a test that stores a message and reads it back must **fail** if `Date` is stored
without offset (e.g. compare `Date.Offset`), and the FTS test for `bodyText` must fail if body text is not indexed.

## Planner decisions 2026-10-31 (fix.2) – binding, supersede the text above where they differ
Source: tester ambiguities (author.0), builder open questions (`impl-notes.md`), review.1 (`review.md`).

| # | Decision | Builder | Tester |
|---|---|---|---|
| D1 | **Search tokens are joined with implicit AND** (FTS5 default for space-separated phrases). | keep | case 19 as written |
| D2 | **Control characters (`char.IsControl`, incl. `\0`) are token separators** in `SearchAsync`, like whitespace. Text consisting only of whitespace/control characters → `ArgumentException` (extends case 18). (review BLOCK 1) | fix | add `"alpha\0x"` → finds a message containing `alpha`, no exception; `"\0"`, `"\t\0 "` → ArgumentException |
| D3 | **Schema v1 change (not released yet): message account must equal folder account.** `folders` gets `UNIQUE(id, account_id)`; `messages` replaces the single-column folder FK by `FOREIGN KEY (folder_id, account_id) REFERENCES folders (id, account_id) ON DELETE CASCADE`; `UNIQUE(account_id, folder_id, uid)` becomes `UNIQUE(folder_id, uid)`. Violation → `StorageException`. (review SHOULD 1) | fix | new case 11a: upsert message with AccountId B into a folder of account A → StorageException; `ListAsync(AccountId: B)` stays empty |
| D4 | **Indexes in v1:** replace `(folder_id, date DESC)` and `(date DESC)` by expression indexes `(folder_id, julianday(date) DESC, id DESC)` and `(julianday(date) DESC, id DESC)`; ordering stays `julianday(date) DESC, id DESC`. (review SHOULD 2) | fix | none (behaviour unchanged) |
| D5 | **`ResolveMailPath` also rejects** segments ending in `.` or a space (`x.eml.`, `x.eml `, `...`) and DOS device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, with or without extension) with `ArgumentException`. (review SHOULD 4) | fix | extend case 27 |
| D6 | **`FileEmlStore` routes every relative path through `DataPaths.ResolveMailPath`**, so `WriteAsync`/`OpenReadAsync`/`Exists`/`DeleteAsync` reject invalid paths with `ArgumentException`. (already implemented per review) | keep | new case 27a for all four methods with `../x.eml` and `C:\x.eml` |
| D7 | **Fail fast on arguments:** `Limit <= 0`, `Offset < 0`, `SearchAsync(null)` (`ArgumentNullException`), `DataPaths(null)` (`ArgumentNullException`), relative root (`ArgumentException`), `MailMessage.Uid == 0` in `UpsertAsync` (`ArgumentOutOfRangeException`, IMAP UIDs ≥ 1). | keep / add Uid check (review NIT 3) | new case 15a for the listed inputs |
| D8 | **Second `InitializeAsync` on the same instance → `InvalidOperationException`** (fail fast). | keep | new case 4a |
| D9 | **`ListAsync` of accounts/folders sorts case-insensitively** (ASCII `NOCASE`); document in the interface XML docs. (review NIT 6) | doc | case 5 unchanged (do not rely on mixed-case order) |
| D10 | Overwriting an EML file while a reader is open fails with `StorageException` – accepted for AP-01; fix the comment and the `IEmlStore.WriteAsync` XML doc. AP-03/AP-04 read EML into memory and retry. (review SHOULD 3) | doc | none |
| D11 | FTS deletion only via `MessageSearchIndex` (review SHOULD 5). Remaining NITs 1, 2, 4, 5, 7 at the builder's discretion. | fix | none |
| D12 | Out of scope, recorded in `TODO.md`: WindowsAppSDK metapackage size → AP-13; invalid `MAILWARDEN_DATA_DIR` at start → logger + message box in AP-04. | – | – |

## Planner decisions 2026-10-31 (verify.2) – binding
| # | Decision | Builder | Tester |
|---|---|---|---|
| D13 | Contract text above updated to D3/D4 (message key `(FolderId, Uid)`, composite folder FK, expression indexes). | – | – |
| D14 | D5 as implemented is accepted: superscript `COM¹–³`/`LPT¹–³` and device names with trailing spaces before the extension (`CON .eml`) are rejected too (Windows treats them as devices). | keep | optional: add `COM¹` and `CON .eml` to case 27 |
| D15 | Case 11a also covers the **update** path: an existing message (account A, folder of A) upserted again with AccountId B → `StorageException`, stored row unchanged (`GetByUidAsync` still returns account A). | keep | extend 11a |
| D16 | Cascades leave no full-text rows: after `IAccountStore.DeleteAsync` / `IFolderStore.DeleteAsync`, `SearchAsync` for a word that only the deleted messages contained returns empty (extends cases 22/23). | keep | extend 22/23 |

## Done criteria
- Builder: `dotnet build MailWarden.sln -warnaserror` green on the feature branch (without the test projects);
  `impl-notes.md` lists every file, deviations from this contract, what was verified by probe program vs. untested,
  and open questions with file:line.
- Tester: test project compiles against the contract (it cannot run before verify), `test-plan.md` maps every
  case 1–29 to a test name, `tests\test-map.json` exists.
- Planner (verify): merge both branches into `feat/ap-01-skeleton-storage`, `dotnet sln add` test project, Tester runs
  S2 through the watchdog, Reviewer approves, Dennis performs case 30.
