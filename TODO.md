# TODO – MailWarden

## Offen
- [ ] Dennis: AP-01 Fall 30 – WinUI-App starten (Pfad unten unter „Übergabe“)
- [ ] AP-13: `.ico` aus `assets\mailwarden-512.png` erzeugen (Exe-Icon, Installer)
- [ ] AP-13: WindowsAppSDK-Metapaket zieht ~38 MB KI-Komponenten (onnxruntime/DirectML) → nur benötigte Teilpakete? (Paketentscheidung Dennis)
- [ ] AP-04: ungültiges `MAILWARDEN_DATA_DIR` beendet die App beim Start stumm → Logger + Meldungsfenster
- [ ] AP-03/AP-04: EML überschreiben scheitert bei offenem Leser → Lesebereich lädt EML in den Speicher, Befehlswarteschlange wiederholt
- [ ] Dennis: AP-02a Fälle 28–30 mit der AuthProbe (Anleitung `work\AP-02a\impl-notes.md`, „AuthProbe usage for
  Dennis“ – Worktree ist entfernt: statt `cd .worktrees\feat-ap-02a` aus dem Projektordner starten, die Probe liegt
  jetzt unter `_scratch\auth-probe`); dabei prüfen: `%APPDATA%\Google.Apis.Auth` entsteht **nicht**; Abbruch auf der
  Google-Seite meldet UserCancelled/AccessDenied, nicht `state_mismatch`
- [ ] Dennis: AP-07a Begründungstexte ansehen (`docs\classifier.md`; Probe `_scratch\ClassifierProbe`)

### Vertagt aus AP-02a (A18, secure.1/2, review.2)
- [ ] Gemeinsamer Atomic-Write-Helfer für `FileEmlStore`/`DpapiCredentialStore` (berührt AP-01-Code)
- [ ] `DataPaths.CredentialsDirectory` / `AuthConfigPath` (AP-02b/AP-04)
- [ ] AP-04 Composition Root: genau **eine** `DpapiCredentialStore`-Instanz je Ordner, beide Token-Provider teilen sie;
  ein `MicrosoftTokenProvider` je Store
- [ ] Logging-Regel (AP-02b/AP-04): bei `AuthenticationFailedException` nur Provider, Reason, ProviderErrorCode, Message,
  Typ der inneren Ausnahme – nie `ToString()` (innere Meldungen enthalten Servertexte bzw. Dateipfade mit Benutzername)
- [ ] AP-13: ACL des Credentials-Ordners bzw. Ort des Datenordners (`<install>\data` erbt Rechte); Obergrenze für
  Credential-Dateien (z. B. 1 MB, heute `OverflowException` > 2 GB)
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

## Testzähler
Testzähler: 0/4 seit letztem S3 (zuletzt 2026-10-31: S3 auf `main` @ `e45b892` nach allen vier Merges, 542/542 –
Core.Tests 299, Infrastructure.Tests 243; Bericht `work\S3-main-2026-10-31.md`)

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
1. Dieser Handover liegt auf `chore/session-handover` (lokal, nicht gepusht) → beim nächsten CPM mit nach `main`.
2. Aufgeräumt 2026-10-31 (Freigabe Dennis): alle sechs AP-Worktrees und sieben Branches (inkl. `chore/app-icon`)
   entfernt, leere Datei `AuthenticationResultsParser.cs` gelöscht; Probe-DBs gab es keine mehr. AuthProbe und
   ClassifierProbe vorher nach `_scratch\auth-probe` bzw. `_scratch\ClassifierProbe` im Hauptordner gerettet.
   `MailWarden.sln` ohne BOM halten (`dotnet sln` fügt es wieder ein).
3. Nächste APs laut PLAN: AP-03 (Sync-Engine) und AP-07b (Klassifizierer an Mails/Ordner, Punkte oben), AP-04 (UI
   inkl. AP-02b Konten-UI). Vor Start: Anforderung je AP schreiben (Planer), Modellwahl wie bisher.

## Worktrees (alle unter `.worktrees\`, git-ignoriert)
Keine aktiven.

WinUI-App für Fall 30: im Projektordner `dotnet build MailWarden.sln`, dann
`src\MailWarden.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\MailWarden.App.exe` starten.

## Kosten (Agenten)
- Sitzung 1 (2026-10-31): Builder AP-01 ×2 (+1 abgebrochen), AP-07a ×1, AP-02a ×1 · Tester AP-01 ×3, AP-07a ×1,
  AP-02a ×1 · Reviewer AP-01 ×1
- Sitzung 2 (2026-10-31): Builder AP-07a ×1 (+5 Fortsetzungen), AP-02a ×1 (+2 Fortsetzungen) · Tester AP-01 ×1,
  AP-07a ×6, AP-02a ×4 · Reviewer AP-01 ×1, AP-07a ×4, AP-02a ×3 · Security AP-02a ×2 · Documenter ×3