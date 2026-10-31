# AP-03a – Testergebnis verify.1

**Ergebnis: PASS** (Stufe S3, volle Suite, Branch `feat/ap-03a-sync-down`)

| Projekt | Tests | Bestanden | Fehlgeschlagen |
|---|---|---|---|
| MailWarden.Core.Tests | 299 | 299 | 0 |
| MailWarden.Infrastructure.Tests | 347 | 347 | 0 |
| Gesamt | 646 | 646 | 0 |

- Laufzeit Wächter: 4,1 s (weiche Grenze nicht überschritten). Aufruf wie in der Test-Konfiguration, Log `.test-logs\last-run.log`.
- Infrastructure.Tests vorher 243 (Stand main), jetzt 347 (+104 AP-03a-Tests). Testzahl nicht leer.
- Fall 22 (v1 -> v2 Migration): grün, v1-DDL im Test musste nicht angepasst werden.
- Fall 28 und `MailKitTransportFactory`: manuell (Dennis, SyncProbe), nicht automatisiert.
- Fehlschläge: keine. Produktcode nicht angefasst.
- Aufgabenzähler in `TODO.md` nicht verändert (Zähler setzt der Planer beim Merge nach `main` zurück).

## verify.2 (nach fix.2, Fälle 29-34)

**Ergebnis: PASS** (S3, volle Suite)

| Projekt | Tests | Bestanden | Fehlgeschlagen |
|---|---|---|---|
| MailWarden.Core.Tests | 299 | 299 | 0 |
| MailWarden.Infrastructure.Tests | 359 | 359 | 0 |
| Gesamt | 658 | 658 | 0 |

- Laufzeit Wächter: 4,9 s.
- Build-Fehler xUnit2029 in `SyncResilienceTests.cs` (Einordnung a, eigener Testfehler): `Assert.Empty(...Where(...))` ersetzt durch `Assert.DoesNotContain(...)`, Sollverhalten unverändert. `dotnet build -warnaserror` danach grün.
- Fehlschläge im Lauf: keine. Produktcode nicht angefasst.
