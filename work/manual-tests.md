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
- [x] Fenster „MailWarden“ erscheint (leer – es gibt noch keine Ansicht)
- [x] Hell/Dunkel folgt Windows (Einstellungen → Personalisierung → Farben umschalten, Fenster wechselt mit)
- [x] Neben der Exe entsteht ein Ordner `data\`

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
Select-String -Path "$probeData\credentials\*" -Pattern 'refresh_token','access_token','hotmail','googlemail' -SimpleMatch
# (kein '@' suchen: die verschlüsselten Dateien enthalten zufällig das Byte 0x40)
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

## 6 – Oberfläche lesen (AP-04a Fälle 42–48)
Eigenes PowerShell-Terminal. Die App arbeitet auf einer **Kopie** der echten Daten
(`K:\Projekte\_private\MailWarden\probe-copy-2026-11-01`, Warteschlange als leer geprüft; AP-04a migriert sie nicht).
Teil 5 (Aufräumen) nicht vorher ausführen.

Vorab:
- Der Dienst gleicht beim Start mit den **echten Servern** ab. Er liest nur; die Oberfläche ändert nichts am Server.
- Die App vor dem Start der AuthProbe beenden (gleiche Anmeldedaten).
- Im Webmail je Konto die Zahl der **ungelesenen** Mails im Posteingang notieren (für Fall 48).

Vorbereitung und Start (zeilenweise kopierbar):
```powershell
cd K:\Projekte\Windows-Tools\MailWarden
Get-Content _scratch\auth-spike\spike.env | Where-Object { $_ -match '^\s*(MAILWARDEN_[A-Z_]+)\s*=\s*(.+?)\s*$' } | ForEach-Object { Set-Item "env:$($Matches[1])" $Matches[2] }
$env:MAILWARDEN_DATA_DIR = 'K:\Projekte\_private\MailWarden\probe-copy-2026-11-01'
cd K:\Projekte\Windows-Tools\MailWarden\.worktrees\feat-ap-04a
dotnet build MailWarden.sln -warnaserror
& .\src\MailWarden.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\MailWarden.App.exe
```
- [ ] Build grün (0 Warnungen, 0 Fehler); die Exe liegt am genannten Pfad (sonst Ausgabeordner der Build-Meldung nehmen)

**Fall 42 – Start mit Datenkopie**
- [ ] Fenster „MailWarden“ öffnet, kein Absturz, keine Fehlermeldung
- [ ] „Posteingang“ ist beim Start in der Navigation markiert, die Liste zeigt Mails
- [ ] Drei Konten sichtbar; Ungelesen-Zahlen plausibel zum Webmail; Kontostatus wechselt von „Wird abgeglichen …“ zu „Aktuell“
- [ ] Gruppen Gesendet, Junk usw. und Ordnerbaum je Konto lassen sich öffnen; Eltern-Knoten ohne Ansicht ändern die Auswahl nicht
- [ ] Eine Mail mit dem Betreff `MailWarden test` ist im Konto 2 bzw. im Google-Konto sichtbar
- [ ] Im Lesebereich erscheinen Kopf (Von, An, Cc, Datum, Anhangnamen) und Inhalt; Kopfbereich und Lesebereich bleiben erreichbar, auch wenn die Liste groß gezogen oder das Fenster klein gemacht wird
- [ ] Liste mit sehr vielen Mails: nach unten scrollen lädt weitere nach
- [ ] Fenster schließen beendet den Prozess: `Get-Process MailWarden.App -ErrorAction SilentlyContinue` gibt nichts aus
- [ ] Schließen **während des Ladens** (App starten, sofort das Fenster schließen): Prozess endet ebenfalls, kein Fehlerfenster

**Fall 43 – Layout und Größen**
- [ ] „Ansicht wechseln“: Liste und Lesebereich wechseln zwischen nebeneinander und untereinander; die Navigation bleibt links
- [ ] Navigationsbreite und Listengröße mit dem Splitter ändern, in beiden Layouts
- [ ] Beenden, neu starten: Layout und Größen sind erhalten (`ui-settings.json` im Datenordner)

**Fall 44 – Design**
- [ ] „Design“: System, Hell, Dunkel wechseln das Fenster sofort; mit System folgt es Windows
- [ ] Mail-Inhalt bleibt in allen drei Varianten lesbar (heller Grund)

**Fall 45 – Newsletter, Bilder und Links** (Mail mit Bildern aus dem Internet auswählen)
- [ ] Bilder sind blockiert; die Schaltfläche heißt „Bilder laden (n blockiert)“ mit passender Zahl
- [ ] „Bilder laden“ zeigt die Bilder nur für diese Mail; eine andere Mail wählen und zurückgehen: wieder blockiert
- [ ] Mit der Maus über einen Link: das Ziel wird angezeigt
- [x] Klick auf einen `http(s)`-Link öffnet den Standardbrowser, nicht die App
- [x] Klick auf einen `mailto:`-Link: ein echter Klick öffnet das Standard-Mailprogramm; öffnet sich nichts, melde das
- [ ] Schnelles Durchklicken durch viele normale Mails (z. B. Pfeil-nach-unten gedrückt halten): jede Mail erscheint formatiert, **ohne** Hinweis und **ohne** Klartext-Ersatz
- [ ] Dieselbe Mail erneut anklicken: sie bleibt normal (kein Hinweis, nicht als Klartext)
- [ ] Erscheint bei einer Mail ein Hinweis über dem Inhalt („ungewöhnlich aufgebaut …“, „dauert zu lange …“, „zu tief verschachtelt …“): Wortlaut melden, ohne Betreff und Adresse

**Fall 46 – neue Mail im laufenden Betrieb** (App läuft)
- [ ] Schicke von außen eine Mail an ein Konto: sie erscheint im Posteingang innerhalb von Sekunden (bis zu einigen Minuten, wenn der Anbieter kein IDLE bietet)
- [ ] Die Ungelesen-Zahl steigt um 1
- [ ] „Aktualisieren“ löst einen Abgleich aus, ohne dass die App hängt

**Fall 47 – Fehlerfälle** (zweites Terminal mit denselben Zeilen aus der Vorbereitung)
- [ ] Bei laufender erster App die Exe ein zweites Mal starten (letzte Zeile der Vorbereitung): Fenster „MailWarden kann nicht starten“ mit dem Text über den bereits verwendeten Datenordner, kein stilles Ende; die erste App läuft weiter
- [ ] Erste App beenden. Ungültiger Pfad: `$env:MAILWARDEN_DATA_DIR = 'relativ\pfad'`, App starten: Fenster mit dem Text über den ungültigen Datenordner. Danach `$env:MAILWARDEN_DATA_DIR` wieder auf die Kopie setzen (Zeile aus der Vorbereitung)
- [ ] Fehlerleiste: `Set-Content "$env:MAILWARDEN_DATA_DIR\ui-settings.json" '{"foo":1}'`, App starten: oben erscheint eine Leiste „Die Einstellungsdatei ist ungültig; es gelten die Standardwerte.“; sie lässt sich schließen und kommt nicht sofort wieder. Danach die Datei löschen: `Remove-Item "$env:MAILWARDEN_DATA_DIR\ui-settings.json"`

**Fall 48 – Server unverändert**
- [x] Nach dem Lesen mehrerer ungelesener Mails in der App: im Webmail sind dieselben Mails weiterhin ungelesen, die Zahlen der Ungelesenen entsprechen den vor dem Test notierten
- [x] Auch in der App bleiben die gelesenen Mails fett (ungelesen)

**Änderungen nach dem ersten Test (fix.6)**
- [ ] Ordnerbaum: Die Ungelesen-Zahlen bleiben neben dem Scrollbalken sichtbar
- [ ] Ordnerbaum: Kontonamen sind fett und etwas größer; Auswahl und Auf-/Zuklappen funktionieren normal
- [ ] Mail in Junk oder Quarantäne mit eingebettetem Bild: kein Bild, „Bilder laden (n blockiert)“ zählt es mit; nach dem Klick erscheinen die Bilder nur für diese Mail
- [ ] Dieselbe Mail in einem normalen Ordner (oder eine Mail im Posteingang): eingebettete Bilder erscheinen sofort

**Das meldest du an Claude** (keine Betreffs oder Adressen nötig)
- je Fall bestanden oder nicht, bei Abweichung Fallnummer und die Beobachtung in einem Satz
- Wortlaut von Fehlerfenstern, Leisten und Hinweisen über dem Mail-Inhalt
- ob `mailto:` und der Standardbrowser sich öffneten
- ob eine Mail unerwartet nur als Text oder mit Hinweis erschien (wie viele von etwa wie vielen)
- wenn vorhanden: die letzten Zeilen von `$env:MAILWARDEN_DATA_DIR\logs\mailwarden.log` (enthalten nur Typnamen)
