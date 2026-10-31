STEP-RESULT: done

# AP-07b – Documentation notes (documenter)

## README.md (committed)
- Abschnitt "Status" (Satz zum Klassifizierer-Kern): Hinweis auf Kalibrierung an echten Headern und Loader für die
  Einstellungsdatei; weiterhin nicht an Mails angebunden.
- Abschnitt "Data folder", Tabelle Layout: neue Zeile `classifier-settings.json` (optional; Loader vorhanden, App liest
  die Datei noch nicht).
- `## AI usage` unverändert.

## docs\classifier.md (Hauptcheckout, git-ignoriert, nicht committet)
- Kopf: Stand AP-07b; Verweise AP-07b → AP-07c; Hinweis auf `ClassifierSettingsFile` in Infrastructure.
- Entscheidungsreihenfolge, Schritt 2: Allow-Liste im compauth-Fall → `TrustedContact` (Entscheidung P1).
- AuthStatus, Regel 1: Ausnahme compauth (DMARC fail + `compauth=pass` → `Unknown`; erstes compauth zählt, `reason`
  ignoriert, nur am Angabenanfang, `compauth=fail` ändert nichts). Kontakt in diesem Fall → `LikelyLegit` ergibt sich aus
  Schritt 4.
- Neuer Unterabschnitt "Ergebniswörter (Verdict-Tokens)": `bestguesspass` → `None`, `timeout` → `TempError`.
- Signale: neue Zeile `authentication` (0 Punkte, compauth); `random-domain`, `domain-not-in-body`,
  `link-domain-mismatch` mit Hinweis "nicht bei `Authenticated`" (bei random-domain nur die +15-Stufe).
- Neuer Unterabschnitt "Signale und Authentifizierung (AP-07b)" inkl. Datengrundlage (anonymisiert, 345 Mails, keine Einzeldaten).
- "Schwellen und Standardwerte": neuer Unterabschnitt "Einstellungsdatei `classifier-settings.json`" (Ort, Schlüssel,
  Regeln, Beispiel, Fehlerverhalten inkl. nicht umgewandelter IO-Fehler).
- "Bekannte Grenzen": bestguesspass aktualisiert, compauth-Punkt neu, C4 ("in AP-07b" entfernt, offen), C16 (authserv-id
  bewusst nicht geprüft).
- "Noch nicht umgesetzt": AP-07b → AP-07c; Einstellungsdatei wird noch nicht beim Start gelesen.

## Rückfragen
Keine.
