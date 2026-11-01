# AP-04a – Testergebnis (verify.4, nach fix.4)

```
Stufe:        S3
Auswahl nach: voll
Ergebnis:     1678 von 1678 Tests grün (Core 430, Infrastructure 928, Presentation 320)
Fehlschläge:  –
Nicht gelaufen: Fälle 42-48 (manuell, Dennis)
Laufzeit:     14,8 s
Abbruch:      –
Auffällig:    Schwere Eingaben (Fälle 49, 66-68, 78-80) laufen ohne Prozessende innerhalb des Testlimits. Fall 78 meldet WasReduced true.
```

Stand: `feat/ap-04a-ui-read` nach Merge von `test/ap-04a-ui-read`, Build `-warnaserror` grün. Ergebnis: **PASS**.

## Verlauf
Lauf 1: 1677 grün, 1 rot (eigener Testfehler, a): `Fifty_thousand_elements_keep_their_markup`. Meine zusätzlich erfundene Kontrolle (50.000 `<span>x</span>`, ca. 700 KB) liegt über den Laufzeit-/Textbudgets, `WasReduced` war true. Der Vertrag (Fall 81) nennt 20.000 gewöhnliche Tags; dieser Fall ist grün. Die überzählige Kontrolle wurde entfernt. Lauf 2: alles grün.
Behoben/erledigt seit verify.3: Same-Mail-Test (Fall 82/83), Klartext-CSP, `OpaquePartsTests` (rfc822) und `Name_of_exactly_100…` sind jetzt grün.
