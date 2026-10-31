# TODO – MailWarden

## Offen
- [ ] **Dennis: manuelle Tests nach `work\manual-tests.md`** (AP-01 Fall 30, AP-02a Fälle 28–30, AP-03a Fall 28,
  Begründungstexte AP-07a/b) – ersetzt die Einzelpunkte unten; „fertig“ erst danach
- [ ] AP-13: `.ico` aus `assets\mailwarden-512.png` erzeugen (Exe-Icon, Installer)
- [ ] AP-13: WindowsAppSDK-Metapaket zieht ~38 MB KI-Komponenten (onnxruntime/DirectML) → nur benötigte Teilpakete? (Paketentscheidung Dennis)
- [ ] AP-04: ungültiges `MAILWARDEN_DATA_DIR` beendet die App beim Start stumm → Logger + Meldungsfenster
- [ ] AP-03/AP-04: EML überschreiben scheitert bei offenem Leser → Lesebereich lädt EML in den Speicher, Befehlswarteschlange wiederholt

### Vertagt aus AP-02a (A18, secure.1/2, review.2)
- [ ] Gemeinsamer Atomic-Write-Helfer für `FileEmlStore`/`DpapiCredentialStore` (berührt AP-01-Code)
- [ ] `DataPaths.CredentialsDirectory` / `AuthConfigPath` (AP-02b/AP-04)
- [ ] AP-04 Composition Root: genau **eine** `DpapiCredentialStore`-Instanz je Ordner, beide Token-Provider teilen sie;
  ein `MicrosoftTokenProvider` je Store
- [ ] Logging-Regel (AP-02b/AP-04): bei `AuthenticationFailedException` nur Provider, Reason, ProviderErrorCode, Message,
  Typ der inneren Ausnahme – nie `ToString()` (innere Meldungen enthalten Servertexte bzw. Dateipfade mit Benutzername)
- [ ] AP-13: Obergrenze für Credential-Dateien (z. B. 1 MB, heute `OverflowException` > 2 GB)
- [ ] Widerruf beim Server beim Entfernen eines Kontos (Google revoke-Endpunkt)
- [ ] Aufräumen verwaister Grants beim Start, **einschließlich `*.tmp`** im Credentials-Ordner (secure N5/N7)
- [ ] Zugriff mehrerer Prozesse auf denselben Credentials-Ordner (AuthProbe + App) ist ungeschützt (A9: Sperre nur im Prozess)
- [ ] AP-03: lokale Mail-Dateien beim Entfernen eines Kontos löschen (Non-goal AP-02a)

### Vertagt aus AP-07a → AP-07b
- [ ] `dmarc=bestguesspass` (Outlook) an echten Headern prüfen (C1)
- [ ] `display-name-mismatch` schärfen (`J.Doe`, `Mr.Smith` → falsche +20), z. B. bekannte TLD-Liste (C4)
- [ ] authserv-id gegen Provider-Profil prüfen; vollständige Public Suffix List statt eingebauter Liste (C16)
- [ ] Grenzfall `@`-Wort, das selbst `key=value` ist → fail-safe Domain null? (C24)

### Befunde PstProbe 2026-10-31 (für AP-07b, anonymisiert)
Werkzeug `_scratch\PstProbe` (XstReader.Api, Freigabe Dennis; nur lokal), Quelle `K:\Projekte\_private\MailWarden\backup.pst`,
Ausgabe `K:\Projekte\_private\MailWarden\probe-out\`. 2.215 empfangene Mails (+68 gesendete), davon 345 mit
Original-Headern (Outlook.com); 1.870 ohne Header (vermutlich IMAP-/Gmail-Ordner) → für AR-Auswertung unbrauchbar. Adressbuch simuliert aus
43 Empfängern gesendeter Mails.
- [ ] Outlook.com schreibt in 300 von 345 Headern **keine authserv-id** (Header beginnt mit `spf=…`), sonst
  `mx.microsoft.com 1;` → Prüfung der authserv-id gegen das Provider-Profil (C16) taugt für Outlook nicht; Vertrauen
  über „oberster Header“ beibehalten
- [ ] `dmarc=bestguesspass` 5× (immer `dkim=none`, `compauth=pass reason=109`) → heute `Unknown`; durch SPF-Pass trotzdem
  authentifiziert. Entscheidung: wie `none` behandeln? (C1)
- [ ] `compauth` (Microsoft Composite Auth) wird nicht ausgewertet: 1 Kontakt-Mail mit `dmarc=fail`, `compauth=pass
  reason=105` landet als LikelySpam (Fehlalarm). `compauth`/`reason` als Signal oder Gegengewicht prüfen
- [ ] `dkim=timeout` (3×) → `Unknown`; ggf. wie `temperror`
- [ ] `link-domain-mismatch` feuert bei fast allen legitimen Newslettern (z. B. 165/165, 26/27 in zwei Ordnern;
  Tracking-Links) → Punkte/Bedingung kalibrieren
- [ ] `random-domain`: Abkürzungs-Domains aus 5 Konsonanten (Score 0,78, knapp unter 0,8) treffen legitime Kontakte
  (+15) → Abkürzungen berücksichtigen
- [ ] Spam im Junk-Ordner: 6 Mails → 1 CertainSpam, 3 LikelySpam, 2 LikelyLegit. Gefälschte `header.from`-Werte wie
  `--xxxx.yyyy.site` oder `AAA.comXXXX…` → künstliche Fixtures daraus bauen
- [ ] `display-name-mismatch` hat in diesen Daten nie gefeuert (C4 hier nicht belegt)
- [ ] Kein Parser-Ausfall: in keinem Header fehlte eine Methode, die im Rohtext stand
- Freigabe Dennis 2026-10-31: zur Feinabstimmung der Filter darf die Probe auch Mail-Bodys auswerten, solange davon
  nichts ins Repo gelangt (nur anonymisierte Kennzahlen bzw. daraus abgeleitete künstliche Fixtures).

## Entschieden (2026-10-31)
- Name/Ablage `K:\Projekte\Windows-Tools\MailWarden`, Lizenz PolyForm Noncommercial 1.0.0
- **Mit Git**: öffentliches GitHub-Repo `Pharenir/MailWarden` (Google verlangt Homepage-, Datenschutz- und
  Nutzungsbedingungs-URL → `README.md`, `PRIVACY.md`, `TERMS.md`).
- UI WinUI 3 · Gmail per OAuth · Daten unter `<install>\data\` · Installer (Inno Setup) als AP-13
- Drei-Rollen-Pipeline ab AP-01: Builder opus (Modellregel: nie höher als Koordinator, `~/.claude/refs/agents.md`),
  Tester sonnet, Reviewer opus; Abschluss delegiert bei PASS+APPROVE; Merge nach `main`/Push nur mit CPM; max. 4 Runden
- Gruppenansichten: Posteingang, Gesendet, Postausgang (lokale Versandwarteschlange), Screener, Junk, Quarantäne – jeweils über alle Konten und je Konto
- Screener: `LikelyLegit` landet im Screener; Posteingang nur bekannte Absender; „kein Junk“ → Absender ins Adressbuch
- AP-07a und AP-02a parallel zu AP-01 vorgezogen (Dennis 2026-10-31)
- Erster Abgleich lädt **alle** Mails: `SyncOptions.InitialSyncDays` Standard 0 statt 90 (Dennis 2026-10-31, vor dem
  Merge in AP-03a umgesetzt)
- Datenordner/Credentials ohne eigene ACL und am bisherigen Ort: Dennis ist einziger Benutzer des Rechners (Entscheidung
  zu secure M2 AP-03a und AP-02a-ACL-Punkt, 2026-10-31) → bewusst akzeptiert, keine Änderung

## Testzähler
Testzähler: 0/4 seit letztem S3 (zuletzt 2026-10-31: S3 auf `main` @ `e0481ef` nach Merge von AP-07b und AP-03a,
752/752 – Core.Tests 360, Infrastructure.Tests 392; Bericht `work\S3-main-2026-10-31-b.md`)

## Stand 2026-10-31 (Session 2, Ende)
**CPM erledigt:** `chore/app-icon`, AP-01, AP-07a, AP-02a per `--no-ff` nach `main` gemergt, Build grün
(`-warnaserror`), S3 542/542 grün, `main` @ `e45b892` gepusht. Konflikte wie erwartet: beide `requirement.md` →
Fassung des Feature-Branches; `README.md` → Status, Projektstruktur und Testbeschreibung von AP-07a und AP-02a
zusammengeführt. „Fertig“ im Sinne von Dennis erst nach seinen manuellen Prüfungen (siehe „Offen“).
- **AP-01** `feat/ap-01-skeleton-storage` @ `cf788ec`: verify.2 100/100, review.2 APPROVE (NIT 1/3 vom Planer als
  Kommentar behoben, NIT 2 Index `messages.account_id` bewusst nicht – bei Bedarf Migration v2), README, `.env.example`
  korrigiert, PLAN-Spalten gefüllt. Offen nur Fall 30 (Dennis, App starten).
- **AP-07a** `feat/ap-07a-classifier-core` @ `40e5970`: 4 Runden (Budget voll). review.1 BLOCK (Parser zerschnitt
  Property-Werte → SPF-Domain fälschbar) → C10–C27; verify.4 299/299, review.4 APPROVE; AP-01 hineingemergt,
  Integrationslauf 399/399. Doku `docs\classifier.md` (lokal) + README. Offen: Dennis prüft Begründungstexte.
- **AP-02a** `feat/ap-02a-accounts-auth` @ `d92873e`: 3 Runden. A1–A20; verify.3 243/243, review.3 APPROVE,
  secure.2 0/0/0/1 (N7 → TODO). AP-01 hineingemergt. Doku `docs\auth-setup.md` Abschnitt 4 (lokal), README,
  `.env.example` um Google-Variablen ergänzt. Offen: Dennis Fälle 28–30 mit AuthProbe.

## Übergabe – so geht es weiter
1. CPM Sitzung 3 erledigt (2026-10-31): `chore/session-handover`, AP-07b, AP-03a per `--no-ff` nach `main`; README-Konflikt
   (Statussatz) zusammengeführt; Build grün, S3 752/752, `main` @ `e0481ef` gepusht. SyncProbe nach
   `_scratch\sync-probe` gerettet und gebaut. Dieser Handover + `work\manual-tests.md` liegen auf
   `chore/manual-test-list` (lokal) → beim nächsten CPM mit nach `main`.
2. Erledigt: gemergte Worktrees/Branches entfernt. Läuft: AP-03a fix.3 (siehe „Worktrees“).
3. Nächste APs: AP-03b (Befehlswarteschlange, IDLE, Reconnect; Punkte „Vertagt aus AP-03a“), danach AP-07c
   (Klassifizierer an Mails/Ordner); AP-04 (UI inkl. Konten-UI). Vor Start: Anforderung je AP (Planer).
4. Aufgeräumt 2026-10-31 (Freigabe Dennis): alle sechs AP-Worktrees und sieben Branches (inkl. `chore/app-icon`)
   entfernt, leere Datei `AuthenticationResultsParser.cs` gelöscht; Probe-DBs gab es keine mehr. AuthProbe und
   ClassifierProbe vorher nach `_scratch\auth-probe` bzw. `_scratch\ClassifierProbe` im Hauptordner gerettet.
   `MailWarden.sln` ohne BOM halten (`dotnet sln` fügt es wieder ein).

## Worktrees (alle unter `.worktrees\`, git-ignoriert)
AP-03a/AP-07b-Worktrees und -Branches sowie `chore/session-handover` entfernt (Freigabe Dennis 2026-10-31).
AP-03a fix.3 (verlorene UIDs, Freigabe Dennis), abgezweigt von `main` @ `e0481ef`, Anforderung `7b1135c`:
| Worktree | Branch |
|---|---|
| `fix-ap-03a` | `fix/ap-03a-missed-uids` |
| `test-ap-03a-fix` | `test/ap-03a-missed-uids` |
Fall 30 (AuthProbe abbrechen) bleibt vorerst offen (Dennis 2026-10-31).

## Session 3 (2026-10-31)
- AP-03 geteilt in AP-03a (Abgleich Server → lokal, Schema v2) und AP-03b (Befehlswarteschlange, IDLE); AP-07 in
  AP-07b (Kalibrierung aus PstProbe + `classifier-settings.json`) und AP-07c (Anbindung an Mails/Ordner, nach AP-03b).
  Grund: AP-07b ist unabhängig vom Sync und läuft parallel; AP-07c braucht die Befehlswarteschlange.
- Planer-Entscheidungen AP-07b aus PstProbe-Daten (E1–E4 in `work\AP-07b\requirement.md`): `compauth=pass` neutralisiert
  DMARC fail zu „nicht verifiziert“ (0 Punkte); Inhalts-Signale ohne Punkte bei authentifizierten Mails.
- AP-03a: Erstabgleich zunächst Standard 90 Tage; Dennis entschied „alles laden“ → Standard 0 (vor dem Merge geändert,
  Test angepasst).
- implement.1 + author.0 für beide APs gestartet.
- **AP-07b Gate erreicht** (`feat/ap-07b-classifier-calibration`): verify.2 395/395 (S2), review.1 APPROVE, fix.2
  (SHOULD 1 + NITs) erledigt, PstProbe: Fehlalarm weg, Junk unverändert; Doku (README, `docs\classifier.md` lokal).
  Gemergt und gepusht.
- **AP-03a Gate erreicht** (verify.2 S3 658/658, review.1 + review.2 APPROVE, secure.1 0/0/2/4, fix.2 F1–F7).
  Gemergt und gepusht.
- **AP-03a fix.3 Gate erreicht** (`fix/ap-03a-missed-uids` @ `a221b2e`): verify.1 S3 761/761 (Core 360, Infrastructure
  401), review-fix3 APPROVE (SHOULDs = Doku, vom Planer erledigt), echte Konten auf Datenkopie: 0 abweichende Ordner.
  Wartet auf CPM. Nach Merge: `_scratch\sync-check` im Hauptordner auf `SkippedUpToUid` umstellen (Fassung im
  Fix-Worktree), dann SyncProbe auf `probe-data` (migriert auf v3, lädt die fehlenden Papierkorb-Mails).

### Vertagt aus AP-03a (review.1/secure.1, Details `work\AP-03a\requirement.md` „Decisions fix.2“)
- [ ] AP-03b: gemeinsamer IMAP-Verbindungs-/Ordnerlisten-Code für `MailKitTransport` und `ConnectionProbe` (review SHOULD 2)
- [ ] AP-03b: nach `ImapProtocolException` neu verbinden und mit dem nächsten Ordner weitermachen (Q4)
- [ ] AP-03b/AP-05: rein lokale Rollen (Postausgang) von R1 ausnehmen (Q2)
- [ ] AP-03b: `IEmlStore.DeleteFolderAsync`, Aufräumen verwaister EML/`*.tmp` beim Start; `secure_delete`/`VACUUM` erwägen (Q3, secure N4)
- [ ] AP-03b: Metadaten aller Kandidaten in einem FETCH; ohne CONDSTORE holt jeder Lauf alle Flags (review NITs)
- [x] Papierkorb-Mails fehlten (Fall 28) / FETCH-null vs. Hochwassermarke / Einzelfehler beim Erstabgleich → durch
  AP-03a fix.3 behoben (`fix/ap-03a-missed-uids`: Kandidaten = jede lokal fehlende Server-UID über der
  Startfenster-Grenze, Schema v3); Nachprüfung echte Konten: alle Ordner deckungsgleich
- [ ] fix.3 review NIT: UID 0 im Startfenster setzt die Grenze still auf 0 → wie F3 als Protocol-Fehler behandeln
  (`SyncEngine.cs:492-494`, mit MailKit praktisch unerreichbar)
- [ ] fix.3 Restrisiko (nur mit Startfenster > 0): liefert SEARCH SINCE beim Erstabgleich unvollständig, liegt die
  Grenze dauerhaft zu hoch; bei Standard 0 ohne Wirkung
- [ ] AP-03b: **Tempo** – Fall 28 (2026-10-31): Outlook.com ~4 Mails/s beim Erstabgleich (zwei Round-Trips je Mail);
  Postfach mit >1.400 Mails braucht viele Minuten → FETCH bündeln (Body + Metadaten in einem Kommando, Blöcke à n UIDs)
- [ ] AP-03b/AP-04: Fortschritt während des Abgleichs melden (`IProgress<…>` am `SyncEngine`); die SyncProbe zeigt bis
  zum Ende nichts an und wirkt deshalb hängend
- [ ] Provider-Profil Gmail: `[Gmail]/Important` kam ohne `\Important`-Attribut (Rolle None, sichtbar) → wird als
  normaler Ordner abgeglichen (Duplikate) → per Profil ausblenden wie All Mail
- [ ] AP-03b: Mail, die jedes Mal `ImapProtocolException` auslöst, blockiert weiter das Konto (MailKit trennt) → mit Reconnect lösen
- [ ] AP-04: Logging-Regel `SyncReport` – nur Typ, Rolle, Ordner-ID; nie `Error`-Text oder Ordnername (secure N1)
- [ ] UI-AP: Steuer-/Bidi-Zeichen (RTLO) in Betreff/Absendername/Ordnernamen beim Anzeigen entschärfen

### Vertagt aus AP-07b
- [ ] Eine Regel für alle Dateilader (AuthConfiguration, ClassifierSettingsFile, FileEmlStore): IO-Fehler mit
  Dateiname einpacken, im Log nur Typ + eigene Meldung; `File.Exists` bei unlesbarer Datei (stiller Default) mitentscheiden
  (AP-07c/AP-04)
- [ ] Härtung: widersprüchliche `compauth`-Ergebnisse im obersten Header → None (review.1 P2)

WinUI-App für Fall 30: im Projektordner `dotnet build MailWarden.sln`, dann
`src\MailWarden.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\MailWarden.App.exe` starten.

## Kosten (Agenten)
- Sitzung 1 (2026-10-31): Builder AP-01 ×2 (+1 abgebrochen), AP-07a ×1, AP-02a ×1 · Tester AP-01 ×3, AP-07a ×1,
  AP-02a ×1 · Reviewer AP-01 ×1
- Sitzung 2 (2026-10-31): Builder AP-07a ×1 (+5 Fortsetzungen), AP-02a ×1 (+2 Fortsetzungen) · Tester AP-01 ×1,
  AP-07a ×6, AP-02a ×4 · Reviewer AP-01 ×1, AP-07a ×4, AP-02a ×3 · Security AP-02a ×2 · Documenter ×3
- Sitzung 3 (2026-10-31): Tester S3 main ×1 · AP-07b: Builder ×1 (+1 Fortsetzung), Tester ×2 (+1), Reviewer ×1,
  Documenter ×1 · AP-03a: Builder ×1 (+2 Fortsetzungen), Tester ×2 (+2), Reviewer ×2, Security ×1, Documenter ×1 ·
  CPM: Tester ×2 (Testanpassung Standard 0, S3 main)