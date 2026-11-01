# TODO – MailWarden

## Offen
- [ ] **Dennis: manuelle Tests nach `work\manual-tests.md`** (AP-02a Fälle 28–30, AP-03a Fall 28,
  Begründungstexte AP-07a/b, AP-04a Fälle 42–48 = Teil 6) – ersetzt die Einzelpunkte unten; „fertig“ erst danach
- [ ] AP-13: `.ico` aus `assets\mailwarden-512.png` erzeugen (Exe-Icon, Installer)
- [ ] AP-13: WindowsAppSDK-Metapaket zieht ~38 MB KI-Komponenten (onnxruntime/DirectML) → nur benötigte Teilpakete? (Paketentscheidung Dennis)
- [x] AP-04: ungültiges `MAILWARDEN_DATA_DIR` beendet die App beim Start stumm → Logger + Meldungsfenster (AP-04a: Fehlerfenster + Log; am Fenster Fall 47 offen)
- [x] AP-03/AP-04: EML überschreiben scheitert bei offenem Leser → Lesebereich lädt EML in den Speicher (AP-04a, `IMailboxReadModel.ReadContentAsync`); Wiederholen in der Befehlswarteschlange unverändert

### Vertagt aus AP-02a (A18, secure.1/2, review.2)
- [ ] Gemeinsamer Atomic-Write-Helfer für `FileEmlStore`/`DpapiCredentialStore` (berührt AP-01-Code)
- [ ] `DataPaths.CredentialsDirectory` / `AuthConfigPath` (AP-02b/AP-04)
- [x] AP-04 Composition Root (AP-04a, `MailWardenRuntime`): genau **eine** `DpapiCredentialStore`-Instanz je Ordner, beide Token-Provider teilen sie;
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
Testzähler: 0/4 seit letztem S3 (zuletzt 2026-10-31: S3 auf `main` @ `3f74af0` nach Merge von AP-03d,
964/964 – Core.Tests 360, Infrastructure.Tests 604; Bericht `work\S3-main-2026-10-31-f.md`)

## Session 4 (2026-11-01)
- Entscheidungen Dennis: AP-04a und AP-07c **parallel**; HTML-Bereinigung mit Paket HtmlSanitizer; Liste und Lesebereich
  umschaltbar nebeneinander/untereinander, Navigation immer links.
- Planer-Entscheidung AP-07c (von Dennis 2026-11-01 vorerst bestätigt): Standard nur protokollieren, Altbestand nie automatisch,
  Handeln ruht bei leerem Adressbuch (`work\AP-07c\requirement.md`). Folge: AP-06 (mindestens Sammeln aus Gesendet) ist
  Voraussetzung, damit der Filter wirklich verschiebt.
- Reihenfolge danach: AP-04a und AP-07c mergen, D8 von AP-07c (Verdrahtung in `MailWardenRuntime`), dann AP-04b / AP-06.

### Vertagt aus AP-04a
Stand: S3 1700/1700, review.5 APPROVE, Sicherheitsbefunde aus secure.4 in fix.5 behoben; eine weitere Sicherheitsprüfung
gibt es nach Entscheidung von Dennis nicht. Alles unterhalb „Hoch“ steht hier.
- [ ] **Fälle 42–48 offen** (manuell Dennis, `work\manual-tests.md` Teil 6): die Oberfläche wurde noch nie ausgeführt
- [ ] N3-Schätzung zählt die Kodierung nicht mit: eine 1-MB-Mail kann 336 Mio. Zeichen ergeben (review S12, Medium).
  Vorschlag: kodierte Zeichen mit ihrer Ausgabelänge zählen
- [ ] AP-07c: Klassifizierer-Signal für Inhalte unterhalb der MIME-Tiefe 32 (`HasOpaqueParts`, secure N1)
- [ ] Restrisiko nach „Bilder laden“: ein öffentlicher Name, der auf eine private Adresse auflöst, und Weiterleitungen
  (ungeprüft)
- [ ] Inline-Bilder ab ca. 24,3 MB verlieren das letzte Bild
- [ ] Parser-Budget-Abbruch trifft Word/Outlook-Mails ab ca. 12.000 gestylten Tags (976 KB): alle Links und Bilder der Mail
  fehlen, der Hinweis erscheint
- [ ] Ein hängender Aufbau des Lesedokuments kostet jede weitere Mail bis zu 5 s (nur ein Aufbau zugleich)
- [ ] Mögliche spätere Härtung: HTML-Aufbereitung in einem eigenen Hilfsprozess (Dennis hat die Extrarunde fix.5
  vorgezogen)
- [ ] Zu erledigen in AP-04b: Konten-UI, Aktionen, Suche, Anhänge öffnen/speichern

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

## Übergabe – so geht es weiter (Sessionende 2026-10-31, Sitzung 3)
1. **Stand `main`:** AP-01, AP-02a, AP-03a (inkl. fix.3), AP-03b, AP-03c, AP-03d, AP-07a, AP-07b gemergt und gepusht.
   Die Abgleich-Schicht ist vollständig (lesen, Befehle schreiben, Hintergrunddienst); es gibt noch keine Oberfläche.
   Letzter S3 auf `main` siehe Testzähler. Dieser Handover liegt auf `chore/session-handover-5` und ist mit dem
   Sessionende-CPM nach `main` gemergt.
2. **Nächstes AP (Empfehlung Planer, Dennis hat noch nicht entschieden):** AP-04 Oberfläche (Gruppenansichten,
   Lesebereich mit bereinigtem HTML/WebView2, Konten-UI = AP-02b, Verdrahtung Dienst + Befehle) – vorher die Punkte
   „Vertagt aus AP-03c/AP-03d (für AP-04)“ in die Anforderung übernehmen; Security-Prüfung einplanen. Alternative:
   AP-07c (Klassifizierer an Mails/Ordner, Screener/Quarantäne anlegen = CREATE, Verschieben über die Warteschlange).
   AP-04 ist groß → beim Planen in AP-04a/b teilen (z. B. a = Gerüst + Liste + Lesebereich, b = Konten-UI + Aktionen).
3. **Werkzeuge in `_scratch\`** (git-ignoriert; Client-Werte vorher aus `_scratch\auth-spike\spike.env` ins Terminal
   laden, siehe `work\manual-tests.md`): `auth-probe`, `sync-probe` (mit Fortschritt), `sync-check` (Server-STATUS
   gegen lokalen Bestand, optional Diagnose eines Ordners), `command-probe` (nur geprüfte Junk-Mails, Freigabeliste
   `command-probe-verified.json`), `shiftdel-probe` (eine Testmail per Betreff), `service-probe` (Hintergrunddienst),
   `ClassifierProbe`, `PstProbe`. Offen: Probes `sync-probe`/`command-probe`/`shiftdel-probe` auf `DataFolderLock`
   umstellen (die `service-probe` nutzt sie bereits).
4. **Echte Daten:** `K:\Projekte\_private\MailWarden\probe-data` (Schema v4, mit dem Server deckungsgleich; die
   Junk-Mails von Konto 2 sind im Fall-24-Test am Server gelöscht worden). Tests
   gegen echte Konten immer auf einer Kopie; nur anonymisierte Zahlen ausgeben.
5. `MailWarden.sln` ohne BOM halten (`dotnet sln` fügt es wieder ein). Python-Patchskripte: Backslashes in
   Ersetzungstexten als Raw-String schreiben (ein `\a` wurde einmal zum Steuerzeichen).

## Worktrees (alle unter `.worktrees\`, git-ignoriert)
Keine offen (alle nach Merge entfernt, Freigabe Dennis 2026-10-31).
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
  Gemergt (`2e1420a`), S3 auf `main` 761/761, gepusht; echte `probe-data` nachgeprüft: 0 abweichende Ordner.
- **AP-03b Gate erreicht** (`feat/ap-03b-sync-robust-fast` @ `7938a29`, Worktrees `feat-ap-03b`/`test-ap-03b`):
  verify.2 S3 803/803 (Core 360, Infrastructure 443), review.1 + review.2 APPROVE, keine Security-Prüfung (nur lesend,
  keine neuen Datenablagen – Planer-Entscheidung), Fall 18 echte Konten ≈ 14 Mails/s (vorher ≈ 4), 0 abweichende Ordner;
  B6 mit AuthProbe gegen echte Server geprüft. Wartet auf CPM: `chore/session-handover-4` → `feat/ap-03b-…` (`--no-ff`),
  danach SyncProbe mit Fortschritt aus `.worktrees\feat-ap-03b\_scratch\sync-probe` nach `_scratch\sync-probe` kopieren.
  Hinweis: Builder hat einmal selbst Tests gestartet (Selbstkontrolle, gegen die Regel „nur Tester“).
- AP-03b gemergt (`3d646e8`), S3 auf `main` 803/803 (`work\S3-main-2026-10-31-d.md`), gepusht; Worktrees/Branches
  entfernt; SyncProbe mit Fortschritt in `_scratch\sync-probe`.
- **AP-03c gestartet** (`work\AP-03c\requirement.md`): Befehlswarteschlange. Entscheidungen Dennis 2026-10-31: Löschen
  wie Outlook (Entf → Papierkorb, Umschalt+Entf → endgültig, „Ordner leeren“ für Papierkorb/Junk); Echttest nur mit
  den aktuellen Junk-Mails (geprüft, dürfen auch endgültig gelöscht werden); AP-03d = Hintergrunddienst (Zeitplan, IDLE).
- **AP-03c Gate erreicht** (`feat/ap-03c-command-queue` @ `96e2349`): verify.2 S3 922/922 (Core 360, Infrastructure 562),
  review.1 + review.2 APPROVE, secure.1 0/0/1/7 (M1, N1, N4, N5 in fix.2 behoben), Fall 24 echte Konten (nur geprüfte
  Junk-Mails, Kopie von `probe-data`): gelesen/ungelesen, Papierkorb + endgültig, endgültig aus Junk, Junk leeren
  (Konto 2) – Server und lokal deckungsgleich; Sperre lehnt Posteingangs-Mail ab. Wartet auf CPM:
  `chore/ap-03c-requirement` → `feat/ap-03c-…`; danach `probe-data` abgleichen (Junk von Konto 2 ist am Server
  leer), CommandProbe nach `_scratch\command-probe` retten.
  Am echten Server ungeprüft: Umschalt+Entf außerhalb Papierkorb/Junk (MOVE + COPYUID-Expunge), Rücknahme von
  `\Deleted` nach gescheitertem Expunge (F3).

- AP-03c gemergt (`09b33b7`), S3 auf `main` 922/922, gepusht; Umschalt+Entf außerhalb Junk vorher am echten Server
  geprüft (Testmail von Dennis im Posteingang von Konto 1: Posteingang −1, Papierkorb unverändert, Mail weg);
  `probe-data` auf Schema v4 abgeglichen, deckungsgleich; Probes `command-probe`, `shiftdel-probe` in `_scratch\`.
- **AP-03d gestartet** (`work\AP-03d\requirement.md`): Hintergrunddienst (Zeitplan 15 min, IDLE Posteingang,
  Sperrdatei je Datenordner, Status je Konto).

- **AP-03d Gate erreicht** (`feat/ap-03d-mailbox-service` @ `a28d81d`): verify.2 S3 964/964 (Core 360, Infrastructure
  604), review.1 + review.2 APPROVE, keine Security-Prüfung (Reviewer: kein Auslöser), Fall 19 echte Konten 15 min:
  Testmail von Dennis → IDLE-Meldung, Abgleich nach 2 s; IDLE nach 9 min erneuert; zweite Instanz „in Benutzung“;
  Beenden in 0,1 s. Wartet auf CPM: `chore/ap-03d-requirement` → `feat/ap-03d-…`; danach ServiceProbe nach
  `_scratch\service-probe` retten, Probes (`sync-probe`, `command-probe`, `shiftdel-probe`) auf `DataFolderLock` umstellen.

### Vertagt aus AP-03d (für AP-04)
- [x] (AP-04a, `RuntimeConnections`/`AccountPipelines`, Fall 27) SQLite: je Ablauf (Konto-Zyklus, IDLE-Watcher, UI) eine eigene `SqliteDatabase`-Verbindung; Komposition der Probe
  in eine Produktionsklasse überführen; Test „zwei Konten schreiben parallel“ (SQLITE_BUSY) (review O1)
- [x] Sperre (`DataFolderLock`) vor `InitializeAsync` nehmen (O2) (AP-04a, Fälle 24–26)
- [ ] Ausnahmen aus `StatusChanged`-Handlern als kritisch protokollieren (G3) – AP-04a: Handler der Oberfläche erledigt (Fall 38), im Dienst selbst offen
- [x] (AP-04a, Fälle 38/39; offen bleibt nur der Backoff-Teil) „Aktualisieren“ im Fehlerzustand ruft `RetryAccount`; UI filtert `CurrentStatus` gegen die Kontoliste;
  `RetryAccount` verkürzt das Watcher-Backoff nicht (IDLE bis 30 min aus, Polling fängt ab) – ggf. ändern (review.2 NIT 1)
- [x] Security-Prüfung für AP-04 (gemeinsame Token-Caches, Logging, Enqueue aus der UI) – für AP-04a erfolgt (secure.1–4); Enqueue aus der UI folgt mit AP-04b

### Vertagt aus AP-03c
- [ ] AP-03d: benannter Mutex je Datenordner um `ExecutePendingAsync` (secure N3; App/Dienst/Probe parallel)
- [ ] AP-04: Bestätigung „Ordner leeren“ zeigt die Anzahl aus dem Snapshot (secure N2); Hinweis, wenn Umschalt+Entf
  Mails nur in den Papierkorb gebracht hat (kein COPYUID, Q1); fehlgeschlagene Befehle anzeigen/verwerfen
- [ ] AP-04: F1 lädt beim Einreihen die UID-Map des ganzen Ordners (Performance bei großen Ordnern, review.2 NIT)
- [ ] `command-probe-verified.json` (in `_private`) nach Abschluss der Junk-Tests löschen (Rückfrage Dennis)
- AP-03d gemergt (`3f74af0`), S3 auf `main` 964/964, gepusht; Worktrees/Branches entfernt; ServiceProbe in
  `_scratch\service-probe`.
- Kosten Sitzung 3, Fortsetzung: fix.3 Builder ×1, Tester ×2 (+1), Reviewer ×1 · S3 main ×1 · AP-03b Builder ×1 (+1),
  Tester ×1 (+3), Reviewer ×2, Documenter ×1 · S3 main ×3 (AP-03b, AP-03c, AP-03d) · AP-03c Builder ×1 (+2), Tester ×1
  (+3), Reviewer ×2, Security ×1, Documenter ×1 · AP-03d Builder ×1 (+1), Tester ×1 (+3), Reviewer ×2, Documenter ×1

### Vertagt aus AP-03a (review.1/secure.1, Details `work\AP-03a\requirement.md` „Decisions fix.2“)
**In AP-03b aufgenommen (2026-10-31, `work\AP-03b\requirement.md`):** gemeinsamer IMAP-Code (B6), Reconnect nach
Abbruch/`ImapProtocolException` (B4), FETCH bündeln/Tempo (B1/B2), Fortschritt (B3), Gmail Important (B5), UID 0 im
Startfenster (B7). Rest: lokale Rollen → AP-03c/AP-05; `DeleteFolderAsync`/Aufräumen → AP-04; Flags ohne CONDSTORE → AP-03c.
AP-03 neu geteilt: AP-03b = Abgleich robust/schnell, **AP-03c** = Befehlswarteschlange, IDLE, Zeitplan (AP-07c danach).
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
- [x] UI-AP: Steuer-/Bidi-Zeichen (RTLO) in Betreff/Absendername/Ordnernamen beim Anzeigen entschärfen (AP-04a, `SafeDisplayText`, Fall 1)

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