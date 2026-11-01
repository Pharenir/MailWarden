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
tag with what the scan read as its attributes. Comments are skipped correctly, so quotes in them do not count.

### Deviations and open points
- **K8:** no `Tag`: the entries are `ComboBoxItem` with `Content`, and `SelectedIndex` maps to a private array. A boxed enum of the Presentation assembly in `Tag` would have to cross the WinRT boundary; I could not test that without a GUI start.
- **K7, sender:** the cut keeps the address and shortens the name, so the total stays at 101 at most, unless the address has more than 90 characters. Attachment names are additionally shortened to 260 characters each (not asked for).
- **K5:** `MailContentReader` counts 25 **binary** MB, which are 34,952,558 characters; the budget of 34,000,000 is strict. A single embedded image can only reach 5 MB, but a set of inline images between about 24.3 and 25 MB loses its last image in the document (`<img>` removed, the part is not listed as an attachment). Question to the planner: reader limit to 24 MB, or accept? Not changed.
- **K13:** IP literals only. A public name that resolves to a local address (DNS rebinding) is not covered.
- **K15:** the first status of an account counts as a change. A status that is already in `CurrentStatus` at the start and comes again reloads once.
- **K6:** the rebuild no longer runs on the UI thread, so `DocumentHtml` changes later than `Execute`: tests must wait for it. `CanLoadRemoteImages` is false at once.

**Hints for the tester:** `listSize` is now an unknown key. `form` keeps its text. `DecideNavigation(string)` stays. Case 64 can observe the result only by waiting, there is no awaitable member.
