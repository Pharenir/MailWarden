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

## secure.2 (2026-10-31)

Grundlage: `git diff 2e2e8a7 9600019 -- src .gitignore`, `git show a3cf2e0`, `requirement.md` (A9–A18), `impl-notes.md`
(fix.2), vollständige Dateien bei `9600019`. `tests\`, `test-plan.md`, `test-result.md` nicht gelesen; nichts gestartet.
(Vom Planer aus der Rückmeldung des Security-Reviewers übernommen; die Rolle hat kein Schreibwerkzeug.)

**Ergebnis: 0 Kritisch · 0 Hoch · 0 Mittel · 1 Niedrig (neu: N7)** → kein Hinderungsgrund für Abschluss und Merge.
Pflichtpunkt Planer: A18-Einträge in `TODO.md` vor dem Merge.

### Niedrig
**N7 – Liegengebliebene `.tmp`-Dateien enthalten einen verschlüsselten Grant, den Abmelden/Entfernen nicht löschen**
- Ort: `src\MailWarden.Infrastructure\Security\DpapiCredentialStore.cs:102, 122-126, 205-219` (best-effort-Aufräumen),
  `:131-147` (`DeleteAsync` löscht nur `<key>.bin`).
- Szenario: Virenscanner/Indexer hält die Temp-Datei offen, während `WriteAsync` scheitert (seit A9 still ignoriert),
  oder Abbruch zwischen Schreiben und `File.Move` → `<key>.<guid>.tmp` mit DPAPI-Chiffrat bleibt unbefristet liegen,
  unsichtbar für `ListKeysAsync`. Datensparsamkeit; selten, nur mit dem Windows-Konto nutzbar.
- Empfehlung: A18-Aufräumen beim Start ausdrücklich auf `*.tmp` im Credentials-Ordner erweitern. Optional:
  `DeleteAsync` löscht zusätzlich exakt `<key>.<32 Hex>.tmp` (nicht `<key>.*.tmp` – träfe bei `a` auch `a.b`).

### Status der secure.1-Befunde
| Befund | Status | Rest |
|---|---|---|
| N1 innere Ausnahmen / Adresse | teilweise erledigt (A15), Logging-Regel vertagt (A18) | zusätzlich: innere `IOException` bei `credential_store_unavailable` enthält den vollen Pfad (Benutzername im Profilpfad) |
| N2 Google `state` / `error` | erledigt (A14) | Browser-Rundlauf ungetestet; Abbruch durch Fremdaufruf mit falschem `state` bleibt möglich (DoS, akzeptiert) |
| N3 Rechte Credentials-Ordner | vertagt (A18 → AP-13) | – |
| N4 Adressabgleich ohne Anbieter | erledigt (A12) | – |
| N5 verwaiste Grants | weitgehend erledigt (A13) | Restfenster (Fehler vor Erkennung der neuen Adresse, Re-Auth mit Caller-`ct`, Absturz) → A18-Aufräumen |
| N6 kein Widerruf beim Server | vertagt (A18) | – |

### Hinweise ohne Befund
- `DpapiCredentialStore.cs:198`: `checked((int)stream.Length)` → eingeschleuste Datei > 2 GB löst `OverflowException`
  aus, die die Token-Provider nicht abbilden; Dateien < 2 GB werden ganz geladen. Setzt Schreibrecht im Ordner voraus
  (N3); bei AP-13 Obergrenze (z. B. 1 MB) mitdenken.
- A9: prozessinterne Sperre schützt nicht prozessübergreifend (AuthProbe + App auf demselben Datenordner) – nur
  Verfügbarkeit betroffen; Mechanismus aus Security-Sicht in Ordnung.
- A10, MSAL-Speicher-Callback: Durchreichen/Einpacken nicht beobachtet; in beiden Fällen kein Fremdtext.
- A14, manuell bei Fall 30: Abbruch/Ablehnung auf der Google-Seite muss `UserCancelled`/`AccessDenied` liefern, nicht
  `state_mismatch` (RFC 6749 4.1.2.1 verlangt `state` im Fehler-Redirect).
- `.gitignore` (`a3cf2e0`): Einträge ohne führenden Schrägstrich, greifen überall; Historie ohne Credentials-Dateien.

### Geprüft und in Ordnung
- A14: CSPRNG, 256 Bit, Base64Url; je Anmeldung neu; `FixedTimeEquals`; Prüfung vor `error`/`code`; fester Satz
  `state_mismatch`; `SanitizeRedirectErrorCode` nur `[a-z_]{1,64}`.
- A13/A12: `SignOutBestEffortAsync` mit `CancellationToken.None`, schluckt nur `AuthenticationFailedException`,
  auslösende Ausnahme bleibt; kein fremder legitimer Grant wird gelöscht (eine Adresse = ein Konto laut Schema).
- A11: Entfernen schluckt nur `AuthenticationFailedException`.
- A10: fester Satz ohne Pfad/Schlüssel/Serverdaten, Code als Konstante, innere Ausnahme erhalten.
- A9: Lesen `Read|Delete`, Temp `CreateNew`/`None`/`WriteThrough`, `ZeroMemory` vor der Sperre, atomares Move,
  Freigabe im `finally`, Aufräumen verdeckt nichts (Rest N7).
- A15/A10: keine Adresse, kein Pfad, kein Token in neuen Meldungen (`git grep`).
- A16: Trennfehler nach erfolgreichem Login still ignoriert, Caller-Abbruch bleibt Abbruch.
