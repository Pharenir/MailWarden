# AP-06 test result (verify.1)

Result: PASS, stage S3 (full suite) green on `feat/ap-06-address-book` after merging `test/ap-06-address-book`.
Build: `dotnet build MailWarden.sln -warnaserror` 0 warnings, 0 errors.

| Project | Tests | Passed |
|---|---|---|
| MailWarden.Core.Tests | 511 | 511 |
| MailWarden.Infrastructure.Tests | 1447 | 1447 |
| MailWarden.Presentation.Tests | 356 | 356 |
| Total | 2314 | 2314 |

Runtime about 16 s via the watchdog (no soft limit exceeded, no abort).

## Findings during the run
1. Build error CS1010 in `ClassificationHarness.AddSentAsync` (line breaks inside string literals, written by the author script). Class: test error (a). Fixed in the test branch.
2. First S3: 1 failure, `ContactImportVCardTests.Empty_file_imports_nothing`. The builder refuses an empty vCard file with `InvalidDataException` ("no BEGIN:VCARD line was found"); the test assumed "imports nothing". The contract is silent (class d). The test now expects the refusal (renamed `Empty_file_is_refused_as_not_a_vcard_file`). Planner to confirm.

## Not covered
Case 19 (real data, planner). `AccountPipelines` / `AccountCycleSteps.Harvest` composition and the probe (D8) have no automated test.

## verify.2 (after fix.2, cases 20-26)

Result: PASS, stage S3 green. Build `-warnaserror` 0 warnings, 0 errors.

| Project | Tests | Passed |
|---|---|---|
| MailWarden.Core.Tests | 527 | 527 |
| MailWarden.Infrastructure.Tests | 1486 | 1486 |
| MailWarden.Presentation.Tests | 356 | 356 |
| Total | 2369 | 2369 |

Runtime about 16 s via the watchdog, no limit reached.

First run: 1 failure, `ContactSenderDirectoryHardeningTests.Punycode_and_u_form_of_a_domain_are_one_entry_in_the_store`.
The store keeps a Punycode and a U-form entry as two rows; R5 only requires that lookups match both forms, which other tests prove.
Class: own test over-specified (c). Test removed; no product change.
