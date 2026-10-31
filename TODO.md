# TODO – MailWarden

## Offen
- [ ] CPM für `chore/app-icon` (Icon, README AI-usage, AP-00-Ergebnis in PLAN, Requirements AP-01/AP-07a/AP-02a) – Freigabe Dennis
- [ ] Dennis: AP-01 Fall 30 – WinUI-App starten (Pfad unten unter „Übergabe“)
- [ ] AP-13: `.ico` aus `assets\mailwarden-512.png` erzeugen (Exe-Icon, Installer)
- [ ] AP-13: WindowsAppSDK-Metapaket zieht ~38 MB KI-Komponenten (onnxruntime/DirectML) → nur benötigte Teilpakete? (Paketentscheidung Dennis)
- [ ] AP-04: ungültiges `MAILWARDEN_DATA_DIR` beendet die App beim Start stumm → Logger + Meldungsfenster
- [ ] AP-03/AP-04: EML überschreiben scheitert bei offenem Leser → Lesebereich lädt EML in den Speicher, Befehlswarteschlange wiederholt
- [ ] AP-07a: mittlere Stufe `random-domain` (+15, Score 0.5–0.8) ungetestet → Grenzbeispiel aus Builder-Notizen nachtesten
- [ ] AP-02a verify: `ListKeysAsync("")` = alle Schlüssel; Ordnernamen-Vergleich `Outbox`/`OUTBOX` case-insensitive (Planer-Entscheidung, in Requirement nachtragen)
- [ ] AP-07a verify: Lesarten des Testers bestätigt (Kontakt + Auth unbekannt → immer LikelyLegit; Freemail-Reply-To bei Freemail-Absender +10) – in Requirement nachtragen

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
Testzähler: 0/4 seit letztem S3 (zuletzt: AP-01 verify.1, 2026-10-31 – 72/72 grün)

## Stand 2026-10-31 (Sessionende)
- **AP-00 abgeschlossen: Go.** Alle drei Konten (2× hotmail.de, 1× googlemail.com): OAuth, Cache, IMAP, SMTP ok,
  Kosten 0 €. Erkenntnisse in `docs\auth-setup.md` (lokal).
- **AP-01** (Skelett, Storage, WinUI-Leer-App): verify.1 72/72 grün; review.1 BLOCK → Planer-Entscheidungen D1–D12 in
  `work\AP-01\requirement.md`. fix.2: Tester-Tests fertig (Branch `test/ap-01-skeleton-storage`, `0e07d24`),
  Builder fix.2 **done** (`a43a00b`, Worktree sauber): D2–D11 umgesetzt, Probe 81/81, Selbstcheck der alten Suite
  72/72 (kein Nachweis). Bereit für verify.2 + review.2. Planer-Punkte:
  - Vertragstext in `requirement.md` nachziehen: Mail-Schlüssel jetzt `(FolderId, Uid)`, Indizes nach D4
  - D5 strenger als entschieden (`COM¹–³`, `CON .eml`) → annehmen (Windows behandelt sie als Geräte)
  - Tester: Fall 11a zusätzlich mit Update einer bestehenden Mail auf falsches Konto; Cascade lässt keine FTS-Zeilen zurück
- **AP-07a** (Klassifizierer-Kern): Tests fertig (`test/ap-07a-classifier-core`, `69252a9`). Builder **done**
  (`0adc48d`): Vertrag vollständig, Build grün, Probe ok (Zufallsdomains ≥ 0.888, echte ≤ 0.293). Bereit für verify.1
  (S3, weil zentrale Bausteine). Offene Fragen in `work\AP-07a\impl-notes.md`, Planer-Tendenz:
  - Outlook `dmarc=bestguesspass` → vorerst `Unknown`, in AP-07b an echten Headern prüfen
  - `hardfail` → `Fail` beibehalten (RFC-Synonym), Vertrag ergänzen
  - Anzeigename `J.Doe`/`Mr.Smith` gibt fälschlich +20 → Regel schärfen (TLD-Liste statt „2–24 Buchstaben“?)
  - doppelte Signal-Ids case-insensitive, Freemail auch über Organisationsdomain, Reply-To ohne `@` → kein Befund: ok
  - Dennis: Begründungstexte in der Probe-Ausgabe ansehen
- **AP-02a** (Konten/Auth): Tests fertig (`test/ap-02a-accounts-auth`, `0e6260d`). Builder **partial** (`04d2aaa`,
  Worktree sauber): gesamter Vertrag implementiert und baut, aber ungeprüft; offen: nicht-interaktive Probe (Fälle
  1–27), AuthProbe-Konsole `_scratch\auth-probe\` (Fälle 28–30, csproj braucht `GenerateDocumentationFile=false`),
  Nutzungsblock für Dennis. Abweichungen/Fragen in `work\AP-02a\impl-notes.md`, Planer-Entscheidung nötig zu:
  - `ListKeysAsync("")` wirft beim Builder, der Tester erwartet „alle Schlüssel“ (meine Tendenz: alle Schlüssel)
  - kaputte `auth-config.json` meldet `Provider = GenericImap` – ok?
  - Re-Auth mit falschem Konto: Sign-out nur, wenn die Adresse kein gespeichertes Konto ist (sinnvoll, Vertrag anpassen)
  - Google-Login ohne eigenes Timeout (403-Seite wartet ewig) → Timeout ergänzen
  - Credential-Keys: Kollision `a@b.c`/`a_b.c`, Länge > 128, NTFS case-insensitive → Key = `google.` + Hash der Adresse?

## Übergabe – so geht es weiter
1. Je Builder-Branch `impl-notes.md` lesen: STEP-RESULT `done` oder `partial`? Bei `partial` bzw. uncommitteten
   Änderungen im Worktree (`git -C .worktrees\<wt> status`) zuerst den Builder-Schritt fortsetzen.
2. **AP-01 verify.2:** in `.worktrees\feat-ap-01` den Branch `test/ap-01-skeleton-storage` mergen
   (`git merge --no-ff`), bauen, Tester S2/S3 über den Wächter; parallel review.2 (Reviewer opus, Fokus D1–D11).
   PASS + APPROVE → PLAN.md Spalten *Umgesetzt/Getestet* AP-01 füllen, CPM bei Dennis anfragen.
3. **AP-07a / AP-02a verify.1:** jeweils Test-Branch in den Feature-Branch mergen, Testprojekt per
   `dotnet sln add` aufnehmen (AP-07a: `tests\MailWarden.Core.Tests`), Tester S2, Reviewer; AP-02a zusätzlich
   **security-reviewer**. Danach Dennis: AP-02a Fälle 28–30 mit `_scratch\auth-probe\` (Anleitung in impl-notes).
4. Reihenfolge der Merges nach `main`: AP-01 zuerst; AP-07a/AP-02a zweigen von AP-01-Stand vor fix.2 ab → vor ihrem
   Merge `feat/ap-01-skeleton-storage` hineinmergen und erneut testen.

## Worktrees (alle unter `.worktrees\`, git-ignoriert)
| Worktree | Branch |
|---|---|
| `feat-ap-01` | `feat/ap-01-skeleton-storage` |
| `test-ap-01` | `test/ap-01-skeleton-storage` |
| `feat-ap-07a` | `feat/ap-07a-classifier-core` |
| `test-ap-07a` | `test/ap-07a-classifier-core` |
| `feat-ap-02a` | `feat/ap-02a-accounts-auth` |
| `test-ap-02a` | `test/ap-02a-accounts-auth` |

WinUI-App für Fall 30: `.worktrees\feat-ap-01\src\MailWarden.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\MailWarden.App.exe`
(nach fix.2 neu bauen: `dotnet build MailWarden.sln` im Worktree).

## Kosten Sitzung 2026-10-31 (Agenten)
Builder: AP-01 ×2 (+1 abgebrochen), AP-07a ×1, AP-02a ×1 · Tester: AP-01 ×3, AP-07a ×1, AP-02a ×1 · Reviewer: AP-01 ×1
