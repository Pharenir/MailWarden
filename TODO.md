# TODO – MailWarden

## Offen
- [ ] Dennis: AP-01 Fall 30 – WinUI-App starten (Pfad unten unter „Übergabe“)
- [ ] Freigabe Dennis: Worktrees/Branches von AP-01, AP-07a, AP-02a und `chore/app-icon` löschen (alles in `main`)
- [ ] Freigabe Dennis: leere, ungetrackte Datei `AuthenticationResultsParser.cs` (0 Byte) im Projektordner löschen
- [ ] AP-13: `.ico` aus `assets\mailwarden-512.png` erzeugen (Exe-Icon, Installer)
- [ ] AP-13: WindowsAppSDK-Metapaket zieht ~38 MB KI-Komponenten (onnxruntime/DirectML) → nur benötigte Teilpakete? (Paketentscheidung Dennis)
- [ ] AP-04: ungültiges `MAILWARDEN_DATA_DIR` beendet die App beim Start stumm → Logger + Meldungsfenster
- [ ] AP-03/AP-04: EML überschreiben scheitert bei offenem Leser → Lesebereich lädt EML in den Speicher, Befehlswarteschlange wiederholt
- [ ] Dennis: AP-02a Fälle 28–30 mit der AuthProbe (Anleitung `.worktrees\feat-ap-02a\work\AP-02a\impl-notes.md`,
  „AuthProbe usage for Dennis“); dabei prüfen: `%APPDATA%\Google.Apis.Auth` entsteht **nicht**; Abbruch auf der
  Google-Seite meldet UserCancelled/AccessDenied, nicht `state_mismatch`
- [ ] Dennis: AP-07a Begründungstexte der Probe ansehen (`.worktrees\feat-ap-07a\_scratch\ClassifierProbe`)
- [ ] Probe-Datenbanken aus `_scratch\` mit altem Schema-v1-Layout (vor D3) vor weiterer Nutzung löschen (Freigabe Dennis)

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
2. Worktrees/Branches der drei APs aufräumen (Löschen nur nach Freigabe). `MailWarden.sln` ohne BOM halten
   (`dotnet sln` fügt es wieder ein).
3. Nächste APs laut PLAN: AP-03 (Sync-Engine) und AP-07b (Klassifizierer an Mails/Ordner, Punkte oben), AP-04 (UI
   inkl. AP-02b Konten-UI). Vor Start: Anforderung je AP schreiben (Planer), Modellwahl wie bisher.

## Worktrees (alle unter `.worktrees\`, git-ignoriert)
| Worktree | Branch |
|---|---|
| `feat-ap-01` | `feat/ap-01-skeleton-storage` |
| `test-ap-01` | `test/ap-01-skeleton-storage` |
| `feat-ap-07a` | `feat/ap-07a-classifier-core` |
| `test-ap-07a` | `test/ap-07a-classifier-core` |
| `feat-ap-02a` | `feat/ap-02a-accounts-auth` |
| `test-ap-02a` | `test/ap-02a-accounts-auth` |

WinUI-App für Fall 30: in `.worktrees\feat-ap-01` `dotnet build MailWarden.sln`, dann
`src\MailWarden.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\MailWarden.App.exe` starten.

## Kosten (Agenten)
- Sitzung 1 (2026-10-31): Builder AP-01 ×2 (+1 abgebrochen), AP-07a ×1, AP-02a ×1 · Tester AP-01 ×3, AP-07a ×1,
  AP-02a ×1 · Reviewer AP-01 ×1
- Sitzung 2 (2026-10-31): Builder AP-07a ×1 (+5 Fortsetzungen), AP-02a ×1 (+2 Fortsetzungen) · Tester AP-01 ×1,
  AP-07a ×6, AP-02a ×4 · Reviewer AP-01 ×1, AP-07a ×4, AP-02a ×3 · Security AP-02a ×2 · Documenter ×3