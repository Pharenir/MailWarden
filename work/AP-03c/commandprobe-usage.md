# CommandProbe – Kurzanleitung (AP-03c, Fall 24)

Werkzeug: `_scratch\command-probe` im Worktree `.worktrees\feat-ap-03c` (git-ignoriert). Aufruf:

```
dotnet run --project _scratch\command-probe -- <dataRoot> <address> <action> [args]
```

## Vorbereitung
- **Nur eine Kopie von `probe-data` verwenden**, nie die Originaldaten oder den Datenordner der App. Dieser Build
  migriert die Datenbank auf Schema 4; SyncProbe/sync-check aus `main` lehnen sie danach ab.
- Freigabedatei `<dataRoot>\command-probe-verified.json` (das Werkzeug liest sie nur, schreibt sie nie): je Konto ein
  Eintrag mit `junkFolderId`, `uidValidity` (ungleich 0) und `uids` (nur geprüfte Junk-Mails). Fehlt Datei oder
  Eintrag, werden alle Junk-Aktionen verweigert.
- Beim Start erscheint eine Warnung (nur Kopie von `probe-data`, keine App/kein Dienst/keine andere Probe parallel).

## Aktionen
`junk` · `seen <uid>` · `unseen <uid>` · `trash <uid>` · `delete <uid>` · `delete <uid> trash` · `empty-junk` ·
`queue` · `discard <id>` · `run`

## Reihenfolge für „Papierkorb, dann dort löschen“
1. `run` (Abgleich direkt vor `trash`, damit der Papierkorb-Stand aktuell ist)
2. `trash <uid>` (Junk-UID aus der Freigabeliste)
3. `run` (führt den Befehl aus, gleicht ab, ordnet die Mail der Papierkorb-UID zu)
4. `delete <uid> trash` (die zugeordnete Papierkorb-UID)
5. `run`

Bleibt die Zuordnung offen (kein oder mehrere Treffer), wird `delete … trash` verweigert.

## Aufräumen
`<dataRoot>\command-probe-trash.json` nach Gebrauch **löschen**. Sie enthält SHA-256-Hashes der Message-IDs. Die Kopie
von `probe-data` danach ebenfalls entfernen, wenn sie nicht mehr gebraucht wird.
