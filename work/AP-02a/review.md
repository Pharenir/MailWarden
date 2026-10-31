# AP-02a – Review

## review.1 (2026-10-31)

**Urteil: APPROVE** (keine BLOCK-Befunde; SHOULD-Punkte vor oder in der nächsten Runde klären)

Umfang: `git diff e5eea7e bed96dd -- . ":(exclude)tests"` (Core `Accounts\`, Infrastructure `Security\`, `Auth\`,
`Providers\`, csproj). Grundlage: `work\AP-02a\requirement.md` @ `bed96dd` inkl. Planer-Entscheidungen A1–A8,
`impl-notes.md`. Nichts unter `tests\` gelesen, Build nicht selbst ausgeführt (Builder meldet `-warnaserror` grün).

### Geprüft und in Ordnung
- Vertragstreue: alle Signaturen wie im Vertrag; `AccessToken.ToString()` ohne `Value`; `AuthConfiguration.ToString()`
  nur set/missing; `AuthErrorMapping`-Tabellen vollständig (MSAL-HttpRequestException → Network im Provider, dokumentiert).
- Ordnerrollen-Regeln 1–6 in Reihenfolge, Server-`Outbox` nie `FolderRole.Outbox`, Rolle nie aus Namen außer 3–5.
- `AccountService`: Add/Token/Re-Auth/Remove wie Vertrag; A3 (kein SignOut bei gespeichertem Fremdkonto) und
  A8 (Remove ohne Token-Provider) umgesetzt.
- A1 (Prefix-Regeln), A2 (Configuration bei `Load`, Provider GenericImap, nur Datei + Position), A4 (Timeout 5 min,
  optionaler Ctor-Parameter, `sign_in_timeout`, Caller-Abbruch bleibt OCE), A5 (SHA-256-Key, 71 Zeichen),
  A6 (Doku), A7 (MS-Namen case-insensitive, Screener/Quarantine exakt).
- Cancellation/Timeout: `InteractiveSignInTimeout` linked CTS mit `using`, Caller vor Timeout, Auth-Fehler werden
  durchgereicht; MSAL- und Google-Loopback-Listener bekommen das verknüpfte Token.
- Ressourcen: `ImapClient`/`SmtpClient` per `using`, `GoogleAuthorizationCodeFlow` per `using`, Klartext-Puffer
  werden genullt, ID-Token wird nicht gespeichert.
- Meldungen: feste englische Sätze (`AuthFailures`), JSON-Parserfehler ohne Inhalt, Probe-Fehler ohne Servertext.
- Schichten: Core nur BCL; Infrastructure nur die vier freigegebenen Pakete; keine AP-01-Datei geändert.
- Docstrings an allen öffentlichen Typen/Membern vorhanden.

### Befunde

**BLOCK** – keine.

**SHOULD**

1. `src/MailWarden.Infrastructure/Security/DpapiCredentialStore.cs:52` und `:107` – Lesen über
   `File.ReadAllBytesAsync` (FileShare.Read, ohne `FileShare.Delete`). Ein gleichzeitiges `WriteAsync` auf denselben
   Key scheitert dann beim `File.Move(..., overwrite: true)` mit `IOException`/`UnauthorizedAccessException`
   (Ersetzen braucht Delete-Sharing). Realistisch ab AP-03 (parallele Token-Anfragen eines Google-Kontos: ein Aufruf
   liest, ein anderer schreibt nach Refresh). Inkonsistent zu `FileEmlStore.OpenReadAsync` (AP-01), das genau dafür
   `FileShare.Read | FileShare.Delete` setzt. Vorschlag: Lesen per `FileStream` mit `FileShare.Read | FileShare.Delete`
   (oder eine `SemaphoreSlim` je Store-Instanz). Folge davon und allgemein: IO-Fehler des Stores verlassen
   `ITokenProvider` roh, obwohl `ITokenProvider.cs:6-7` „All failures surface as AuthenticationFailedException“
   zusagt → entweder auf `AuthenticationFailedException(Unknown)` abbilden oder die Doku um „IO-Fehler des
   Credential-Stores propagieren“ ergänzen (Meldung muss zum Verhalten passen).

2. `src/MailWarden.Infrastructure/Auth/MicrosoftTokenProvider.cs:127` (`SignOutAsync` → `GetApplication()` →
   `RequireMicrosoftClientId()`) vs. `GoogleTokenProvider.cs:120-124` (SignOut ohne Konfiguration). Fehlt die
   Microsoft-Client-ID, wirft `SignOutAsync` `Configuration`; `AccountService.RemoveAccountAsync`
   (`AccountService.cs:142-147`) bricht damit **vor** `DeleteAsync` ab – ein Microsoft-Konto lässt sich dann nicht
   entfernen, ein Google-Konto schon. Gleiche Funktion, unterschiedliches Verhalten ohne fachlichen Grund.
   Vorschlag (Planer entscheidet): MS-SignOut bei fehlender Konfiguration als No-op (der Cache ist ohne Client-ID
   ohnehin nicht nutzbar) oder Remove-Reihenfolge/Behandlung im `AccountService` festlegen.

3. Doppelung atomares Schreiben: `DpapiCredentialStore.cs:89-113` wiederholt das Muster aus
   `FileEmlStore.cs:29-68` (AP-01: eindeutige Temp-Datei, `CreateNew`, Flush, `File.Move(overwrite)`, Aufräumen).
   Zusätzlich weicht das Aufräumen ab: `DpapiCredentialStore.cs:112` ruft `File.Delete` ungeschützt im `finally` und
   kann so die ursprüngliche Exception überdecken; `FileEmlStore.DeleteTemporaryFile` ist bewusst best effort.
   Vorschlag: internen Helfer (z. B. `Infrastructure\Storage\AtomicFile.WriteAsync(path, bytes|stream, ct)`) für
   beide; bleiben soll die `FileEmlStore`-Variante (best-effort-Cleanup, `Flush(flushToDisk: true)`). Da
   `FileEmlStore` AP-01-Code ist → Planer-Freigabe nötig; mindestens das Cleanup in `DpapiCredentialStore`
   jetzt angleichen.

4. Plan: `PLAN.md` hat keine Sollverhalten-Tabelle für AP-02a (nur Zeile AP-02 in der Übersicht; Projekt-CLAUDE.md:
   „Ablage Sollverhalten: PLAN.md, Tabelle je AP“). Kein Builder-Fehler – Planer-Aufgabe (siehe Fragen).

**NIT**

5. `src/MailWarden.Core/Accounts/AccountService.cs:118` und `:64` – Adressabgleich ignoriert den Provider: Meldet
   sich beim Re-Auth eines Microsoft-Kontos eine Adresse an, die als **Google**-Konto gespeichert ist, bleibt der
   neue MS-Grant verwaist im `msal.cache`; `AddAccountAsync` gibt bei Adressgleichheit still ein Konto eines anderen
   Providers zurück. Randfall (MSA mit Fremdadresse); Vorschlag: zusätzlich `Provider` vergleichen bzw. im
   anderen Fall explizit fehlschlagen.
6. `src/MailWarden.Infrastructure/Auth/MicrosoftTokenProvider.cs:39/76` – Thread-Sicherheit: MSAL serialisiert
   Before/After-Access je Client (Ich bin mir unsicher, ob 4.92 das auch für reine Lesezugriffe wie
   `GetAccountsAsync` tut). Das Instanzfeld `storedCacheUnreadable` wird nach einem separaten MSAL-Aufruf gelesen;
   bei parallelen Aufrufen kann es aus einem fremden Ladevorgang stammen – wirkt sich nur auf den Fehlercode aus
   (harmlos). Wichtiger: zwei `MicrosoftTokenProvider`-Instanzen auf demselben Store überschreiben sich gegenseitig
   `msal.cache` (last writer wins, Grant geht verloren) → in der Klassendoku „eine Instanz je Credential-Store“
   festhalten (AP-04 registriert als Singleton).
7. `src/MailWarden.Infrastructure/Providers/ProviderProfiles.cs:80` – Exception-Text enthält die vollständige
   E-Mail-Adresse (personenbezogen, landet ggf. in Logs). Vorschlag: Adresse weglassen.
8. `src/MailWarden.Core/Accounts/AccountService.cs:13` – „signed in as other account“ → „as a different account“
   (Vertragswortlaut; falls Tests den Text prüfen, nur gemeinsam ändern).
9. `src/MailWarden.Infrastructure/Auth/GoogleTokenProvider.cs:31` – `MissingCodeError` heißt anders als die
   übrigen `*Code`-Konstanten (`MissingAuthorizationCodeCode` o. ä.).
10. `src/MailWarden.Infrastructure/Providers/ConnectionProbe.cs:69/80` – `catch (Exception)` macht auch
    Programmierfehler zu einem `Error`-Satz („… failed (TypeName)“), und ein Fehler in `DisconnectAsync`
    (`:126`, `:142`) setzt `ImapOk`/`SmtpOk` auf false, obwohl der Login gelungen ist. Für eine Diagnose-Probe
    vertretbar; ggf. Disconnect-Fehler ignorieren.
11. Kein zentraler Ort für `<Root>\credentials` und `<Root>\auth-config.json` (DataPaths ist AP-01 und gesperrt) –
    jede Composition Root baut die Pfade selbst. Siehe Fragen.

### Fragen an den Planer (Repo)
- PLAN.md: Abschnitt „### AP-02a Konten + Anmeldung“ mit den Fällen 1–30 (+25a, A1–A8) und Spalten
  Umgesetzt/Getestet nachtragen; AP-02-Zeile in der Übersicht auf AP-02a/AP-02b aufteilen.
- SHOULD 2: Soll `RemoveAccountAsync` ein Konto auch ohne Provider-Konfiguration löschen können (MS-SignOut als
  No-op) – ja/nein?
- SHOULD 3: Freigabe, `FileEmlStore` (AP-01) auf einen gemeinsamen Atomic-Write-Helfer umzustellen, oder als
  TODO für später?
- NIT 11: `DataPaths` um `CredentialsDirectory` und `AuthConfigPath` erweitern (in AP-02b/AP-04, AP-01-Datei)?
- NIT 5: Adressabgleich in `AccountService` providerbezogen machen (Vertragsänderung)?

### Getestet / ungetestet (Reviewer)
- Getestet: nichts ausgeführt (reines Code-Review am eingefrorenen Stand `bed96dd`).
- Ungetestet/vermutlich korrekt: Verhalten von MSAL/Google-Listenern bei Timeout, Windows-Sharing-Verhalten bei
  SHOULD 1 (aus der Dokumentation von `MoveFileEx`/FileShare abgeleitet, nicht reproduziert).

## review.2 (2026-10-31)

**Urteil: APPROVE** (keine BLOCK-Befunde; ein SHOULD, gern noch vor dem Merge, sonst als TODO)

Umfang: `git diff 2e2e8a7 9600019 -- src` (fix.2, Commit `89dc243`) plus Wechselwirkungen mit dem unveränderten Code.
Grundlage: `requirement.md` @ `9600019` (A9–A18), `impl-notes.md` Abschnitt fix.2, review.1, secure.1. Nichts unter
`tests\` gelesen, kein `test-plan.md`/`test-result.md`. Build nicht selbst ausgeführt (Builder meldet `-warnaserror` grün).

### A9 – Abweichung im Mechanismus: akzeptiert
- Die Begründung stimmt: Ersetzen per `File.Move(overwrite)` wird bei offenem Ziel-Handle auch mit `FileShare.Delete`
  verweigert. `FileEmlStore.cs:84-85` (AP-01) dokumentiert genau das; meine Annahme in review.1 SHOULD 1 („Delete-Sharing
  reicht“) war falsch. Die `SemaphoreSlim` je Instanz war dort schon als Alternative genannt.
- Sperrreihenfolge: MSAL hält seine Cache-Sperre während Before/AfterAccess und ruft darin den Store auf
  (MSAL → Store). Der Store ruft in seiner Sperre nichts Fremdes auf (kein Callback, kein zweiter Store-Aufruf);
  Google ruft Lesen und Schreiben nacheinander auf, nicht verschachtelt. Kein Zyklus, also kein Deadlock.
- Async-Sperre: `WaitAsync(ct)` steht jeweils **vor** dem `try` (`DpapiCredentialStore.cs:57, 103, 134`). Ein Abbruch
  beim Warten gibt nichts frei, was nicht erworben wurde. `Release` im `finally` deckt den Not-found-Return (`:64`),
  Abbruch und IO-Fehler ab. DPAPI läuft außerhalb der Sperre (`:74`, `:94`), die Sperre ist also kurz.
- Dispose: Die Klasse ist nicht `IDisposable`, `AvailableWaitHandle` wird nie benutzt → die Semaphore braucht kein
  Dispose. In Ordnung.
- `ListKeysAsync` ohne Sperre ist unkritisch: Move ist atomar, `.tmp` wird gefiltert.
- Die Restlücke (mehrere Instanzen/Prozesse auf einem Ordner) ist in den Remarks dokumentiert (`:15-18`).

### A10–A17 geprüft und in Ordnung
- A10: Der Filter `IOException or UnauthorizedAccessException` (`AuthFailures.cs:58-61`) erfasst keine
  `OperationCanceledException`, die Cancellation-Semantik bleibt also erhalten. `CredentialStoreException` wird jeweils
  vorher gefangen (`GoogleTokenProvider.cs:293`, `MicrosoftTokenProvider.cs:196`), die inner exception bleibt erhalten.
  Abgedeckt sind Google Lesen/Schreiben/SignOut und die MSAL-Load/Save-Callbacks. `AuthenticationFailedException`
  läuft unverändert durch die beiden `TranslateFailuresAsync` und `InteractiveSignInTimeout`.
- A11: Nur `AuthenticationFailedException` wird geschluckt (`AccountService.cs:182`), OCE und andere Fehler
  propagieren und das Konto bleibt. Microsoft ohne Konfiguration (`GetApplication` wirft im async-Kontext) wird
  dadurch abgedeckt.
- A12/A13 Reihenfolge: Anmeldung → Suche → gleicher Provider: bestehendes Konto / anderer Provider: SignOut + AccessDenied
  `address_in_use` / neu: Upsert, bei Fehler SignOut + Rethrow (`AccountService.cs:69-103`). Re-Auth-Guard
  providerbezogen (`:151`). SignOut läuft auf dem Provider, der den Grant erzeugt hat; der Grant des anderen Providers
  bleibt unberührt.
- A14: Der State wird vor `error`/`code` geprüft (`GoogleTokenProvider.cs:161-165`). `FixedTimeEquals`, 32 Byte aus
  `RandomNumberGenerator`, Base64Url. Ein ungültiger `error` wird null → `Unknown`.
- A15: In `src` gibt es keine interpolierte Adresse mehr in Meldungen (git grep @ `9600019`).
- A16: `DisconnectIgnoringFailuresAsync` (`ConnectionProbe.cs:154-164`) wirft nur einen Abbruch des Aufrufers weiter,
  gleiche `IsCancellation`-Regel wie bei IMAP/SMTP.
- A17: Remarks und Umbenennung wie entschieden.

### Befunde

**BLOCK** – keine.

**SHOULD**

1. `src/MailWarden.Core/Accounts/AccountService.cs:150-154` – Re-Auth verwirft den falschen Grant anders als
   `AddAccountAsync` (`:83`, `:99`, Helfer `:193-203`), ohne fachlichen Grund. Beide Fälle sind „frischer Grant, der
   nicht bleiben darf“. Re-Auth ruft `SignOutAsync(signedInAddress, ct)` direkt auf:
   (a) Eine `AuthenticationFailedException` des SignOut (seit A10 z. B. `credential_store_unavailable`) ersetzt die
   eigentliche Meldung „anderes Konto angemeldet“ (AccessDenied); die Meldung passt dann nicht mehr zum Vorgang.
   (b) Bricht der Aufrufer nach erfolgreicher Anmeldung ab, bleibt der falsche Grant liegen. `SignOutBestEffortAsync`
   verwendet genau deshalb `CancellationToken.None`.
   Vorschlag: In `:153` `SignOutBestEffortAsync(tokenProvider, signedInAddress)` verwenden; Doku `:122-126` bleibt.
   Testfälle 25/25a sind davon nicht betroffen.

**NIT**

2. `AccountService.cs:74` und `:150` – `FindByAddressAsync(…, ct)` liegt zwischen Anmeldung und Aufräumen. Ein
   Abbruch oder Store-Fehler genau dort lässt den frischen Grant einer neuen Adresse liegen. A13 deckt nur das Speichern
   ab. Das ist ein Randfall und gehört zum TODO „verwaiste Grants beim Start aufräumen“ (A18).
3. `AccountService.cs:184` – Der Kommentar „a grant left behind is unusable without it“ stimmt nur für MailWarden. Durch
   A10 × A11 bleibt bei einem IO-Fehler (Virenscanner/Backup hält die Datei) der verschlüsselte Refresh-Token auf der
   Platte (Google `google.<hash>.bin`, Microsoft `msal.cache` nach `RemoveAsync` ohne Speichern) und ist serverseitig
   weiter gültig. Vorschlag: Kommentar entsprechend formulieren und diesen Weg im A18-TODO „verwaiste Grants“
   ausdrücklich nennen. Ebenso `:191-192`: Geschluckt wird nur `AuthenticationFailedException`, andere Fehler ersetzen
   den Ausgangsfehler weiterhin. Mit den echten Providern ist das unkritisch, der Kommentar sagt aber mehr zu.
4. `DpapiCredentialStore.cs:15-18` – „Genau eine Instanz je Ordner“ ist jetzt eine harte Bedingung für die
   Composition Root (AP-04): Beide Token-Provider müssen **dieselbe** Store-Instanz bekommen. Gehört zusammen mit
   A17 (MS-Provider als Singleton) in den AP-04-Hinweis bzw. ins TODO.
5. A18: `TODO.md` @ `9600019` enthält die A18-Punkte noch nicht (Builder-Notiz sagt, dass der Planer sie nachträgt).
   Das ist eine Aufgabe für den Planer, kein Builder-Fehler.

### Getestet / ungetestet (Reviewer)
- Getestet: nichts ausgeführt (reines Code-Review am eingefrorenen Stand `9600019`).
- Ungetestet/vermutlich korrekt: ob MSAL Ausnahmen aus dem AfterAccess-Callback unverändert durchreicht (Builder hat
  nur Load beobachtet); der State-Roundtrip mit dem echten Google-Redirect (Fall 28/30).
