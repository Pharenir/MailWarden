# AP-04a – implementation notes (implement.1)

STEP-RESULT: implemented; all contract points D1–D11 are in the code. Builds of `MailWarden.Presentation` and
`MailWarden.App` with `-warnaserror`: 0 errors, 0 warnings. The GUI itself was never started.

Branch `feat/ap-04a-ui-read`. New package: `HtmlSanitizer` 9.2.1039 in `MailWarden.Infrastructure`, without
`PrivateAssets` (brings AngleSharp 1.7.2, AngleSharp.Css 1.0.2); `dotnet list package --vulnerable --include-transitive`
reports nothing.

Solution build: fails with exactly one error, as announced: `tests\MailWarden.Infrastructure.Tests\TestSupport\FaultyMessageStore.cs(7,65): CS0535` (`CountUnreadAsync` missing). That file belongs to the tester.

## Planner decisions of 2026-11-01 (additional to the requirement)
All applied:
- `MailContentReader` wraps only `FormatException`; an `IOException` of the stream passes unchanged
  (`MailContentReader.cs:41`). Empty stream, text without headers and random bytes all gave `MailContentException` (probe).
- `UiSettingsFile(path)`: path of the file.
- `SelectedItem`/`SelectedRow` setters start the load without a way to wait; the `*Async` methods can be awaited.
- `ShowAsync`: the `OperationCanceledException` of the caller's token passes; state becomes `Empty`, no log line.
- `background-image: url(https://…)` stays with `allowRemoteImages` (probe: `style="background-image: url(&quot;https://r.test/a.png&quot;)"`).
- Deep nesting: see "Limits against crafted HTML" below.
- `heldLock` is a named, optional constructor parameter of `MailboxService`.
- The banner for empty accounts contains "AuthProbe"; `StatusText` differs per state.

## Interface deviations and additions
Nothing of the contract was left out or changed in signature. Additions and interpretations:

| Where | What | Why |
|---|---|---|
| `Presentation\ReadingPaneViewModel.cs:81` | additional `StatusText` | D10 asks for "a fixed text" for `Error`, but the contract has no property for it |
| `Presentation\ReadingPaneViewModel.cs:154` | additional `AreRemoteImagesAllowed` | `AllowRequest` in the WebView2 wiring needs it; the contract only has `CanLoadRemoteImages` |
| `Presentation\ShellViewModel.cs:13` | additionally `IDisposable` | the window must end the `StatusChanged` subscription before the runtime goes away |
| `Presentation\ShellViewModel.cs:111`, `:124` | setters clamp to 150–2000 | otherwise a value could be saved that the next `Load` rejects |
| `Presentation\IMailboxController.cs:30` | adapter `MailboxServiceController` lives in Presentation | D10 says "in the composition", but `IMailboxController` is in Presentation and Infrastructure must not know it |
| `Presentation\UiSettings.cs:40` | constants `MinimumSize`, `MaximumSize` | shared by file, shell and splitter |
| `Presentation\UiSettingsFile.cs` `Save` | throws `UiSettingsException` for a size out of range | such a file would not load again |
| `Presentation\ShellViewModel.cs:285` | every `Idle` status without progress reloads, also Idle after Idle | the service raises Idle only at the end of a successful cycle, so this equals "transition to Idle" in production; a burst still coalesces to one more reload |
| `Presentation\ShellViewModel.cs:217` | Refresh iterates the accounts of the navigation, not `CurrentStatus` | TODO "status filter against the account list"; needs `InitializeAsync` first; an account without status gets a trigger |
| `Presentation\ReadingPaneViewModel.cs` | superseded by K6: `LoadRemoteImagesCommand` builds off the UI thread | see section fix.2 |
| `Presentation\FolderTree.cs:21` | at most 32 levels; the rest stays in the last name | names come from the server, later walks are recursive |
| `Infrastructure\Mime\MailContentReader.cs:15` | "5 MB" and "25 MB" are binary (5 × 1024 × 1024) | contract does not say; exactly at the limit is allowed |
| `Infrastructure\Mime\MailContentReader.cs:116` | "inline" means not `Content-Disposition: attachment`; an attachment part with a Content-ID is listed and its `cid:` image is removed | literal reading of D2 |
| `App\MailWebViewHost.cs:141` | additional `IsReputationCheckingRequired = false` | no online lookup of the message address |
| `App\MailWebViewHost.cs:148`, `:149` | `BasicAuthenticationRequested` cancelled, `PermissionRequested` denied | a remote image must not be able to show a password dialog |

## Sanitizer: behaviour worth knowing
`Infrastructure\Mime\MailHtmlSanitizer.cs`. Three steps on one parsed document: own removal of the "removed entirely"
list (`:43`), package allow-lists, own rules for `a href`, `img src`, style URLs.

- The removed list is extended by elements whose content is not body text or would appear twice: `title`, `head`,
  `noscript`, `noembed`, `noframes`, `frameset`, `option`, `optgroup`, `datalist`, `canvas`, `track`, `param`, `keygen`,
  `portal`.
- Unknown tags (`o:p`, `foo`) only lose the tag, their text stays (`KeepChildNodes`, `:92`).
- `img` with a blocked remote source keeps the element without `src` (with `alt`); `img` with `cid:` unknown, `data:` not
  allowed or another or no scheme (also relative and `src=""`) is removed completely. `img` without `src` stays.
- `AngleSharp.Css` normalises values, **important for the tester**: `color: red` comes out as
  `color: rgba(255, 0, 0, 1)`, `font-family: 'x'` with double quotes (as `&quot;` in the attribute). Case 14 "colour
  stays" should look for the property `color`, not the text `red`.
- Shorthands with a blocked URL: `background: url(https://…) no-repeat red` keeps the longhands without the image and
  counts 1. `background-image: url(a), url(b)` counts 2.
- `position`: `static`, `relative`, `absolute`, `sticky` stay (`:75`), everything else goes, also the escaped `f\69xed`.
- Stays by contract: `display: none`, `position: absolute`. A mail can therefore hide text or lay text over text inside
  the document, not over the app's header (for the security review).

### Limits against crafted HTML
Measured: the parser takes 24 s for 100,000 nested `div` and more than 60 s for 1,000,000; many siblings, comments and
stray `<` are also quadratic. A stack overflow would end the process without any handler. Therefore:

1. `HtmlNestingLimiter` (linear, before parsing): estimated depth above 1024 (`MailHtmlSanitizer.cs:31`) or more than
   20,000 start tags and comments (`:35`) → these tags are dropped, the text stays. Stray `<` become `&lt;`.
2. After parsing: everything below depth 256 (`:28`) is flattened to text (iterative).

The estimate ignores void and self-closing elements and respects scope boundaries, so that `<div><object></div>` cannot
hide depth. Legitimate mail with more than 20,000 tags loses the markup of the rest, not its text.

Measured with limits on a 1 MB stack: 5,000 / 100,000 / 1,000,000 levels of `div`, `b`, `table`: 4–135 ms, no
overflow, text kept.

**Correction:** implement.1 stated "worst cases 5 s" here. secure.1 disproved that: 100,000 attributes took over 40 s,
because these limits did not look at attributes or style declarations. Current limits and times: section fix.2.

## D4: WebView2 questions, checked against the real runtime
Probe `_scratch\webview2-probe` (hidden window, runtime 138.0.3351.83, only `.invalid` addresses, report in
`bin\Debug\net9.0-windows\report.txt`). It uses the same native API through the WinForms SDK, **not the WinUI control**.

| Question | Result |
|---|---|
| InPrivate possible in WinUI? | Yes. `WebView2.EnsureCoreWebView2Async(environment, controllerOptions)` exists in WinAppSDK 1.8 and compiles (`MailWebViewHost.cs:113`). Probe: `Profile.IsInPrivateModeEnabled` is true |
| Filter `*` also for the main document? | Yes: `WebResourceRequested … message.html context=Document`, served from memory with status 200 |
| `DownloadStarting` reachable? | Yes: compiles against the WinUI `CoreWebView2` (`MailWebViewHost.cs:147`); probe: event fires and `Cancel` ends the download |
| Refused request | CSS image and `img` as `context=Image`, 403 works. Blocked by CSP nothing arrives at the filter at all |
| Scripts off | `<script>` does not run (title unchanged) |
| Document over 2 MB | 6 MB works |
| Second document at the same URI | is requested anew with `Cache-Control: no-store` |
| `data:` image | no request event, is displayed with CSP `img-src data:` (`naturalWidth` 1). So `AllowRequest` never sees `data:` in practice |
| Links | `NavigationStarting` fires with the target and can cancel; `target=_blank` arrives at `NewWindowRequested`; `mailto:` arrives at `NavigationStarting`. After a cancel the filter may still see the request: it gets a 403, nothing goes out |
| Meta refresh | `NavigationStarting` with `IsUserInitiated=false`, is cancelled (the sanitizer removes `meta` anyway). `DecideNavigation` would open an http target externally: without the sanitizer a mail could open a browser by itself, see question 2 |
| favicon | The browser requests `/favicon.ico` at the document host: `context=Other`, 403 |

Probe processes: none left after the run (checked by command line).

## Tested / not tested
**Tested by me with probes (`_scratch\`), no proof, the tester's run stays necessary:**
- `sanitizer-probe`: all lists of D3 by printed output, D2 reader with synthetic EML, 940,000 fuzz inputs without exception and without `script`/`svg`/`onerror`/remote `img src` in the output, 40,000 parallel runs on 8 threads with identical results.
- `runtime-probe` on a temporary root with artificial data: lock order (second open: `DataFolderInUseException`, database size and time unchanged), dispose twice, reopen, 450 parallel writes on two connections, `CountUnreadAsync`, shell on the real read model (navigation, paging 100/200/250, reload keeps the selection, settings round trip, clamp, refresh, status), service with `heldLock` leaves it held, log format and a bad log path.
- `webview2-probe`: see above.

**Not tested:**
- Nothing with the GUI: `MailWarden.App` was only compiled. Open for Dennis (cases 42–48): XAML at runtime, `x:Bind` to the types of the Presentation assembly, tree selection by code (`NavigationTree.SelectedItem`), expanded nodes, splitter, list loading more on scrolling, theme, `Launcher.LaunchUriAsync` unpackaged, `MemoryStream.AsRandomAccessStream` as response stream of the WinUI projection (`MailWebViewHost.cs:168`, the probe used the `Stream` overload), closing and process end, error window.
- Existing test projects: not run, because the solution does not compile before the tester adds `CountUnreadAsync` to `FaultyMessageStore`. The `MailboxService` change is meant to leave the old behaviour unchanged (`heldLock` null), checked by reading only.
- No real accounts, no real mail.

## For the tester
- Constructors of the child view models are internal: test through `ShellViewModel`.
- `Refresh` needs `InitializeAsync` first (accounts come from the navigation).
- Colour normalisation in case 14 (see above).
- The EML must parse: a text without any header line gives `MailContentException`; "Subject: x" alone is a valid message.
- `SelectedRow` starts `ShowAsync` in the background and the document is built on the thread pool, so wait for the state; `ShowAsync` itself can be awaited.
- `Banner` joins several current texts with a space.

## For the security review
Layers for remote content: sanitizer, CSP meta, `WebResourceRequested`, `NavigationStarting`. Log calls pass type names
and fixed texts only. `SanitizedHtml` is produced from one DOM, not re-parsed; a mutation after serializing would still
meet CSP and filter with scripts off.

## Questions to the planner
1. Answered by K10: `form` loses only the tag.
2. Answered by K11: without a user gesture `OpenExternally` becomes `Block`.
3. Answered: stays as it is.

## fix.2 (K1–K16)
All implemented on `feat/ap-04a-ui-read`, base `c7c86e2`. `Infrastructure\Mime\MimeParserDefaults.cs` is unchanged.
Builds of `MailWarden.App` and `MailWarden.Presentation` with `-warnaserror`: 0 errors, 0 warnings. The test projects do not
compile for now (`UiSettings`, `ListWidth`/`ListHeight`), as announced; **no test was run**.

"Probe" below means a self-check under `_scratch\`, no proof. "Only built" means compiled and read, never executed: the GUI
was not started.

| K | State | Checked by probe | Only built or read |
|---|---|---|---|
| K1 | done: `IsOpen` and `Message` set in code-behind, also once in the constructor | the view model's `Banner` returns to null | info bar in the window |
| K2 | done: `MimeParserDefaults.Create()` in `SyncEngine.ParseAsync` and `MailContentReader`; a search of `src` found no other `MimeMessage.Load` or `MimeParser` | reader: 1,023 nested `multipart` and `rfc822`, 5,000 and 20,000 `multipart`: no process end (`secprobe`, `secprobe2`). A body below depth 32 stays opaque: `Html` null, the message opens without its text | `SyncEngine` (case 49 of the tester); `secprobe2 sync` uses its own load call and does not cover it |
| K3 | done: compared by message id; the control gets its selection back after the collection change | shell: reload keeps the selection | the control's behaviour |
| K4 | done, details below | shell: `Dispose` during a slow reload: 0 further reads, 0 log lines, also when the running read then fails | `App` paths, `isClosed` of the host |
| K5 | done: strict budget of 34,000,000, each image checked once; `OutOfMemoryException` ends in `Error` | see measurements; exactly 34,000,000 kept, 34,000,001 removed | |
| K6 | done, see measurements and "beyond K6"; `LoadRemoteImagesCommand` off the UI thread with the generation guard | shell: a late rebuild does not overwrite the next message | |
| K7 | done, details below | `Shorten`; 365 KB subject → 501, long name → 101 with the address kept, To → 2,001, 100,000 attachments → 51 entries, last "und 99950 weitere" | `MaxLines`, `MaxHeight`, `ScrollViewer` |
| K8 | done with a deviation, see below | | theme choice |
| K9 | done | settings file round trip, `ShellViewModel` saves each size | limit of 200 px for the reading pane |
| K10 | done | form with text and input: tag and input gone, text stays | |
| K11 | done | table for document, http, mailto, javascript | a real click must report `true` (case 45) |
| K12 | done | `attach`, `ATTACH`, `%61ttach`, `;attach`, fragment dropped; `to`, `subject` with encoding unchanged; no parameters left → no `?`; not a mailto → unchanged | `Launcher` |
| K13 | done | `AllowRequest`: `localhost`, `x.localhost`, `localhost.`, `127.0.0.1`, `127.1`, `2130706433`, `0x7f.0.0.1`, 10/8, 172.16/12, 192.168/16, 169.254/16, `0.0.0.0`, `[::1]`, `[::]`, `[fe80::1]`, `[fd00::1]`, `[::ffff:192.168.1.1]` false; public hosts true. Document head with referrer meta, CSP text unchanged | `DeleteAllCookies` |
| K14 | done, `\u` escapes in the source | code points of a mixed text; U+200C and U+200D stay | |
| K15 | done: only a change into `WaitingAfterError` or `NeedsSignIn` reloads | shell: same state again no reload, then `NeedsSignIn` one | |
| K16 | done: `AtomicFile`, `MailDisplayText.Subject`, `NavigationViewModel.ReadStatusByAccount`, `CriticalLogLevel.Critical` (replaces `Presentation\LogLevels.cs`, deleted) | settings round trip | |
| NITs | `HorizontalContentAlignment=Stretch` for list items, `heldLock` in the summary of `StartAsync` | | list items |

Precisions of the coordinator are met: `Shorten` keeps up to `maxCharacters`, then "…" (at most `maxCharacters + 1`), exactly
`maxCharacters` stays unchanged. `From` in the reading pane uses the limit 100. A start tag over 256 attributes is dropped
completely, its `title` does not survive. Same K15 message again: no reload.

### K4 in detail
- `ShellViewModel.Dispose` closes navigation, list and reading pane. A running load gets cancelled and made stale, so a later result or failure changes nothing and logs nothing. Between reads the token is checked, so a fake or a query that ignores the token still causes no further read. Later calls return without a read.
- `MailboxReadModel.CloseAsync` passes through the gate; the runtime calls it after stopping the service and before closing the connections. Later reads get `ObjectDisposedException`.
- `App`: `isShuttingDown` is checked before `Service.StartAsync` and in the `catch` of `OnLaunched`; `ReleaseAfterFailedStartAsync` closes the window inside its `try`, also when disposing the runtime fails.
- `MailWebViewHost`: `isClosed`; a late `Show` does nothing, a start ended by closing writes no log line.

### K6: measurements
Debug build of the Infrastructure DLL, 1 MB stack, one machine; `secprobe` of the security review (copied to `_scratch\secprobe`).
**Not a guarantee:** review.2 and secure.2 found bypasses of these numbers; current numbers in section fix.3.

| Input | secure.1 | Now |
|---|---|---|
| `attrs` 100,000 (one tag) | over 40 s | 181 ms; dropped, text stays |
| `attrsAllowed` 100,000 | | 184 ms; dropped |
| `styleUnknown` 100,000 | | 172 ms; tag stays without `style` |
| `styleKnown` 100,000 | | 169 ms |
| `styleLong` 100,000 | | 168 ms |
| `styleLongUrls` 100,000 | | 178 ms |
| `unclosedUrl` 100,000 | | 170 ms |
| `spanDiv` 100,000 | | 1,664 ms |
| `bDiv` 100,000 | | 1,273 ms |
| `aDiv` 100,000 | | 1,301 ms |
| `plainDiv` 1,000,000 | | 368 ms |
| `tableEsc` 100,000 | | 452 ms |
| `fontB` 100,000 | | 231 ms |
| `pB` 100,000 | | 550 ms |
| `bigText` 1,000,000 | | 363 ms |
| `bigAttr` 1,000,000 | | 170 ms |
| `ampText` 1,000,000 | | 453 ms |

**Target met:** all inputs of the security probes under 2 s, slowest 1.7 s.

My own attempts to stay just inside the limits (`_scratch\fix2-probe`): 20,000 unknown sibling elements **2.1 s (slowest, slightly
over the target)**, the package replaces each of them in the sibling list; tags with 256 attributes up to the text budget 1.5 s; styles
just under 64 KB up to the budget 1.5 s; 20,000 styled siblings 1.2 s; 255 declarations 0.5 s; 20,000 comments 59 ms; quote tricks in `script`,
`--!>` and `svg` `style` with 1,000,000 `div` at most 158 ms. Fuzz with 920,000 inputs: no exception, nothing suspicious. 40,000 parallel
runs: identical.

K5, `secprobe3`: `cid 1 300`: 423 ms, 24 images kept, document 33,555,780 characters, peak 426 MB (secure.1: 419 million
characters, 3.8 GB). `cid 25 1000`: 192 ms, 215 MB.

MIME and headers: `parts 100000` 1,574 ms, `subject 5000` 111 ms; headers `comment`, `group`, `route`, `many` at 100,000 at most 361 ms.

### Beyond K6 (limits I added, the same goal)
| Where | What | Why |
|---|---|---|
| `HtmlNestingLimiter.MaxTagTextChars` | budget of 2,000,000 characters of kept tag text, without `data:` of `src` | K6 leaves the value open; tags with 256 attributes up to the budget were 2.9 s at 4,000,000 |
| `HtmlNestingLimiter.MaxStyleDeclarations` | a `style` with more than 256 `;` is dropped, the tag stays | the parser is quadratic in declarations, 64 KB alone would allow about 6,500 |
| `HtmlNestingLimiter.MaxEndTags` | at most 20,000 end tags; end tags lose their attributes | each end tag splits the text, and the parser appends text quadratically: the dropped start tags left 180,000 `</span>` and took 4.9 s instead of 0.6 s |
| `HtmlNestingLimiter.MaxTagsInAttributeValues` | at most 1,024 `<x` in kept quoted values of the whole document, the tag beyond is dropped | a quote inside `script` could otherwise hide real tags from the scan as an attribute value |

Consequence: a mail with an unbalanced quote in a `<x …` inside a `script`, more than 1,024 of them, or over the budget loses the affected
tag with what the scan read as its attributes.

**Correction:** "Comments are skipped correctly" stood here. review.2 and secure.2 disproved it: `<style><!--</style>` makes the scan
see a comment where the parser sees raw text, and all following tags were unseen (32 s and more). Fixed by L1, see section fix.3.

### Deviations and open points
- **K8:** no `Tag`: the entries are `ComboBoxItem` with `Content`, and `SelectedIndex` maps to a private array. A boxed enum of the Presentation assembly in `Tag` would have to cross the WinRT boundary; I could not test that without a GUI start.
- **K7, sender:** the cut keeps the address and shortens the name, so the total stays at 101 at most, unless the address has more than 90 characters. Attachment names are additionally shortened to 260 characters each (not asked for).
- **K5:** `MailContentReader` counts 25 **binary** MB, which are 34,952,558 characters; the budget of 34,000,000 is strict. A single embedded image can only reach 5 MB, but a set of inline images between about 24.3 and 25 MB loses its last image in the document (`<img>` removed, the part is not listed as an attachment). Question to the planner: reader limit to 24 MB, or accept? Not changed.
- **K13:** IP literals only. A public name that resolves to a local address (DNS rebinding) is not covered.
- **K15:** the first status of an account counts as a change. A status that is already in `CurrentStatus` at the start and comes again reloads once.
- **K6:** the rebuild no longer runs on the UI thread, so `DocumentHtml` changes later than `Execute`: tests must wait for it. `CanLoadRemoteImages` is false at once.

**Hints for the tester:** `listSize` is now an unknown key. `form` keeps its text. `DecideNavigation(string)` stays. Case 64 can observe the result only by waiting, there is no awaitable member.

## fix.3 (L1–L11)
All implemented, base `c2e3603`. Builds of `MailWarden.App` and `MailWarden.Presentation` with `-warnaserror`: 0 errors, 0 warnings.
**No test was run**, and the GUI was not started. "Probe" means a self-check under `_scratch\`, no proof. Records got their new members
with default values, so existing callers keep compiling.

| L | State | Checked by probe | Only built or read |
|---|---|---|---|
| L1 | done: raw-text elements removed up to the first matching end tag, without one the rest of the input; `xmp`, `plaintext`, `listing` lose only the tag; every `<` in attribute values counts; comment with more than 20,000 `<` is scanned | time table below; 34 wrappers of `secprobe4 safety` all ok; controls of `l3-probe` | |
| L2 | done, plus a limit I added, see below; exactly 64 stay, the 65th goes | `<b>` × 64 not reduced, × 65 reduced; bomb below | |
| L3 | done | `l3-probe` with a blocked builder and a manual clock: waits at 4.9 s, plain text and notice at 5.1 s; same mail and other mail during the run: 1 build, plain text; late result ignored; next mail builds again | with the real builder, because no real input reaches 5 s any more |
| L4 | done: `WasReduced`, `ContentNotice`, `HasContentNotice`; removing raw-text elements does not count | normal newsletter null; over budget set; image budget true, not hit false | text above the document |
| L5 | done: `HasOpaqueParts`, walks also into attached messages; a generic part of a container type counts, delivery reports do not | depth 5 false; depth 40 true and not listed; harmless top + depth 40 true; rfc822 depth 40 true; DSN false | |
| L6 | done: `MailContent.Senders`, `MailSender`, `SenderLine(IReadOnlyList<MailSender>)`, `ShortenAddress` | all cases below | |
| L7 | done | | `DeleteAllCookies`, `CloseReadingView` paths |
| L8 | done; `::/64` is rejected as a whole, which contains `::/96` and `::ffff:0:0/96` | `secprobe4 lan`: 63 blocked, allowed only `public.example.test`, `8.8.8.8`, `[2001:4860:4860::8888]` and `127.0.0.1.nip.io` | |
| L9 | done | `secprobe4 mailto` | `Launcher` |
| L10 | done: `MaxHeight` 160, comment moved. Case 45 with `mailto:` belongs to the manual test list (documenter) | | |
| L11 | done, only built: `_scratch\limiter-probe`, copied to the main checkout | | never started, as ordered |

### Measurements
`DOTNET_GCHeapHardLimit=2 GB`, Debug build, 1.5 MB stack, one machine. Baseline of the process without crafted input: about 60 MB.

| Input (`secprobe4 time`, `secprobe5`) | Before (review.2, secure.2) | Now | Peak |
|---|---|---|---|
| `scriptCommentDiv` 100,000 | | 250 ms | 88 MB |
| `styleCommentDiv` 100,000 | 32 s | 247 ms | 87 MB |
| `titleCommentDiv` 100,000 | | 255 ms | 87 MB |
| `textareaCommentDiv` 100,000 | | 257 ms | 88 MB |
| `scriptDataSrcDiv` 100,000 | | 246 ms | 87 MB |
| `styleDataSrcDiv` 100,000 | | 246 ms | 88 MB |
| `scriptCommentAttrs` 100,000 | over 45 s | 186 ms | 62 MB |
| `scriptCommentStyle` 100,000 | | 168 ms | 62 MB |
| `scriptCommentSiblings` 100,000 | | 329 ms | 92 MB |
| `attrs` 100,000 | | 188 ms | 62 MB |
| `styleUnknown` 100,000 | | 171 ms | 62 MB |
| `formatBomb` 1000 18000 | over 5 GB | **1,422 ms** | **193 MB** |
| `formatBombEsc` 1000 18000 | | 1,099 ms | 140 MB |
| `titleValueAttrs` 100,000 | over 60 s | 186 ms | |
| `titleValueStyle` 100,000 | | 168 ms | |
| `styleValueAttrs` 100,000 | | 183 ms | |
| `noRawControlAttrs` 100,000 | | 201 ms | |

All under 2 s. `secprobe`: `spanDiv` 1,679 ms, `aDiv` 1,292 ms, `bDiv` 1,284 ms (slowest of all). Own attempts near the limits: 20,000 unknown siblings 2.0 s, 256 attributes
up to the budget 1.6 s, the rest below. Fuzz 930,000 inputs without exception or suspicious output; 40,000 parallel runs identical. Regression of the fix.2 probes: unchanged results.

### Beyond L2
L2 alone did not reach the target: 64 open elements copied into 18,000 blocks still gave 5.7 s and 1 GB. Therefore `MaxCopiedFormattingElements`: each kept start tag adds the
number of open formatting elements to an estimate; above 100,000 every further start tag is dropped (`WasReduced`). That is the difference between 1 GB and 193 MB. A mail in 5 unclosed
`font` tags reaches it after 20,000 tags, which is also the budget for all tags.

### L3: interface for the tests
- `ShellViewModel(readModel, controller, dispatcher, settings, log, timeProvider, Func<MailContent, bool, ReadingDocument>? documentBuilder = null)`: null means `ReadingDocumentBuilder.Build`. The `bool` is `allowRemoteImages`.
- `ReadingPaneViewModel.BuildTimeLimit` is a public static `TimeSpan`, 5 s. The wait is `Task.Delay(limit, timeProvider, token)`, so it needs `CreateTimer` of the given `TimeProvider`; a provider overriding only `GetUtcNow` waits for real.
- `ShowAsync` completes when the document or the plain text is shown. With a blocked builder it stays pending until the clock passes the limit.
- Fallback document: `ReadingDocumentBuilder.BuildFromPlainText(MailContent)` (public).
- Addendum of the planner: while the build of **another** mail runs, the pane stays in `Loading` and waits at most `BuildTimeLimit` from its own call. Build ended in time: the current mail builds normally, intermediate selections build nothing. Not in time: plain text with `ContentNotice`. The **same** mail during its own build: plain text at once, no second build. At most one build at a time.
- `ReadingPaneViewModel.ContentNotice`, `HasContentNotice`. Texts: `UiText.ContentReduced`, `ContentShownAsText`, `ContentHasOpaqueParts`, `MoreSenders(int)`.

### Deviations and open points
- **Rapid clicking:** decided by the addendum to L3, see the interface above. Self-check in `_scratch\l3-probe`, four flows: another mail in time; intermediate selections; not in time with the limit from the own call; same mail. A stuck build costs each later mail up to 5 s until it ends.
- **L6:** without parsed `Senders` (content not from a parsed message, e.g. a fake) `From` is treated as one address. With a name beside it the address gets at most 60 characters, and the name the rest of the 100, so the name is usually cut earlier than at 100. "+N weitere" comes on top of the limit. The row in the list uses `ShortenAddress` as well when there is no name.
- **L1:** text inside `<script>` without an end tag is gone with the rest of the mail, as the parser would read it. A stray `<` inside a scanned comment gets encoded; it stays a comment.
- **L4:** comments dropped over the budget and flattening below depth 256 also set `WasReduced`. Attributes of end tags and `xmp`-like tags do not.
- **L8:** `127.0.0.1.nip.io` stays allowed: a public name resolving to a private address is accepted by the planner.
- **L11:** `LimiterProbe.csproj` refers to the worktree by its fixed path, also in the copy; if the worktree is gone, change the path in that file. It opens the database with `Mode=ReadOnly` and only needs table `messages` and column `eml_path`.

## fix.4 (M1a, M2–M8)
All implemented, base `0816649`. **No test was run**, and the GUI was not started. "Probe" means a self-check under `_scratch\`, no proof.
The test projects are not touched; the planner's decision about the strict CSP of the plain-text document needs only a test change.

### Probe of the parser callbacks
`OnCreated` does **not** see copied formatting elements (`_scratch\oncreated-probe`: `formatBomb` 64 x 100: 168 callbacks, 6,568 elements). `OnToken` carries (`_scratch\ontoken-probe`):
it fires for every token, also while the tree is built, including `Character`, `Comment` and `EndOfFile`. An exception from it reaches the caller unchanged and ends the parser.

| Case, parser alone, budget 256 MB or 3 s per token | Result | Tokens | Time | Allocated | Process peak |
|---|---|---|---|---|---|
| `formatBomb` 1000 x 18,000 | aborted | 2,064 | 854 ms | 256 MB | 268 MB |
| `fmtObject` 1000 x 16,000 | aborted | 7,094 | 641 ms | 256 MB | 282 MB |
| `fmtTable` 1000 x 16,000 | aborted | 10,989 | 757 ms | 256 MB | 287 MB |
| `formatBomb` 64 x 18,000 | aborted | 17,028 | 615 ms | 256 MB | 290 MB |
| `<p>x</p>` x 150,000 | not aborted | 450,004 | 226 ms | 112 MB | |
| `<p>x</p>` x 10,000 | not aborted | 30,004 | 8 ms | 7 MB | |

The check is between tokens, so one token can overshoot; 256 MB was never exceeded by more than the rounding here.

| M | State | Checked by probe | Only built or read |
|---|---|---|---|
| M1a | done: `ParseWithinBudget` in `MailHtmlSanitizer`; own parser with the CSS configuration of the package; on abort `MimeBodyText.ExtractFromHtml`, encoded, `WasReduced` true | on the calling thread and on a pool thread aborted without markup, text kept; a thread with 1.2 GB allocated before is not reduced for a normal mail; reading pane shows the notice | |
| M2 | done | `<!---->`, `<!-->`, `<!--->`, `<!----!>`, `<!-- x --!>` end there; `<!--!>a-->b` and `<!---!>a-->b` end at `-->`; 70,000 characters with `<` dropped, also `data:`; without `<` kept | |
| M3 | done | same mail, clock standing: waits, one build, document, no notice; clock past the limit: text and notice; rebuild with images running, same mail: waits, then a second build without images, no wrong document; the four flows of the addendum unchanged | |
| M4 | done | `rfc822` depth 40 opaque, not listed; name of 100 unchanged (line 124), 101 cut (line 125), address visible | |
| M5 | done, also: a name without end tag is not searched again, otherwise a list of `<svg>` would be quadratic | all cases of 85 and 86; `iframe`, `noembed`, `noframes` reduced; `style`, `script`, `title` not | |
| M6 | done: `Multipart` with `Count` 0 and non-blank `Preamble` | no boundary and unusable boundary opaque; empty multipart and normal preamble not | |
| M7 | done as revised: opaque only for `multipart/*` and `message/rfc822` parts left at the depth limit, and M6 | `message/feedback-report`, `message/partial`, delivery report: listed, no notice | |
| M8 | done | 200 unclosed `<a name=x>`, 2,000 links: not reduced | |

### Measurements
`DOTNET_GCHeapHardLimit` 2 GB, Debug build, 1.5 MB stack, one machine. Baseline of the process about 57 MB. All inputs 100,000 unless given.

| `secprobe6` | ms | reduced | peak MB |
|---|---|---|---|
| `bangStart` (secure.3: 7.4 s for 259 KB) | 859 (first run, JIT) | yes | 60 |
| `bangStart2` | 182 | yes | 59 |
| `dashBang` | 183 | yes | 59 |
| `doctypeQuote` / `doctypeQuote2` | 169 / 159 | yes | 59 |
| `cdata` / `pi` / `bogusEnd` | 160 / 159 / 163 | yes | 59 |
| `commentSpace` / `commentNewline` / `commentOneDash` / `commentBangSpace` | 178 / 178 / 183 / 177 | no, stays one comment | 74 |
| `commentNested` / `commentEmpty` | 181 / 182 | yes | 59 |
| `unquotedQuote` / `selfClose` / `bangData` | 176 / 170 / 191 | yes | 59 |
| `upperSpace` / `endSlash` / `endNewline` | 191 / 183 / 181 | yes | 59 |
| `incompleteEnd` | 155 | no | 59 |
| `svgCdata` / `svgUpper` / `mathText` | 185 / 186 / 183 | yes | 59 |
| `xmpComment` / `plaintextComment` | 193 / 180 | no, stays one comment | 77 / 75 |
| `fmtObject` 1000 16000 (secure.3: over 5 GB) | **1,217** | yes, abort | **308** |
| `fmtTable` 1000 16000 | **1,129** | yes, abort | **314** |
| `fmtSeed` 0 18000 | 1,392 | yes | 188 |

| `secprobe4 time` | ms | peak MB |
|---|---|---|
| `scriptCommentDiv` / `styleCommentDiv` / `titleCommentDiv` / `textareaCommentDiv` | 242 / 247 / 248 / 239 | 83 |
| `scriptDataSrcDiv` / `styleDataSrcDiv` | 244 / 243 | 83 |
| `scriptCommentAttrs` / `scriptCommentStyle` | 183 / 171 | 57 |
| `scriptCommentSiblings` | 333 | 92 |
| `attrs` / `styleUnknown` | 174 / 167 | 57 |
| `formatBomb` 1000 18000 / `formatBombEsc` 1000 18000 | 1,458 / 1,134 | 189 / 135 |

`secprobe5`: `titleValueAttrs` 182, `titleValueStyle` 165, `styleValueAttrs` 180, `noRawControlAttrs` 158 ms. `secprobe4 safety`: 34 of 34 ok. `lan`: 63 blocked as before. `secprobe6 hide`: as M5, MIME without boundary opaque; `deepFlag`: opaque, not listed.
Regression: `spanDiv` 1,707, `aDiv` 1,250, `bDiv` 1,175 ms; 20,000 unknown siblings 2.1 s (slowest of all, outside the named cases). Fuzz 510,000 inputs without exception or suspicious output, 40,000 parallel runs identical; fix.2 and fix.3 self-checks unchanged except as below.

### What the budget costs ordinary mail (`_scratch\budget-probe`)
| Input | Allocated by `Sanitize` | Result |
|---|---|---|
| `<p>` x 20,000 / `<i>` x 20,000 / `<span>text` x 20,000 | 49 / 52 / 60 MB | markup kept |
| table report 3,000 rows, 410 KB | 301 MB in total | markup kept |
| inline-styled `span` or `i` x 15,000 | about 625 MB in total | markup kept |
| **inline-styled `span`, `i` or `td` x 19,000–20,000, about 500 KB** | 270 MB up to the abort | **text without markup** |
| `<td>a` outside a table x 20,000, `a</x>` x 20,000 (97 KB) | 262 MB up to the abort | text without markup |

Parsing an inline `style` allocates about 14 KB per element, and text split many times into pieces allocates quadratically. So a mail with **about 19,000 or more inline-styled tags** now falls back to plain text, below the 20,000 tags of the pre-scan. Consequence of the budget of 256 MB, built as decided. The real data (2,664 mails, largest 313,586 characters) should stay far below; `limiter-probe` shows it, its code did not need a change.

### Deviations and open points
- **M1a:** "the CSS configuration of the package" is copied, because `HtmlParser` does not expose its context. Checked by the same output for one styled case and unchanged results of the 57 payloads of `secprobe` (only the changes of L1 show in a comparison with the fix.2 state: `xmp` and `plaintext`).
- **M1a:** no notice distinguishes the abort from other limits: both use `ContentReduced`.
- **Case 80:** as decided, over the pre-scan limits the text stays with `WasReduced` true and the first 20,000 tags keep their markup. `<p>x</p>` x 100,001 ends so, without an abort.
- **M3:** the running build no longer stops when its caller is cancelled before it has started, so that the same mail selected again can use it. A selection that is already outdated when the build would start throws before starting.
- `secprobe2 sync`, `secprobe3`: older binaries must be rebuilt, because `MailContent` got new constructor parameters since fix.2.
