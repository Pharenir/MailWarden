# AP-07d test result (verify.1)

Stufe:        S3
Auswahl nach: voll
Ergebnis:     2133 von 2133 Tests gruen (Core 511, Infrastructure 1267, Presentation 355; Suite gesamt 2133)
Fehlschlaege: keine im Endlauf
Nicht gelaufen: nichts
Laufzeit:     14,7 s (Waechter; Historie vorhanden, weiche Grenze nicht ueberschritten)
Abbruch:      -
Auffaellig:   siehe unten

## Erster Lauf (4 rote Tests, alle behoben)
- Build zuerst rot: zwei Fehler in eigenen Tests (CS1061 `Mode` heisst `EffectiveMode`, CS8604) - behoben.
- `ClassificationPolicyMaxMovesFileTests.Limit_outside_the_range_is_rejected("0")`: Testfehler (Typ a, eigener Test). Die Meldung enthaelt "1 and 1000", also die Zeichenkette "0"; Wertpruefung fuer 0 entfernt.
- `ClassificationPassRoutingTests.Moves_are_split_into_commands_of_at_most_1000_uids...` (1000, 1001, 2500): Bestandstest, durch die neue Bremse (Default 100, Maximum 1000) ueberholt (Typ b, gewollte Verhaltensaenderung). Test angepasst: 1000 Mails mit Limit 1000 -> 1 Befehl; 1001 wird als Ganzes zurueckgehalten (neuer Test `More_than_the_maximum_limit_is_held_as_a_whole`).

## Befund fuer den Planer
Das Maximum der Bremse (1000) entspricht der Befehlsgroesse (1000 UIDs); die Aufteilung von Moves in mehrere Befehle ist ueber den Pass nicht mehr erreichbar (nur noch bei Retention-Deletes). Kein Fehler, aber bitte bewusst bestaetigen.

## Hinweise
- Case 13 ueber `OpenWithCycleStepsAsync` getestet (Event aus der Service-Schleife, Folgezyklus bei Moves, kein Folgezyklus ohne Moves, kein Event waehrend Dispose).
- Cases 17 und 18 sind manuell (nicht automatisiert).

## verify.2 (nach fix.2, Stand f91df74)
Stufe:        S3
Auswahl nach: voll
Ergebnis:     2137 von 2137 Tests gruen (Core 511, Infrastructure 1270, Presentation 356)
Fehlschlaege: keine
Nicht gelaufen: nichts
Laufzeit:     15,8 s (weiche Grenze nicht ueberschritten)
Abbruch:      -
Neu in fix.2: Faelle 19 und 20 (Abbruch-Exception mit/ohne abgebrochenem Token), Fall 21 und geaenderter Fall 14 (Summen seit App-Start, saubere Durchlaeufe loeschen nichts).
