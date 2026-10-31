# ServiceProbe – Kurzanleitung (AP-03d, Fall 19)

Das Werkzeug liegt im Worktree unter `_scratch\service-probe` (git-ignoriert) und startet den echten Hintergrunddienst
für alle Konten eines Datenordners. Nur Dennis bzw. der Planner startet es, in einem eigenen Terminal.

## Aufruf
```
dotnet run --project _scratch\service-probe -- <Datenordner> [Minuten]
```
- `<Datenordner>`: **nur eine Kopie** von `probe-data` verwenden, nie den echten Datenordner. Der Dienst führt offene
  Befehle aus und ändert damit den Server.
- `[Minuten]`: Laufzeit, Standard 10.
- Beenden: Ctrl+C (sauberes Stoppen), sonst nach Ablauf der Minuten.

## Start-Sperre
Sind im Datenordner Befehle mit Status Pending in der Warteschlange, startet das Werkzeug nicht (Exit 3), weil jeder
Zyklus die Warteschlange ausführen würde. Das Werkzeug stellt selbst nichts in die Warteschlange.

## Ausgabe
Eine Zeile je Statuswechsel, ohne Namen, Adressen oder Betreffs:
```
#<Index> <Anbieter> <Zustand> error=<Typ> last ok=<hh:mm:ss> next=<hh:mm:ss> folder n/m <Rolle> verarbeitet/Kandidaten
```
- `#<Index>`: laufende Nummer des Kontos.
- Zustand: Running, Idle, WaitingAfterError, NeedsSignIn, Stopped.
- `error`: nur der Fehlertyp, z. B. `MailTransportException(Network)`.
- `last ok` / `next`: letzter Erfolg und nächster geplanter Lauf.
- `folder n/m`: Fortschritt über die Ordner; reine Fortschrittszeilen höchstens 1 pro Sekunde und Konto.

IDLE-Zeilen:
- `idle: connected` – Wächter verbunden.
- `wait N started (max 9 min)` – IDLE wartet.
- `wait N change -> trigger` – Server meldete eine Änderung, ein Zyklus folgt nach ca. 2 s.
- `wait N timeout -> renew` – 9 min um, IDLE wird erneuert.
- `wait N failed <Typ>` – Verbindungsproblem, Wächter verbindet nach Wartezeit neu.
- `disposed` – Wächter beendet.

## Exit-Codes
0 ok · 1 Aufruf falsch · 2 Datenordner in Benutzung (zweite Instanz) · 3 Start verweigert (offene Befehle) ·
4 `StopAsync` meldete Fehler in Schleifen.

## Ablauf Fall 19
1. Start, alle Konten sollten nach dem ersten Zyklus `Idle` sein.
2. Zweite Instanz auf demselben Ordner starten: muss mit „in use“ und Exit 2 enden.
3. Testmail an das Gmail-Konto senden: `change -> trigger`, nach ca. 2 s ein Zyklus.
4. Nach 9 min `timeout -> renew`, danach `wait 2 started`.
5. Ctrl+C: Dauer des Stoppens und Endstatus je Konto werden ausgegeben.
