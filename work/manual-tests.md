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
- [ ] Im Lesebereich erscheinen Kopf (Von, An, Cc, Datum, Anhänge mit Name, Größe, Endung sowie Öffnen und Speichern) und Inhalt; Kopfbereich und Lesebereich bleiben erreichbar, auch wenn die Liste groß gezogen oder das Fenster klein gemacht wird
- [ ] Liste mit sehr vielen Mails: nach unten scrollen lädt weitere nach
- [ ] Fenster schließen beendet den Prozess: `Get-Process MailWarden.App -ErrorAction SilentlyContinue` gibt nichts aus
- [ ] Schließen **während des Ladens** (App starten, sofort das Fenster schließen): Prozess endet ebenfalls, kein Fehlerfenster

**Fall 43 – Layout und Größen**
- [ ] „Ansicht wechseln“: Liste und Lesebereich wechseln zwischen nebeneinander und untereinander; die Navigation bleibt links
- [ ] Navigationsbreite und Listengröße mit dem Splitter ändern, in beiden Layouts
- [ ] Beenden, neu starten: Layout und Größen sind erhalten (`ui-settings.json` im Datenordner)

**Fall 44 – Design**
- [ ] „Design“: System, Hell, Dunkel wechseln das Fenster sofort; mit System folgt es Windows (Nightshade: Teil 10)
- [ ] Mail-Inhalt bleibt in allen drei Varianten lesbar (heller Grund)

**Fall 45 – Newsletter, Bilder und Links** (Mail mit Bildern aus dem Internet auswählen)
- [ ] Bilder sind blockiert; die Schaltfläche heißt „Bilder laden (n blockiert)“ mit passender Zahl
- [ ] „Bilder laden“ zeigt die Bilder nur für diese Mail; eine andere Mail wählen und zurückgehen: wieder blockiert
- [x] Mit der Maus über einen Link: das Ziel wird angezeigt
- [x] Klick auf einen `http(s)`-Link öffnet den Standardbrowser, nicht die App
- [x] Klick auf einen `mailto:`-Link: ein echter Klick öffnet das Standard-Mailprogramm; öffnet sich nichts, melde das
- [ ] Schnelles Durchklicken durch viele normale Mails (z. B. Pfeil-nach-unten gedrückt halten): jede Mail erscheint formatiert, **ohne** Hinweis und **ohne** Klartext-Ersatz
- [ ] Dieselbe Mail erneut anklicken: sie bleibt normal (kein Hinweis, nicht als Klartext)
- [ ] Erscheint bei einer Mail ein Hinweis über dem Inhalt („ungewöhnlich aufgebaut …“, „dauert zu lange …“, „zu tief verschachtelt …“): Wortlaut melden, ohne Betreff und Adresse

**Fall 46 – neue Mail im laufenden Betrieb** (App läuft)
- [x] Schicke von außen eine Mail an ein Konto: sie erscheint im Posteingang innerhalb von Sekunden (bis zu einigen Minuten, wenn der Anbieter kein IDLE bietet)
- [x] Die Ungelesen-Zahl steigt um 1
- [x] „Aktualisieren“ löst einen Abgleich aus, ohne dass die App hängt

**Fall 47 – Fehlerfälle** (zweites Terminal mit denselben Zeilen aus der Vorbereitung)
- [x] Bei laufender erster App die Exe ein zweites Mal starten (letzte Zeile der Vorbereitung): Fenster „MailWarden kann nicht starten“ mit dem Text über den bereits verwendeten Datenordner, kein stilles Ende; die erste App läuft weiter
- [ ] Erste App beenden. Ungültiger Pfad: `$env:MAILWARDEN_DATA_DIR = 'relativ\pfad'`, App starten: Fenster mit dem Text über den ungültigen Datenordner. Danach `$env:MAILWARDEN_DATA_DIR` wieder auf die Kopie setzen (Zeile aus der Vorbereitung)
- [ ] Fehlerleiste: `Set-Content "$env:MAILWARDEN_DATA_DIR\ui-settings.json" '{"foo":1}'`, App starten: oben erscheint eine Leiste „Die Einstellungsdatei ist ungültig; es gelten die Standardwerte.“; sie lässt sich schließen und kommt nicht sofort wieder. Danach die Datei löschen: `Remove-Item "$env:MAILWARDEN_DATA_DIR\ui-settings.json"`

**Fall 48 – Server unverändert**
- [x] Nach dem Lesen mehrerer ungelesener Mails in der App: im Webmail sind dieselben Mails weiterhin ungelesen, die Zahlen der Ungelesenen entsprechen den vor dem Test notierten
- [x] Auch in der App bleiben die gelesenen Mails fett (ungelesen)

**Änderungen nach dem ersten Test (fix.6)**
- [x] Ordnerbaum: Die Ungelesen-Zahlen bleiben neben dem Scrollbalken sichtbar
- [x] Ordnerbaum: Kontonamen sind fett und etwas größer; Auswahl und Auf-/Zuklappen funktionieren normal
- [x] Mail in Junk oder Quarantäne mit eingebettetem Bild: kein Bild, „Bilder laden (n blockiert)“ zählt es mit; nach dem Klick erscheinen die Bilder nur für diese Mail
- [x] Dieselbe Mail in einem normalen Ordner (oder eine Mail im Posteingang): eingebettete Bilder erscheinen sofort

**Das meldest du an Claude** (keine Betreffs oder Adressen nötig)
- je Fall bestanden oder nicht, bei Abweichung Fallnummer und die Beobachtung in einem Satz
- Wortlaut von Fehlerfenstern, Leisten und Hinweisen über dem Mail-Inhalt
- ob `mailto:` und der Standardbrowser sich öffneten
- ob eine Mail unerwartet nur als Text oder mit Hinweis erschien (wie viele von etwa wie vielen)
- wenn vorhanden: die letzten Zeilen von `$env:MAILWARDEN_DATA_DIR\logs\mailwarden.log` (enthalten nur Typnamen)

## 7 – Spamfilter in der laufenden App (AP-07d Fall 17)
Gleiche Datenkopie wie in Teil 6 (`K:\Projekte\_private\MailWarden\probe-copy-2026-11-01`), eigenes PowerShell-Terminal.
Der Filter läuft jetzt nach jedem Abgleich und **protokolliert nur**, solange es keine Datei `classification-policy.json`
im Datenordner gibt. Öffne die Datenbank nicht; geprüft wird nur, was du in der App siehst.

Vorbereitung und Start (Zeilen wie in Teil 6, aber der Ordner von AP-07d):
```powershell
cd K:\Projekte\Windows-Tools\MailWarden
Get-Content _scratch\auth-spike\spike.env | Where-Object { $_ -match '^\s*(MAILWARDEN_[A-Z_]+)\s*=\s*(.+?)\s*$' } | ForEach-Object { Set-Item "env:$($Matches[1])" $Matches[2] }
$env:MAILWARDEN_DATA_DIR = 'K:\Projekte\_private\MailWarden\probe-copy-2026-11-01'
Test-Path "$env:MAILWARDEN_DATA_DIR\classification-policy.json"    # muss False sein
cd K:\Projekte\Windows-Tools\MailWarden\.worktrees\feat-ap-07d
dotnet build MailWarden.sln -warnaserror
& .\src\MailWarden.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\MailWarden.App.exe
```
- [ ] Build grün (0 Warnungen, 0 Fehler); `Test-Path` gibt `False` aus (sonst die Datei nicht anlegen und melden)

**Fall 17 – neue Mail, nichts wandert** (App läuft, Kopie, keine Richtliniendatei)
- [ ] Notiere im Webmail für Konto 1, 2 und 3 die Zahl der Mails im Posteingang und in Junk
- [ ] Schicke von außen eine Mail an ein Konto. Sie erscheint im Posteingang der App wie in Teil 6 (Fall 46)
- [ ] Nichts wandert: die Mail bleibt im Posteingang, im Webmail ändert sich nichts in Junk, Screener oder Quarantäne
- [ ] Es erscheint **kein** Hinweis in der Infoleiste („Spamfilter seit dem Start …“ oder „… wartet auf das Adressbuch“)
- [ ] Der Abgleich ist nicht merklich langsamer als in Teil 6 (Kontostatus wechselt von „Wird abgeglichen …“ zu „Aktuell“ in etwa derselben Zeit)
- [ ] Fenster schließen beendet den Prozess (`Get-Process MailWarden.App -ErrorAction SilentlyContinue` gibt nichts aus)

**Das meldest du an Claude**
- bestanden oder nicht; bei Abweichung die Beobachtung in einem Satz
- Wortlaut eines Hinweises in der Infoleiste, falls einer erschien (ohne Betreff und Adresse)
- wenn vorhanden: die letzten Zeilen von `$env:MAILWARDEN_DATA_DIR\logs\mailwarden.log` (nur Typnamen und Zahlen)

Fall 18 (Modus `act`) macht der Planer; dafür brauchst du nichts zu tun, außer vorher die Freigabe zu geben.

## 8 – Aktionen in der App (AP-04b Fälle 21, 22, 32)

**Achtung:** Die App läuft auf der Kopie, aber jede Aktion wirkt auf dem echten Server. Fasse nur Testmails an: Ihr Betreff beginnt mit `MailWarden test MWT`. Echte Mails bleiben unberührt, auch in Papierkorb und Junk.

Vorbereitung und Start:
```powershell
cd K:\Projekte\Windows-Tools\MailWarden
Get-Content _scratch\auth-spike\spike.env | Where-Object { $_ -match '^\s*(MAILWARDEN_[A-Z_]+)\s*=\s*(.+?)\s*$' } | ForEach-Object { Set-Item "env:$($Matches[1])" $Matches[2] }
$env:MAILWARDEN_DATA_DIR = 'K:\Projekte\_private\MailWarden\probe-copy-2026-11-01'
Test-Path "$env:MAILWARDEN_DATA_DIR\classification-policy.json"    # muss False sein
cd K:\Projekte\Windows-Tools\MailWarden\.worktrees\feat-ap-04b
dotnet build MailWarden.sln -warnaserror
& .\src\MailWarden.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\MailWarden.App.exe
```

**Fall 21 – Aktionen.** Nach jedem Schritt im Webmail vergleichen, das darf bis zu einer Minute dauern.
- [ ] **Lesen und Kennzeichnen (Konto 3, Posteingang, eine Testmail):** Ctrl+Q macht sie gelesen, Ctrl+U ungelesen. Kennzeichnen setzen und wieder entfernen. Die Zeile ändert sich sofort.
- [ ] **Entf (Konto 2, Posteingang, `MailWarden test MWTd21dd4d9e3 1`):** Die Mail liegt danach im Papierkorb, die nächste Zeile ist ausgewählt und unverändert.
- [ ] **Umschalt+Entf (Konto 2, Posteingang, `MailWarden test MWTd21dd4d9e3 2`):** Es kommt eine Rückfrage. Nach „Ja“ ist die Mail auch im Papierkorb nicht mehr da.
- [ ] **Verschieben (Konto 3, Posteingang, die zwei übrigen Testmails per Mehrfachauswahl):** nach Junk von Konto 3. Beide liegen danach im Webmail im Spam.
- [ ] **Ordner leeren (Konto 3, Junk):** Junk von Konto 3 hatte in der Kopie 0 Mails. Die Rückfrage muss genau die Zahl der verschobenen Testmails nennen. **Steht dort mehr, „Nein“ wählen und melden.** Nach „Ja“ ist der Spam im Webmail leer.
- [ ] **Suche:** Nach `MWT` suchen, die Treffer stimmen. Nach dem Leeren der Suche erscheint wieder die ganze Liste.
- [ ] **Gelesen-Schalter:** Einschalten, eine Testmail öffnen, nach etwa 1,5 s ist sie gelesen. Danach wieder ausschalten.

**Fall 22 – Fenster und Tasten.**
- [ ] Größe und Position ändern, schließen, neu starten: beides ist erhalten.
- [ ] Maximieren, schließen, neu starten: wieder maximiert.
- [ ] Die Tasten aus Fall 21 wirken nur auf die Mail-Liste, nicht beim Tippen im Suchfeld.

**Fall 32 – Link-Ziel.** Eine Mail mit einem Link öffnen, dessen Text nicht das Ziel ist.
- [ ] Beim Zeigen auf den Link erscheint das echte Ziel im Tooltip an der Maus **und** in der Statusleiste. Danach entscheidet Dennis, ob die Statusleiste ausgeschaltet wird.

### 8b – Nachtrag aus dem ersten Test (AP-04b X und Y)
Start wie in Teil 8. Nur Testmails anfassen (Betreff beginnt mit `MailWarden test MWT`).
- [ ] **Link-Ziel ohne Statusleiste (Pflicht vor dem Merge):** Mail mit einem Link öffnen, dessen Text nicht das Ziel ist. Die Statusleiste unten erscheint nicht mehr, der Tooltip an der Maus zeigt das echte Ziel. Das Gleiche im schmalen Fenster: Der Tooltip bleibt ganz lesbar.
- [ ] **Rechtsklick im Ordnerbaum:** Auf Junk von Konto 3 und auf den Papierkorb eines Kontos erscheint „Ordner leeren“, auf dem Posteingang und dem Kontoknoten nichts. Ebenso mit der Kontexttaste. Die Auswahl im Baum bleibt.
- [ ] **Leeren über den Baum:** Eine Testmail nach Junk von Konto 3 verschieben, im Baum einen anderen Ordner auswählen, Rechtsklick auf Junk von Konto 3, „Ordner leeren“. **Die Rückfrage muss genau die Zahl der Testmails nennen, sonst „Nein“ wählen und melden.** Die aktuelle Liste bleibt unverändert, im Webmail ist der Spam leer.
- [ ] **Ungelesen-Zahl:** Eine ungelesene Testmail mit Entf löschen: Die Zahl im Baum fällt im selben Moment wie die Zeile. Bei einer gelesenen Testmail bleibt sie gleich.

## 9 – Lesebereich und Thema (AP-04d Fälle 13, 21)
Nur Ansehen, keine Aktionen auf dem Server.
- [x] Dunkel, Newsletter: Das Blatt ist abgedunkelt und gut lesbar, seine Farben bleiben erkennbar.
- [x] Dunkel, Newsletter: Ein Link lässt sich anklicken, der Tooltip zeigt das echte Ziel, Text lässt sich markieren und kopieren.
- [x] Hell, gleicher Newsletter: kein Schleier, sieht aus wie bisher.
- [x] Design System: Windows bei laufender App zwischen Hell und Dunkel umstellen. Die App und der Lesebereich gehen mit.
- [x] Dunkel, vom Newsletter auf eine reine Textmail wechseln: Kein störendes Aufblitzen.

## 10 – Nightshade-Thema (AP-04e Fälle 23–29)
Nur Ansehen, keine Aktionen auf dem Server: **Rückfragen immer mit „Abbrechen“ schließen**, und nur die unten genannten Wege nutzen (die Taste Entf außerhalb des Papierkorbs verschiebt ohne Frage). „Beim Anzeigen als gelesen markieren“ ausschalten oder nur gelesene Mails öffnen. Deine bisherige Einstellung bleibt erhalten, also wählst du Nightshade einmal von Hand.
- [ ] Fall 23: Datenordner ohne `ui-settings.json` (oder Datei ohne `theme`): Die App startet in Nightshade, ohne dass vorher ein anderes Thema aufblitzt.
- [ ] Fall 24: Mit deiner vorhandenen Datei bleibt dein Thema. „Design“ auf Nightshade stellen, neu starten: Nightshade bleibt.
- [ ] Fall 25: Mit offener Mail (einmal Textmail, einmal Newsletter) durch alle vier Themen schalten, über die Liste „Design“. Alles wechselt zugleich, der Lesebereich geht mit, kein anderes Thema blitzt auf. Beim Verlassen von Nightshade kommen Mica und die normale Titelleiste zurück. Auswahl und Bildfreigabe der offenen Mail bleiben.
- [ ] Fall 26: In Nightshade öffnen und ohne Auswahl wieder schließen: Verschieben-Menü, Kontextmenü der Liste, Kontextmenü auf Junk oder Papierkorb eines Kontos im Baum (das obere Junk über alle Konten und andere Ordner haben keins), Einstellungen, Liste „Design“, „Fehlgeschlagene Aktionen“ (falls vorhanden), Link-Tooltip. Rückfrage: „Ordner leeren“ auf Junk oder Papierkorb eines Kontos mit mindestens einer Mail, ansehen, **Abbrechen**. Überall die Palette und lesbarer Text, auch nachdem sie vor einem Wechsel schon einmal offen waren.
- [ ] Fall 26b: Text in den Kopfzeilen der Mail markieren: Die Markierung ist Windows-Blau und nicht Nightshade. Gemessen und bekannt. Stört dich das?
- [ ] Fall 27: Gut lesbar und unterscheidbar: ausgewählte Zeile in Liste und Baum, ungelesene Mails (fett), deaktivierte Buttons, Warnbanner, Hover auf Zeilen und auf Buttons, Rahmen des Suchfelds mit und ohne Fokus, in der Rückfrage der Standard-Button „Abbrechen“ (orange).
- [ ] Fall 28: Newsletter in Nightshade: abgedunkeltes helles Blatt, Links lassen sich klicken, der Tooltip zeigt das Ziel, Text lässt sich markieren.
- [ ] Fall 29 (optional): Windows im Hellmodus mit Nightshade: Palette hält, nur ein Button mit Fokus kann kurz die alte Farbe zeigen. Hochkontrast vor dem Start eingeschaltet: Systemfarben und lesbarer Text, nur der Lesebereich bleibt violett (gewollt). Hochkontrast bei laufender App eingeschaltet: Bis zum nächsten Themenwechsel oder Start kann die dunkle Fensterfläche bleiben (bekannt).

## 11 – Anhänge (AP-04c Fälle 21–26)
Künstlicher Datenordner mit Testkonto ohne Anmeldung, die App erreicht damit keinen Server: `$env:MAILWARDEN_DATA_DIR = 'K:\Projekte\Windows-Tools\MailWarden\_scratch\testdata-ui\data'`. Das Testkonto zeigt, dass eine Anmeldung fehlt; das ist gewollt. Der Ordner wird von `_scratch\attachment-testdata` erzeugt (13 künstliche Mails in Posteingang, Junk, Quarantäne, Screener und Papierkorb; `rechnung.pdf.exe` ist nur Text).
- [ ] Fall 21: PDF, Office-Datei und Bild speichern. Der Dialog schlägt den Namen vor, ohne doppelte Endung; die Datei öffnet sich danach; in den Dateieigenschaften steht der Internet-Vermerk. Office zeigt deshalb die geschützte Ansicht (gewollt). Bei einem großen Anhang erscheint „Anhang wird kopiert …“, danach „Anhang gespeichert.“.
- [ ] Fall 22: PDF aus dem Posteingang öffnen: sofort, die Datei trägt den Internet-Vermerk.
- [ ] Fall 23: Aus Junk, Quarantäne, Screener und Papierkorb öffnen: erst eine Rückfrage; **Abbrechen** öffnet nichts, „Trotzdem öffnen“ öffnet einmal.
- [ ] Fall 24: Mail mit `rechnung.pdf.exe`: nur Speichern, keine Möglichkeit zum Öffnen. Reicht dir die Anzeige ohne Erklärung?
- [ ] Fall 25: App schließen und neu starten: `attachments-open` im Datenordner ist leer. Eine Datei, die noch im Betrachter offen ist, darf bis zum nächsten Start liegen bleiben.
- [ ] Fall 26: Mail mit sehr langem Anhangsnamen und einer `.zip`: Die Endung bleibt in eigener Spalte sichtbar; `zip` fragt zuerst.
- [ ] Zusätzlich: Speichern ohne Endung und mit ungewöhnlicher Endung; `.xyz` öffnen; über eine Datei speichern, die noch im Betrachter offen ist (klare Meldung, alte Datei bleibt); optional in einen OneDrive-Ordner und auf einen USB-Stick ohne NTFS (Speichern klappt, ohne Vermerk). Nightshade: Zeilenhöhe und Spalten passen.
