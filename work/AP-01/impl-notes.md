STEP-RESULT: done

# AP-01 – Implementation notes (Builder)

Branch `feat/ap-01-skeleton-storage`. Build: `dotnet build MailWarden.sln -warnaserror` → 0 warnings, 0 errors
(Core, Infrastructure, App; without test projects).

## Files
| File | Content |
|---|---|
| `Directory.Build.props` | Nullable, ImplicitUsings, LangVersion latest, TreatWarningsAsErrors, NET analyzers `AnalysisLevel=latest`, Deterministic; `GenerateDocumentationFile` for every project whose name does not end in `.Tests` |
| `.editorconfig` | UTF-8, 4 spaces (2 for XML/XAML), file-scoped namespaces, braces, readonly fields |
| `MailWarden.sln` | Core, Infrastructure, App in solution folder `src`; App mapped `Any CPU → x64` |
| `src\MailWarden.Core\MailWarden.Core.csproj` | net9.0, no packages |
| `src\MailWarden.Core\Model\{MailProvider,FolderRole,MessageFlags,ContactSource,Account,MailFolder,MailMessage,Contact}.cs` | domain model as in the contract |
| `src\MailWarden.Core\Storage\{StorageException,SchemaVersionException,EmlNotFoundException}.cs` | exception hierarchy |
| `src\MailWarden.Core\Storage\{IAccountStore,IFolderStore,IMessageStore,IEmlStore,MessageQuery}.cs` | storage contracts |
| `src\MailWarden.Infrastructure\MailWarden.Infrastructure.csproj` | net9.0-windows, Microsoft.Data.Sqlite |
| `src\MailWarden.Infrastructure\Storage\DataPaths.cs` | data root paths, mail path validation |
| `src\MailWarden.Infrastructure\Storage\SqliteDatabase.cs` | connection ownership, version check, migration runner, WAL |
| `src\MailWarden.Infrastructure\Storage\SchemaMigrations.cs` | ordered `(int Version, string Sql)` list, schema v1 |
| `src\MailWarden.Infrastructure\Storage\SqliteAccountStore.cs`, `SqliteFolderStore.cs`, `SqliteMessageStore.cs` | stores |
| `src\MailWarden.Infrastructure\Storage\MessageSearchIndex.cs` (internal) | the one place that maintains `messages_fts` and builds the sanitised MATCH expression |
| `src\MailWarden.Infrastructure\Storage\SqliteValues.cs` (internal) | column conversions (Guid text, ISO 8601 date, enums) and `SqliteException → StorageException` wrapping |
| `src\MailWarden.Infrastructure\Storage\FileEmlStore.cs` | EML file store |
| `src\MailWarden.App\MailWarden.App.csproj`, `app.manifest`, `App.xaml(.cs)`, `MainWindow.xaml(.cs)` | WinUI 3 skeleton |
| `work\AP-01\impl-notes.md` | this file |

Nothing created under `tests\`. `PLAN.md` not changed (instruction of the coordinator; the *Umgesetzt* column is
summarised below instead).

## Interface deviations from the contract
None in names, namespaces or signatures. Details the contract leaves open, decided as follows:
- `SchemaVersionException(int foundVersion, int supportedVersion)` and `EmlNotFoundException(string relativePath)`
  (+ an overload with inner exception) are the constructors; `FoundVersion`, `SupportedVersion`, `RelativePath` are
  get-only properties.
- `ForFile` builds `Data Source=<path>;Mode=ReadWriteCreate;Pooling=False`, `InMemory` builds
  `Data Source=:memory:;Pooling=False`. Pooling is off so that disposing really releases the file (case 29, temp root
  removable); `DisposeAsync` additionally calls `SqliteConnection.ClearPool` for custom connection strings.
- `ForFile` requires a fully qualified path; `InitializeAsync` creates the parent directory of a file database.
- `SchemaVersion` before `InitializeAsync` throws `InvalidOperationException` (same as `Connection`); after dispose both
  throw `ObjectDisposedException` (a subclass of `InvalidOperationException`).
- `UpsertAsync` of folders and messages ignores the `Id` of the argument (key is the natural key), returns a copy with
  the stored id.
- Argument validation (fail fast, `ArgumentException` family, not `StorageException`): null records/collections,
  `MessageQuery.Limit <= 0`, `Offset < 0`, `SearchAsync` `limit <= 0`, `RelativePathFor` with `folderId <= 0` or
  `uid == 0`, `DataPaths` with a relative root.
- `DataPaths.ResolveMailPath` rejects, besides rooted paths and `..`: empty segments (`a//b`), `.` segments and
  characters invalid in file names (`:` → no alternate data streams, `*?"<>|`). Final check: the normalised result
  must start with `MailRoot\`.
- Message ordering is `julianday(date) DESC, id DESC` (see open question 1).
- Search: every whitespace token becomes `"token"*` (quotes doubled), tokens are AND-combined; tokens without any
  letter/digit (e.g. `(`, `*`) are dropped; if none remain the result is empty (no exception). FTS5 tokenizer:
  `unicode61 remove_diacritics 2` (case- and accent-insensitive).
- Deleting accounts/folders removes the FTS rows explicitly in the same transaction (FK cascades do not reach the
  virtual table).

## Verified by probe program (`_scratch\StorageProbe`, not a test, git-ignored)
86 checks, all OK, against `InMemory()` and temp-file databases under `%TEMP%`:
- #1, #2 (second instance on same file, data preserved, journal mode `wal`), #3 (version 2 found → exception with
  both numbers, instance stays uninitialised), #4
- #5, #6 (`UNIQUE constraint failed: accounts.email_address` → `StorageException`, also with different case), #7, #8
- #9 (Date `+02:00` and `-05:30` with sub-second ticks round-trip exactly, `MessageId == null`), #10 (incl. body kept
  when `bodyText == null`), #11
- #12 (dates with different offsets: ordering is by instant, tie → higher id), #13, #14, #15, #16
- #17 (subject mixed case, body-only word, full from address `billing_dept@shop.example.test`, from name, unknown
  word), #18, #19 (`"foo" OR bar*`, `(`, `NEAR(...) ^col:x - * {a b}`, unbalanced quotes)
- #20, #21 (incl. FTS rows removed), #22, #23 (no orphan rows in `messages_fts`), #24, #25, #26, #27 (all three
  cases plus `/x`, `\x`, `C:x`, `a/./b`, `a//b`, `a/b.eml:ads`), #28 (also with trailing backslash), #29 (temp root
  deleted afterwards without error)

Untested:
- #30 / WinUI app start, window title, light/dark, `data\` creation next to the exe – app was only built, never
  launched (manual check by Dennis).
- Migration from a real older schema version (only version 1 exists).
- Cancellation paths, IO failures (disk full, access denied) in `FileEmlStore`, corrupt column values
  (`SqliteValues` → `StorageException`).

Observed while probing (not a contract case): overwriting an EML file while a reader stream on it is still open
fails with `StorageException` ("Access to the path is denied"; Windows `MoveFileEx` cannot replace an open file
even with `FileShare.Delete`). The temp file is cleaned up. `DeleteAsync` while a reader is open works.

## Package versions
| Package | Version | Project |
|---|---|---|
| Microsoft.Data.Sqlite | 9.0.22 (latest 9.x, matches .NET 9; transitive SQLitePCLRaw.bundle_e_sqlite3 2.1.13 with FTS5) | Infrastructure |
| Microsoft.WindowsAppSDK | 1.8.260921001 (latest stable 1.x) | App |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.9457 (latest 10.0.26100.x, matches the installed SDK 10.0.26100) | App |

## WinUI csproj settings that build with plain `dotnet build` (first attempt, no Visual Studio)
`OutputType WinExe`, `TargetFramework net9.0-windows10.0.19041.0`, `TargetPlatformMinVersion 10.0.17763.0`,
`UseWinUI true`, `WindowsPackageType None`, `WindowsAppSDKSelfContained true`, `Platforms x64`,
`RuntimeIdentifier win-x64`, `ApplicationManifest app.manifest` (PerMonitorV2, Windows 10/11 supportedOS), plus
`<Platform>x64</Platform>` when `$(Platform)` is `AnyCPU` so that a direct project build (`dotnet build/run
--project src\MailWarden.App`) also lands in `bin\x64\Debug\...`. No explicit Page/ApplicationDefinition items needed
(WinAppSDK defaults). Output: `src\MailWarden.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\MailWarden.App.exe`
with `Microsoft.WindowsAppRuntime*.dll`, `Microsoft.ui.xaml.dll`, `*.xbf`, `*.pri`, `e_sqlite3.dll`.

## Open questions
1. `src\MailWarden.Infrastructure\Storage\SqliteMessageStore.cs:21` – `date` is text with the original offset, so text
   order is not time order (`10:00+02:00` sorts after `09:00+00:00`). I order by `julianday(m.date)` (correct, ms
   precision), which means the indexes `(folder_id, date DESC)` / `(date DESC)` from the contract
   (`SchemaMigrations.cs:50-51`) cannot serve the sort. Fine for AP-01 volumes; for large mailboxes a v2 migration
   could add a `date_utc` column (or expression indexes on `julianday(date)`). Decision for the planner.
2. `src\MailWarden.Infrastructure\Storage\SqliteMessageStore.cs:59` – nothing guarantees that `message.AccountId`
   equals the account of `message.FolderId` (two independent FKs). A composite FK `(folder_id, account_id) →
   folders(id, account_id)` would enforce it, but changes schema v1. Keep as is?
3. `src\MailWarden.Infrastructure\Storage\SqliteDatabase.cs:115` – a second `InitializeAsync` on the same instance
   throws `InvalidOperationException` (fail fast) instead of being a no-op. Case 2 only covers a second instance.
4. `src\MailWarden.Infrastructure\Storage\SqliteAccountStore.cs:24` / `SqliteFolderStore.cs:25` – "ordered by
   DisplayName/FullName" implemented case-insensitively (`COLLATE NOCASE`, ties binary, then id). If tests expect
   ordinal order, mixed-case names would differ.
5. `src\MailWarden.Infrastructure\Storage\FileEmlStore.cs:57` – overwrite while a reader is open fails (see above).
   Relevant for AP-03/AP-04 (sync re-downloads while the reading pane holds the file); possibly read into memory in
   the UI or retry in the command queue.
6. `src\MailWarden.App\MailWarden.App.csproj:23` – the 1.8 metapackage pulls `Microsoft.WindowsAppSDK.AI`/`.ML`
   (onnxruntime.dll 20.7 MB, DirectML.dll 17.8 MB; output 140 MB). Referencing only the needed component packages
   (`Microsoft.WindowsAppSDK.WinUI`, `.Foundation`, …) would shrink the installer – a new package decision, so
   left for AP-13 / Dennis.
7. `src\MailWarden.App\App.xaml.cs:30` – an invalid `MAILWARDEN_DATA_DIR` (e.g. a relative path) throws in
   `OnLaunched` and the app terminates without a visible message (no logger yet). Acceptable for AP-01?
8. Builder role vs. coordinator: the builder role asks to fill the *Umgesetzt* column in `PLAN.md`, the coordinator
   said not to touch `PLAN.md`. Followed the coordinator. Status for the planner: AP-01 rows 1–7 implemented
   (`src\MailWarden.Infrastructure\Storage\*`), row 8 implemented (`src\MailWarden.App\*`, build only).
