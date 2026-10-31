# SyncProbe – Bedienung (AP-03a, Fall 28)

Die SyncProbe gleicht ein echtes Konto einmalig vom Server in einen Probe-Datenordner ab und gibt nur Zahlen aus. Sie
ist nur lesend: Auf dem Server wird nichts verändert, ungelesene Mails bleiben ungelesen.

## Voraussetzung
- Im Probe-Datenordner liegt eine `auth-config.json` (OAuth-Client-Werte, siehe `docs\auth-setup.md`).
- Du hast dich vorher mit der AuthProbe (`_scratch\auth-probe`) **am selben Datenordner** angemeldet. Die SyncProbe
  öffnet nie einen Browser. Fehlt die Anmeldung, meldet sie: "sign in with the AuthProbe first".

## Datenordner
Empfohlen außerhalb des Projekts, damit keine echten Mails ins Repo geraten:
`K:\Projekte\_private\MailWarden\probe-data`. Der Ordner enthält danach echte Mails (`mail\`) und die Datenbank.
Nicht committen, nicht weitergeben.

## Aufruf
Aus dem Projektordner:

```
dotnet run --project _scratch\sync-probe -- <dataRoot> <address> [initialDays]
```

- `<dataRoot>`: Probe-Datenordner (voller Pfad)
- `<address>`: E-Mail-Adresse des Kontos (muss mit der AuthProbe angelegt sein)
- `[initialDays]`: Zeitfenster des ersten Abgleichs je Ordner in Tagen, Standard `0` = alle Mails

Beispiel (künstliche Adresse):

```
dotnet run --project _scratch\sync-probe -- K:\Projekte\_private\MailWarden\probe-data user_a@example.test 30
```

Die Probe läuft bis zum Ende; mit Strg+C bricht sie sauber ab. Fertige Ordner behalten ihren Stand, der nächste Lauf
macht weiter. Ein zweiter Aufruf mit denselben Parametern holt nur Neues und gleicht Flags und Löschungen ab.

## Ausgabe (nur Zahlen)
- Datenordner, Schema-Version (soll `2` sein), Anbieter und Zeitfenster
- Je Ordner eine nummerierte Zeile (ohne Ordnernamen): Rolle, Hinzugefügt, Flags aktualisiert, Gelöscht, UIDVALIDITY
  zurückgesetzt (yes/no), Fehlertyp (`-` = kein Fehler)
- Summen (hinzugefügt, Flags, gelöscht, Ordner mit Fehler) und Dauer

Keine Betreffs, Absender, Texte oder Ordnernamen. Exit-Code 0 = alles ok, 3 = mindestens ein Ordner mit Fehler oder
Abbruch durch Netzwerk/Protokoll, 2 = Anmeldung fehlt oder Token-Fehler, 1 = falscher Aufruf oder Konto unbekannt.

## Was du prüfen sollst (Fall 28)
1. Die Zahl hinzugefügter Mails je Rolle (Inbox, Sent, Junk, …) ist plausibel gegen das Webmail (innerhalb des
   Zeitfensters).
2. **Ungelesene Mails bleiben ungelesen**: Im Webmail die Anzahl ungelesener Mails vor und nach dem Lauf vergleichen.
3. Gmail: `[Gmail]/All Mail` taucht nicht auf (kein Ordner mit Rolle `Archive` doppelt); Labels können dieselbe Mail
   mehrfach zählen, das ist so gewollt.
4. Zweiter Lauf: Hinzugefügt ≈ 0, keine Fehler. Optional: im Webmail eine Mail löschen oder als gelesen markieren,
   dritter Lauf zeigt Gelöscht 1 bzw. Flags 1.
5. Fehler in der Spalte Error: Typ notieren (z. B. `MailTransportException`) und melden.

## Ablage
Die Probe liegt in `_scratch\sync-probe` (git-ignoriert). Im Worktree von AP-03a existiert sie dort; nach dem Merge
liegt sie im Hauptordner unter `_scratch\sync-probe`.
