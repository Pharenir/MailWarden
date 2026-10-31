# AP-02a – Security-Review

## secure.1 (2026-10-31)

Grundlage: `git diff e5eea7e bed96dd -- . ":(exclude)tests"`, `requirement.md` (inkl. A1–A8), `impl-notes.md`,
AuthProbe `_scratch\auth-probe\` (nur gelesen, nicht gestartet). `tests\`, `test-plan.md`, `test-result.md` nicht
gelesen. Maßstab: OWASP Top 10, DSGVO, Sicherheitsprofil der Projekt-CLAUDE.md.
(Vom Planer aus der Rückmeldung des Security-Reviewers übernommen; die Rolle hat kein Schreibwerkzeug.)

**Ergebnis: 0 Kritisch · 0 Hoch · 0 Mittel · 6 Niedrig** → aus Security-Sicht kein Hinderungsgrund für den Abschluss.

### Kritisch
keine

### Hoch
keine

### Mittel
keine

### Niedrig

**N1 – Innere Ausnahmen tragen ungefilterte Server-/Bibliothekstexte**
- Ort: `src\MailWarden.Infrastructure\Auth\MicrosoftTokenProvider.cs:263, 267, 272`;
  `src\MailWarden.Infrastructure\Auth\GoogleTokenProvider.cs:323`; `src\MailWarden.Core\Accounts\AuthenticationFailedException.cs:23`;
  zusätzlich `src\MailWarden.Infrastructure\Providers\ProviderProfiles.cs:80` (vollständige Adresse in der Meldung).
- Szenario: Sobald AP-02b/AP-03 Fehler mit `exception.ToString()` nach `<daten>\logs\` schreibt, landen die Message der
  inneren `MsalServiceException`/`TokenResponseException` (error_description des Servers, ggf. Login-Name/Adresse)
  und Stacktraces im Log. Tokens sind nicht enthalten. Heute gibt es noch keine Log-Ausgabe.
- Empfehlung: Logging-Regel für AP-02b festhalten: bei `AuthenticationFailedException` nur `Provider`, `Reason`,
  `ProviderErrorCode`, `Message`, Typ der inneren Ausnahme; nie `ToString()`. In `ProviderProfiles.ExtractDomain` die
  Adresse nicht in die Meldung übernehmen.

**N2 – Google-Loopback-Redirect ohne eigenen `state`; `error` aus der Redirect-URL ungeprüft**
- Ort: `src\MailWarden.Infrastructure\Auth\GoogleTokenProvider.cs:135-154`
- Szenario: Ein lokaler Prozess oder eine Webseite, die den zufälligen Port findet, ruft während der Anmeldung
  (max. 5 min) `http://127.0.0.1:<port>/authorize/?error=…` oder `?code=<fremder Code>` auf. Folge: Anmeldung bricht ab
  (DoS) oder, falls Google den Code-Verifier nicht erzwingt, wird das Postfach des Angreifers als Konto hinzugefügt
  (Login-CSRF). Der beliebige `error`-Wert landet als `ProviderErrorCode` in Ausgabe/UI. PKCE und das validierte
  ID-Token begrenzen das stark. Unklar, ob `LocalServerCodeReceiver` (1.78.0) `state` selbst setzt/prüft; der Code
  hier tut es nicht.
- Empfehlung: `authorizationRequest.State` auf 32 zufällige Bytes (Base64Url) setzen und `authorizationResponse.State`
  mit `CryptographicOperations.FixedTimeEquals` vergleichen, sonst `AuthFailures.Create(Google, Unknown,
  "state_mismatch")`. `ProviderErrorCode` aus der Redirect-URL auf `[a-z_]{1,64}` begrenzen, sonst `null`. (MSAL prüft
  `state` und PKCE intern.)

**N3 – Credentials-Ordner erbt die Rechte des Installationsordners**
- Ort: `src\MailWarden.Infrastructure\Security\DpapiCredentialStore.cs:90`; Datenordner `<exe>\data`
  (`src\MailWarden.App\App.xaml.cs:41`).
- Szenario: Liegt die Installation z. B. auf `D:\Tools\MailWarden` (Laufwerkswurzel mit „Authenticated Users:
  Modify“), können andere lokale Konten die `.bin`-Dateien lesen, löschen oder ersetzen. Entschlüsseln können sie sie
  nicht (DPAPI CurrentUser), möglich sind aber DoS (erneute Anmeldung nötig) und Offline-Material für jemanden, der das
  Benutzerkennwort kennt. Gilt sinngemäß für `auth-config.json` (Google-Client-Secret).
- Empfehlung: Beim Anlegen des Credentials-Ordners eine ACL nur für den aktuellen Benutzer (+ SYSTEM) ohne Vererbung
  setzen, oder den Standard-Datenordner nach `%LOCALAPPDATA%\MailWarden` legen. Spätestens mit dem Installer (AP-13)
  entscheiden.

**N4 – Adressabgleich ignoriert den Anbieter**
- Ort: `src\MailWarden.Core\Accounts\AccountService.cs:64-68, 118, 171-176`
- Szenario: Ein Google-Konto mit externer Adresse oder ein MSA mit Gmail-Adresse meldet sich an. `AddAccountAsync` gibt
  dann das bestehende Konto des *anderen* Anbieters zurück, und der frische Grant bleibt verwaist im Store. A3 meldet
  ein Konto mit gleicher Adresse beim anderen Anbieter nicht ab.
- Empfehlung: Adresse **und** `Provider` vergleichen.

**N5 – Verwaiste Grants bei Teilfehlern (DSGVO, Datensparsamkeit)**
- Ort: `src\MailWarden.Core\Accounts\AccountService.cs:60-72`; `src\MailWarden.Infrastructure\Auth\GoogleTokenProvider.cs:171`
  (bzw. MSAL-`SaveCacheAsync`).
- Szenario: Der Token-Provider speichert den Grant schon während `SignInInteractiveAsync`. Scheitert danach
  `UpsertAsync` (oder Abbruch/Absturz), bleibt ein verschlüsselter Refresh-Token zu einem Konto zurück, das MailWarden
  nicht kennt und nie löscht.
- Empfehlung: In `AddAccountAsync` bei Fehler nach erfolgreicher Anmeldung (für neue Adressen)
  `SignOutAsync(signedInAddress)` im catch aufrufen. Aufräumen verwaister Grants beim Start als TODO.

**N6 – Entfernen widerruft den Grant nicht beim Server (A8, akzeptiert)**
- Ort: `src\MailWarden.Infrastructure\Auth\GoogleTokenProvider.cs:120-124`;
  `src\MailWarden.Infrastructure\Auth\MicrosoftTokenProvider.cs:124-142`
- Szenario: Eine vor dem Entfernen kopierte `google.<hash>.bin` (z. B. aus einem Backup) bleibt mit dem Benutzerkonto
  entschlüsselbar und der Refresh-Token weiter gültig.
- Empfehlung: Wie in A8 geplant in einem späteren AP `https://oauth2.googleapis.com/revoke` aufrufen (Microsoft:
  Hinweis auf die Consent-Verwaltung, steht schon in `PRIVACY.md`).

### Hinweise ohne Befund
- `.gitignore`: `auth-config.json` und `credentials/` sind nur unter `data/`, `bin/`, `_scratch/` abgedeckt. Zwei
  Zeilen ergänzen, damit eine falsch abgelegte Datei nicht ins öffentliche Repo kommt. Historie nach typischen
  Google-/Token-Präfixen durchsucht: nichts gefunden.
- Der A4-Timeout bricht nur ab, wenn MSAL bzw. `LocalServerCodeReceiver` das Abbruch-Token beachten; nicht beobachtet.
  Die Variante „Tab schließen“ in Fall 30 deckt das ab.
- AuthProbe `Program.cs:140` gibt Ordnernamen vom Server ungefiltert auf die Konsole aus (Steuerzeichen möglich); nur
  Scratch, unkritisch.
- Manueller Zusatzcheck bei Fall 28/29: `%APPDATA%\Google.Apis.Auth` darf **nicht** entstehen (kein
  Klartext-FileDataStore).

### Geprüft und in Ordnung
- `DpapiCredentialStore`: DPAPI `CurrentUser` + Entropy `MailWarden.Credentials.v1` (nur Domänentrennung); Klartextkopie
  per `ZeroMemory` gelöscht; Temp-Datei im selben Ordner, `CreateNew`/`FileShare.None`/`WriteThrough`, nur Chiffrat,
  `File.Move(overwrite)` atomar; Key-Allowlist `[A-Za-z0-9._-]{1,128}` → kein Traversal, kein ADS; Meldungen nennen nur
  Key (Google: Hash), nie Bytes.
- Token-Caches: MSAL-Cache nur über `ICredentialStore`, kein MSAL-PII-Logging; Google ohne DataStore, ID-Token vor dem
  Speichern entfernt, Key = SHA-256 der normalisierten Adresse (pseudonym, nicht anonym).
- ID-Token: `GoogleJsonWebSignature.ValidateAsync` mit `Audience = clientId`, zusätzlich `email_verified`; Adresse nie
  aus dem Hint. PKCE S256; MSAL `consumers`, System-Browser, `state`/PKCE intern. Listener nur auf Loopback.
- A3 korrekt; A4 Timeout 5 min, Caller-Abbruch bleibt OCE.
- Fehlertexte: feste Sätze, `AccessToken.ToString()` ohne `Value`, `AuthConfiguration.ToString()` nur set/missing,
  JSON-Fehler nur Datei + Position, `ConnectionProbe.Describe` nur Host:Port, Typname, Socket-Code.
- TLS: MailKit-Standardvalidierung, Sperrprüfung an, `StartTls` Pflicht, XOAUTH2 nur über TLS, kein `ProtocolLogger`.
- AuthProbe gibt nie Token-Werte aus.
- Pakete: Google.Apis.Auth 1.78.0, MailKit 4.18.2, Microsoft.Identity.Client 4.92.0, ProtectedData 10.0.14 –
  `dotnet list package --vulnerable --include-transitive` (2026-10-31): keine anfälligen Pakete.

### Einstiegspunkte
| Einstiegspunkt | Erreichbar | Gewollt |
|---|---|---|
| MSAL-Loopback-Listener `http://localhost:<zufällig>/` | nur lokal, nur während `SignInInteractiveAsync`, max. 5 min | ja (Sicherheitsprofil) |
| Google-Loopback-Listener `http://127.0.0.1:<zufällig>/authorize/` | nur lokal, nur während der Anmeldung, max. 5 min | ja; `state` fehlt (N2) |
| `auth-config.json` / Umgebungsvariablen | lokal, Benutzerrechte | ja |
| AuthProbe-Konsole (`_scratch\`, nicht ausgeliefert) | lokal, nur Dennis | ja |
| Öffentliche Netzwerk-Einstiegspunkte | keine | – |
