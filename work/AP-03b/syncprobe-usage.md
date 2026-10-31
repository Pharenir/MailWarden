# SyncProbe – Änderung in AP-03b

Die SyncProbe gibt jetzt während des Laufs Fortschrittszeilen aus (Ordnernummer/-anzahl, Rolle, verarbeitet/fehlend).
Wie bei den übrigen Ausgaben stehen darin keine Ordnernamen, Betreffs oder Adressen.

Aufruf, Voraussetzungen, Datenordner, Ausgabe und Prüfpunkte sind unverändert, siehe `work\AP-03a\syncprobe-usage.md`.
Ein Neuabgleich ist deutlich schneller (Mails werden in Blöcken geladen); bei Verbindungsabbruch verbindet der Lauf
höchstens 3× neu. `[Gmail]/Important` taucht nicht mehr auf.
