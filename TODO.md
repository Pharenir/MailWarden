# TODO – MailWarden

## Offen
- [ ] AP-00: Microsoft-App-Registrierung – ist ein Azure-Konto/Kreditkarte nötig? (Kostenfrage)
- [ ] AP-00: Google-Cloud-Projekt anlegen, OAuth-Client „Desktop“, App auf Production stellen
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
- Nächster Schritt: GitHub-Repo anlegen und pushen (Freigabe Dennis) → URLs in Google Branding eintragen → Publish →
  Spike je Konto (`docs\auth-setup.md`) → „Erfahrungen“ nachtragen → Go/No-Go → AP-01.
- Blockiert durch: Freigabe Repo/Push, Browser-Anmeldungen durch Dennis.
