# AP-02a test result

## verify.1 (2026-10-31)
**Result: PASS** (stage S3, full suite via watchdog)

- Build `dotnet build MailWarden.sln -warnaserror`: 0 warnings, 0 errors.
- Full suite: 237 total, 237 passed, 0 failed (AP-01: 100, AP-02a: 137 incl. new cases). Duration ~5 s.
- Fixes in own tests (class a): CS0407 in AuthConfigurationTests (lambda); `Timeout` attribute removed from 30 synchronous
  tests (xUnit: "Tests marked with Timeout are only supported for async tests", 103 failures in first run).
- New tests: case 25a (A3: grant of other stored account -> AccessDenied, no SignOut, both accounts unchanged);
  A1 ListKeys: empty prefix = all, null -> ArgumentNullException, `../` / `a/b` / `a:b` / 129 chars -> ArgumentException.
- A7 (`OUTBOX`) already covered by case 10.
- No browser, no network login in tests. Cases 28-30 remain manual (Dennis).

## verify.2 (2026-10-31)
- Merge `test/ap-02a-accounts-auth` (49d44ff: 22a/22b/25b/26a) in `feat/ap-02a-accounts-auth`: conflict-free.
- Build `dotnet build MailWarden.sln -warnaserror`: 0 warnings, 0 errors.
- S3 (full suite via watchdog): **PASS** - 242 total, 242 passed, 0 failed, 0 skipped (verify.1: 237, +5 new). Duration ~3.4 s.
- Failures: none. Cases 28-30 remain manual (Dennis).

## verify.3 (2026-10-31)
- Merge `test/ap-02a-accounts-auth` (c53f5d4: case 25c) in `feat/ap-02a-accounts-auth`: conflict-free.
- Build `dotnet build MailWarden.sln -warnaserror`: 0 warnings, 0 errors.
- S3 (full suite via watchdog): **PASS** - 243 total, 243 passed, 0 failed, 0 skipped (verify.2: 242, +1 new). Duration ~3.5 s.
- Failures: none. Cases 28-30 remain manual (Dennis).
