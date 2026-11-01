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
| `Presentation\ReadingPaneViewModel.cs:345` | `LoadRemoteImagesCommand` builds synchronously on the UI thread | `ICommand.Execute` cannot report completion; worst case see the limits below |
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
overflow, text kept. Worst cases within the limits: 20,000 unknown siblings 5.3 s, 20,000 styled siblings 4.8 s,
20,000 `span` 2.4 s. These are the only remaining long runs; `ShowAsync` runs them on the thread pool, "Bilder laden"
on the UI thread (it then freezes that long, only for such a crafted mail).

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
1. `MailHtmlSanitizer.cs:43`: `form` is "removed entirely" by D3, with its content. Some mailers wrap the whole mail body in a `form`, which would then show an empty mail. Built as the contract says. Keep it, or unwrap `form` (drop the tag, keep the text)?
2. `MailWebViewHost.cs` `OnNavigationStarting`: D4 opens every allowed target externally. `IsUserInitiated` is available and was false for a meta refresh in the probe. Should a navigation without user gesture only be blocked? No change made, because D4 does not ask for it and I could not verify a real click in the WinUI control (a click by script also reported true).
3. `MailContentReader.cs:116`: Should an image part with `Content-Disposition: attachment` and a Content-ID that the HTML refers to be embedded? Some mailers send it that way; now it is listed as an attachment and the image is removed.
