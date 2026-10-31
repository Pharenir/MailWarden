# Manuelle Tests für Dennis (Stand 2026-10-31, `main` nach AP-03a/AP-07b)

Reihenfolge einhalten: Teil 2 legt die Anmeldung an, die Teil 3 braucht. Alle Befehle in **einem eigenen
PowerShell-Terminal** im Projektordner `K:\Projekte\Windows-Tools\MailWarden` (nicht aus der Claude-Session). Echte
Daten landen nur unter `K:\Projekte\_private\MailWarden\probe-data`, nie im Projektordner.

Ergebnis je Punkt abhaken; bei Abweichung Fallnummer + Ausgabezeile an Claude geben (keine Betreffs/Adressen nötig).

## 1 – App starten (AP-01 Fall 30)
```powershell
dotnet build MailWarden.sln -warnaserror
& .\src\MailWarden.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\MailWarden.App.exe
```
- [ ] Build grün (0 Warnungen, 0 Fehler)
- [ ] Fenster „MailWarden“ erscheint (leer – es gibt noch keine Ansicht)
- [ ] Hell/Dunkel folgt Windows (Einstellungen → Personalisierung → Farben umschalten, Fenster wechselt mit)
- [ ] Neben der Exe entsteht ein Ordner `data\`

## 2 – Anmeldung mit der AuthProbe (AP-02a Fälle 28–30)
Vorbereitung (einmal je Terminal):
```powershell
$probeData = 'K:\Projekte\_private\MailWarden\probe-data'
New-Item -ItemType Directory -Force $probeData | Out-Null
# Client-Werte aus der vorhandenen spike.env (AP-00) in dieses Terminal laden – gibt nichts aus
Get-Content _scratch\auth-spike\spike.env | Where-Object { $_ -match '^\s*(MAILWARDEN_[A-Z_]+)\s*=\s*(.+?)\s*$' } |
    ForEach-Object { Set-Item "env:$($Matches[1])" $Matches[2] }
dotnet build _scratch\auth-probe
function AuthProbe { dotnet run --no-build --project _scratch\auth-probe -- $probeData @args }
```

**Fall 28 – Anmelden** (Browser öffnet sich, 5 Minuten Zeit):
```powershell
AuthProbe add ms <erste hotmail.de-Adresse>
AuthProbe add ms <zweite hotmail.de-Adresse>
AuthProbe add google <googlemail-Adresse>
AuthProbe list
AuthProbe probe <Adresse>          # je Konto
```
- [ ] Jedes `probe`: `IMAP ok: True, SMTP ok: True`, keine Zeile `Error`
- [ ] Kontozeile zeigt die Adresse, mit der du dich wirklich angemeldet hast
- [ ] Outlook-Ordner: Inbox/Sent/Junk/Drafts/Deleted→Trash/Archive sichtbar; Notes, Outbox, Scheduled,
      Synchronisierungsprobleme versteckt
- [ ] Gmail-Ordner: INBOX, Spam→Junk, Bin/Papierkorb→Trash, Sent Mail, Drafts sichtbar; `[Gmail]`, All Mail,
      Important, Starred versteckt; ein eigenes Label `Junk-E-Mail` erscheint als normaler sichtbarer Ordner

**Fall 29 – ohne Browser** (neues Terminal, Vorbereitung wiederholen):
```powershell
AuthProbe probe <Adresse>          # je Konto
AuthProbe list
Select-String -Path "$probeData\credentials\*" -Pattern 'refresh_token','access_token','@' -SimpleMatch
```
- [ ] Kein Browser, Zeile `Token (silent, no browser)`
- [ ] Nur `.bin`-Dateien (`msal.cache.bin`, je Google-Konto `google.<64 Hex>.bin`); `Select-String` findet nichts
- [ ] `%APPDATA%\Google.Apis.Auth` existiert **nicht**

**Fall 30 – Abbrechen**:
```powershell
AuthProbe add ms                   # auf der Microsoft-Seite „Abbrechen“
AuthProbe add google               # auf der Google-Seite „Abbrechen“
AuthProbe list
```
- [ ] Ausgabe `Sign-in failed:` mit `Reason: UserCancelled` oder `AccessDenied`, ein lesbarer Satz, kein Stacktrace
- [ ] `$LASTEXITCODE` ist 2; **nicht** `state_mismatch`
- [ ] `list` zeigt kein neues Konto

Die Anmeldungen **nicht** entfernen – Teil 3 braucht sie.

## 3 – Abgleich mit der SyncProbe (AP-03a Fall 28)
Nur lesend: auf dem Server ändert sich nichts. Standard ist jetzt „alles laden“ (kein Zeitfenster); der erste Lauf
kann bei großen Postfächern dauern, Strg+C bricht sauber ab, der nächste Lauf macht weiter.

Im selben Terminal wie Teil 2 (oder dort die Vorbereitung wiederholen – die SyncProbe braucht die Client-Werte auch).
Vorher im Webmail je Konto notieren: Anzahl Mails im Posteingang, Anzahl **ungelesener** Mails.
```powershell
dotnet run --project _scratch\sync-probe -- $probeData <Adresse>        # je Konto
```
- [ ] Erste Ausgabezeilen: Schema-Version `2`, richtiger Anbieter, Zeitfenster `0 days (0 = all)`
- [ ] Hinzugefügt je Rolle plausibel (Inbox ≈ Webmail-Zahl; Gmail: Labels dürfen dieselbe Mail mehrfach zählen)
- [ ] Gmail: kein Ordner mit All Mail (keine doppelte Archive-Zeile mit allen Mails)
- [ ] Spalte Error überall `-`, Exit-Code 0 (sonst Fehlertyp notieren)
- [ ] **Ungelesen im Webmail nach dem Lauf unverändert**
- [ ] Zweiter Lauf: Hinzugefügt ≈ 0, keine Fehler
- [ ] Optional: im Webmail eine Mail löschen und eine als gelesen markieren → dritter Lauf zeigt Gelöscht 1, Flags 1

Danach liegen echte Mails unter `$probeData\mail\` – nur dort lassen, nicht weitergeben.

## 4 – Begründungstexte des Spamfilters (AP-07a/AP-07b)
```powershell
notepad docs\classifier.md
dotnet run --project _scratch\ClassifierProbe
```
- [ ] `docs\classifier.md`: Begründungstexte verständlich, Schwellen (30/70) und Signale nachvollziehbar
- [ ] Neu in AP-07b nachvollziehbar: `compauth=pass` macht DMARC-Fehler zu „nicht verifiziert“ (0 Punkte);
      Inhalts-Signale zählen bei authentifizierten Absendern nicht; Datei `classifier-settings.json` (noch nicht
      von der App gelesen)
- [ ] ClassifierProbe: Zeilen mit Signal, Punkten und Begründung lesbar; am Ende keine `FAIL`-Zeile

## 5 – Aufräumen (optional, erst wenn alles geprüft ist)
```powershell
AuthProbe remove <Adresse>         # je Konto; danach darf nur msal.cache.bin bleiben
Remove-Item -Recurse -Force 'K:\Projekte\_private\MailWarden\probe-data'
```
Behältst du `probe-data`, kann Claude den Abgleich später für AP-03b/AP-07c weiter nutzen (nur anonymisierte Zahlen).
