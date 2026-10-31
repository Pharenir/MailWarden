# TODO – MailWarden

## Offen
- [ ] CPM für `chore/app-icon` (Icon, README AI-usage, AP-00-Ergebnis in PLAN, Requirement AP-01) – Freigabe Dennis
- [ ] AP-13: `.ico` aus `assets\mailwarden-512.png` erzeugen (Exe-Icon, Installer)
- [ ] Google-403 im Status „Testing“ endete ohne `RESULT:`-Zeile – Fehlerpfad der Google-Anmeldung in AP-02 explizit testen
- [ ] AP-01: Einzeltest-Timeout-Mechanismus und Parallelität festlegen, in `CLAUDE.md` eintragen
- [ ] AP-01: WinUI 3 unpackaged + self-contained verifizieren (Fallback WPF)

## Entschieden (2026-10-31)
- Name/Ablage `K:\Projekte\Windows-Tools\MailWarden`, Lizenz PolyForm Noncommercial 1.0.0
- **Mit Git** (Umentscheidung 2026-10-31, vorher „ohne Git“): öffentliches GitHub-Repo `Pharenir/MailWarden`, weil Google
  Homepage- und Datenschutz-URL verlangt (`README.md`, `PRIVACY.md`). Snapshot-Regel entfällt.
- UI WinUI 3 · Gmail per OAuth · Daten unter `<install>\data\` · Installer (Inno Setup) als AP-13
- Pipeline-light ab AP-01: Builder fable / Tester sonnet / Reviewer opus; Abschluss delegiert bei PASS+APPROVE; max. 4 Runden
- Gruppenansichten: Posteingang, Gesendet, Postausgang (lokale Versandwarteschlange), Screener, Junk, Quarantäne – jeweils über alle Konten und je Konto
- Screener: `LikelyLegit` landet im Screener; Posteingang nur bekannte Absender; „kein Junk“ → Absender ins Adressbuch
- PLAN.md freigegeben 2026-10-31

## Testzähler
Testzähler: 0/4 seit letztem S3 (zuletzt: –, –)

## Stand 2026-10-31
- Zuletzt gemacht: PLAN.md freigegeben. AP-00: Spike-Programm `_scratch\auth-spike\AuthSpike` gebaut (MSAL + Google
  OAuth + MailKit, Tokens DPAPI-verschlüsselt), Fehlerpfade (Usage, fehlende Config) geprüft; `docs\auth-setup.md`
  mit Registrierungsanleitung geschrieben.
- Google-Veröffentlichung scheiterte zunächst an fehlender Homepage-/Datenschutz-URL → Repo wird öffentlich auf
  GitHub, `PRIVACY.md` angelegt. Git initialisiert, `.gitignore` angelegt.
- Repo `Pharenir/MailWarden` öffentlich angelegt und gepusht (2 Commits auf `main`); `PRIVACY.md`, `TERMS.md` online.
- **AP-00 abgeschlossen: Go.** Alle drei Konten: Cache ohne Browser, IMAP + SMTP ok, Kosten 0 €. Erkenntnisse in
  `docs\auth-setup.md`. Icon nach `assets\`, README-AI-usage angepasst (Icon aus Claude Design).
- AP-01 gestartet: Anforderung `work\AP-01\requirement.md`, Pipeline Builder/Tester auf Worktrees unter `.worktrees\`.
- Nächster Schritt: verify.1 (Branches zusammenführen, Tester lässt S2 laufen), review.1 parallel.
- Blockiert durch: CPM-Freigabe für `chore/app-icon`; Dennis startet die WinUI-App manuell (Sollverhalten AP-01 #8).
