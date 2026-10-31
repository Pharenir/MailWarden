# AP-03a – Dokumentationsnotizen (documenter)

Basis: `requirement.md` (inkl. Decisions fix.2), `impl-notes.md`, Diff `d39915b...feat/ap-03a-sync-down` (src, .gitignore).
Beschrieben ist nur der Stand nach fix.2.

## Änderungen
| Datei:Abschnitt | Änderung |
|---|---|
| `README.md`: Status | Abgleich Server → lokal ergänzt (nur lesend, Bibliothek ohne Oberfläche/Zeitplan/IDLE); Kontoeinrichtung in AP-04, keine Posteingangsansicht |
| `README.md`: Build & run, Tests | Sync-Tests (fake Serververbindung, kein echter Server) genannt |
| `README.md`: Data folder, Tabelle | `mailwarden.db` = Schema v2 inkl. Abgleichstand je Ordner, automatische Aktualisierung älterer Datenbanken; `mail\` je Konto und Ordner |
| `README.md`: Data folder, Absatz darunter | "app does not use it yet" auf "does not start a sync yet" angepasst |
| `README.md`: Project structure | Core `Sync`, Infrastructure `Sync` und `Mime`; Tests Infrastructure um Sync ergänzt |
| `README.md`: AI usage, Environment variables | unverändert |
| `work\AP-03a\syncprobe-usage.md` | neu: Bedienungsanleitung SyncProbe (Fall 28) für Dennis; nicht in der README, da das Werkzeug in `_scratch\` liegt |

## Nicht dokumentiert (bewusst)
- Interne Regeln (R1–R11, Fehlerbehandlung je Mail, Wiederaufnahme) – kein Nutzerthema, stehen in `requirement.md`.
- Der Datenordner-ACL-Hinweis (secure M2) ist offen (AP-04, Dennis entscheidet); daher keine Aussage in der README.

## Rückfragen an Dennis
- `syncprobe-usage.md` nennt `dotnet run --project _scratch\sync-probe`; die AuthProbe-Bedienung liegt nicht in diesem
  Worktree und wurde nicht geprüft (Pfad `_scratch\auth-probe` laut Auftrag).
- `docs\classifier.md` / `docs\auth-setup.md`: keine Änderung nötig.
