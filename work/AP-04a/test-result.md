# AP-04a – Testergebnis (verify.2, nach fix.2)

```
Stufe:        S3
Auswahl nach: voll
Ergebnis:     1488 von 1488 Tests grün (Core 430, Infrastructure 818, Presentation 240)
Fehlschläge:  –
Nicht gelaufen: Fälle 42-48 (manuell, Dennis)
Laufzeit:     14,7 s (keine belastbare Historie)
Abbruch:      –
Auffällig:    Fall 49 (1.023 und 5.000 verschachtelte Teile, multipart und message/rfc822, Reader und Sync) lief ohne Prozessende.
              Fall 64 (langsamer Remote-Bild-Rebuild) war in 3 zusätzlichen Einzelläufen stabil (3/3 grün je Lauf); kein Flakiness-Hinweis.
```

Stand: `feat/ap-04a-ui-read` nach Merge von `test/ap-04a-ui-read`, Build `-warnaserror` grün.

## Verlauf verify.2 (alle Fehlschläge Einordnung a, eigener Testfehler)
Lauf 1: 1484 grün, 4 rot.
- `Sanitize_mailto_of_something_else_...`: Test erwartete "kein attach im Ergebnis"; Planer-Festlegung: Nicht-mailto kommt unverändert zurück. Test auf Gleichheit umgestellt.
- `Load_remote_images_rebuilds_...`, `Slow_remote_image_rebuild_arrives_...`: `CanLoadRemoteImages` wird sofort false, der Rebuild läuft danach. Test wartet jetzt per Polling auf das neue Dokument.
- `Status_event_is_marshalled_through_the_dispatcher`: Fake-Controller aktualisierte `CurrentStatus` nicht; der durch K15 ausgelöste Reload baute die Navigation mit altem Status neu. Fake wie der echte Controller angepasst.
Lauf 2: 1 rot (`Slow_remote_image_rebuild_arrives_...`, Timeout): das 2,3-MB-Dokument lief in das Gesamtbudget des Limiters, die Bilder am Ende gingen verloren. Fixture: Bilder vor dem Füllmaterial. Lauf 3: alles grün.
Zusätzlich verschärft (Planer-Antworten): `Shorten` exakt max + "…", Absender im Lesebereich: Adresse sichtbar, Name gekürzt.
