# AP-07b Testergebnis (verify.1)

**Ergebnis: PASS** (Stufe S2, Filter `Category=feature:classifier|Category=smoke`)

| Projekt | Gesamt | Bestanden | Fehlgeschlagen |
|---|---|---|---|
| MailWarden.Core.Tests | 360 | 360 | 0 |
| MailWarden.Infrastructure.Tests | 35 | 35 | 0 |

Laufzeit: ca. 2,7 s (Wächter, ohne Build-Cache-Effekte); Testzahl geprüft, Filter nicht leer.

## Lauf 1: 1 Fehlschlag, Einordnung a (eigener Testfehler)
`CompAuthAndVerdictTokenTests.Dmarc_pass_with_compauth_pass_is_authenticated_as_before`
- Erwartet: Authenticated; Tatsächlich: Unknown.
- Ursache: Fixture `AuthHeaders.Outlook` nutzt `example.test`, das Test-Input hat die Absenderdomain `TestInputs.PlausibleDomain` -> DMARC nicht ausgerichtet. Kein Produktfehler.
- Behoben im Test: Header mit `{Domain}` aufgebaut (1 Versuch). Danach alle grün.

## Hinweis Werkzeug
Aufruf des Wächters aus Git Bash scheitert am `|` im Filter (cmd-Interpretation); aus PowerShell mit `'"Category=...|Category=..."'` funktioniert er.

## Nicht getestet
Offene Unklarheiten aus `test-plan.md` (leere Liste, null-Schwelle, doppelte Keys, BOM, null-Pfad). Kein S3-Lauf (nicht gefordert).

## verify.2 (nach fix.2: DisplayText.Truncate)

**PASS** (S2, gleicher Filter). Core.Tests 360/360, Infrastructure.Tests 35/35 (gesamt 395, 0 Fehlschlaege). Laufzeit ca. 4,2 s. Keine Testanpassung noetig.
