# PLAN.md – MailWarden

**Status: freigegeben von Dennis am 2026-10-31.** Verbindliche Vorlage für Umsetzung und Tests. Abweichungen werden
gemeldet und nach Freigabe hier nachgezogen.

## Ziel
Ein Windows-Mail-Client für den Eigengebrauch von Dennis, der mehrere Konten (2× hotmail.de, 1× googlemail.com,
erweiterbar) in **einem gemeinsamen Posteingang** zeigt und eingehende Mails mit einem eigenen, nachvollziehbaren
**Vier-Stufen-Spamfilter** plus frei definierbaren Regeln einsortiert. Alle Verschiebe-/Löschaktionen laufen über den
Server (IMAP), damit andere Geräte denselben Stand sehen.

## Nicht-Ziele
- Keine Mehrbenutzer-Fähigkeit, keine Verteilung an Dritte, kein Store/Installer-Signing.
- Kein Exchange/Microsoft-365-Firmenpostfach (nur private Microsoft- und Google-Konten; andere nur, wenn IMAP/SMTP).
- Kein Mobile, kein Web.
- Kein eigener Server-seitiger Spamfilter: Mails, die der Anbieter bereits abweist oder in Quarantäne nimmt, sieht die
  App nicht.
- Kalender und Kontakt-Sync mit den Anbietern erst in Phase 3 (siehe Arbeitspakete), nicht im Mindestumfang v1.

## Stack
| Baustein | Wahl | Grund |
|---|---|---|
| Laufzeit | .NET 9 (SDK 9.0.320 vorhanden), C# | Windows-only, Dennis-Stack |
| UI | **WinUI 3** (Windows App SDK, unpackaged, self-contained), Light/Dark/System über `RequestedTheme` | modernes Fluent-Design (Wunsch Dennis). *Risiko:* Tooling jünger als WPF, UI nicht automatisiert testbar → ViewModels getrennt testen |
| Mail-Anzeige | WebView2 (in WinUI 3 enthalten), HTML vorab bereinigt, externe Inhalte standardmäßig blockiert | Sicherheit |
| IMAP/SMTP | MailKit/MimeKit | Standard, OAuth2-fähig, gepflegt |
| OAuth Microsoft | MSAL.NET (Public Client, `consumers`), Scopes `IMAP.AccessAsUser.All`, `SMTP.Send`, `offline_access` | Outlook.com erlaubt kein Passwort-Login mehr |
| OAuth Google | eigenes Google-Cloud-Projekt, OAuth-Client „Desktop“, Scope `https://mail.google.com/`, App-Status **Production** (unverifiziert, Warnhinweis beim Consent) | im Status „Testing“ verfallen Tokens nach 7 Tagen |
| Speicher | SQLite (`Microsoft.Data.Sqlite`, FTS5) + EML-Dateien auf Platte | lokal, Volltextsuche, einfache Sicherung |
| Zugangsdaten | Windows DPAPI (CurrentUser) für Tokens | kein Klartext |
| Installer | Inno Setup (kostenlos), Zielordner frei wählbar (z. B. `D:\MailWarden`), Daten in `<install>\data\` | alles an einem Ort (Wunsch Dennis) |
| Tests | xUnit, `dotnet test` | Standard |

## Architektur
```
MailWarden.sln
  src\MailWarden.Core\            Domänenmodell, Klassifizierer, Regel-Engine, Schnittstellen (kein IO)
  src\MailWarden.Infrastructure\  IMAP/SMTP (MailKit), OAuth (MSAL/App-Passwort), SQLite, EML-Store, DPAPI
  src\MailWarden.App\             WinUI-3-Oberfläche (MVVM), WebView2, Theme
  installer\                      Inno-Setup-Skript (ab AP-13)
  tests\MailWarden.Core.Tests\
  tests\MailWarden.Infrastructure.Tests\   SQLite in-memory, Fakes für IMAP-Schnittstelle
```
- **Datenfluss:** Server ⇄ `IMailTransport` (MailKit) → `SyncEngine` → SQLite/EML → Klassifizierer + Regeln →
  Aktionen (als Befehle zurück über IMAP) → UI liest nur aus SQLite.
- **Konten:** beliebig viele; jedes Konto hat Provider-Profil (Hosts, Auth-Art, Sonderordner-Mapping). Gmail-Labels:
  `[Gmail]/All Mail` wird nicht in den gemeinsamen Posteingang übernommen (Dubletten).
- **Klassifizierer (Core):** `ISpamSignal`-Bausteine liefern Punkte + Begründung; Summe → Stufe über
  konfigurierbare Schwellen. Jede Entscheidung wird mit allen Begründungen protokolliert.
- **Regel-Engine (Core):** datengetrieben (Bedingungen → Aktionen, in SQLite), läuft vor und nach dem Klassifizierer.
- **Aktionen:** Behalten · Markieren · nach Screener · nach Junk · nach Quarantäne · Löschen. Zuordnung Stufe → Aktion
  ist Einstellung, Standard: `TrustedContact` → Posteingang, `LikelyLegit` → Screener, `LikelySpam` → Junk,
  `CertainSpam` → Quarantäne (Aufbewahrung X Tage, danach löschen); nie direkt löschen, bis Dennis den Schalter setzt.
- **Ordner je Konto:** die App legt auf dem Server die IMAP-Ordner `Screener` und `Quarantine` an, falls sie fehlen;
  `Junk` und `Gesendet` sind die Anbieter-Ordner (Mapping im Provider-Profil).
- **Gruppenansichten (über alle Konten):** Posteingang · Gesendet · Postausgang (noch nicht erfolgreich
  verschickt, lokal) · Screener · Junk · Quarantäne. Jede Ansicht ist zusätzlich je Konto einzeln aufrufbar; die
  Mail-Zeile zeigt immer das Konto. Gruppenansichten sind reine Abfragen auf der lokalen DB (Rolle des Ordners +
  optional Konto-Filter). Der Postausgang ist kein Server-Ordner, sondern die lokale Versandwarteschlange.
- **Datenordner:** `<install>\data\` (DB, EML, Logs, Einstellungen), in der Entwicklung
  `src\MailWarden.App\bin\...\data\`; über `MAILWARDEN_DATA_DIR` übersteuerbar.

### Vier Stufen (Sollverhalten, Signale v1)
| Stufe | Bedingung |
|---|---|
| `TrustedContact` | Absenderadresse im Adressbuch **und** `Authentication-Results` SPF/DKIM bestanden (sonst Herabstufung, nie Rettung aus Junk) |
| `LikelyLegit` | Auth bestanden, Domain plausibel, kein starkes Spam-Signal, nicht im Adressbuch |
| `LikelySpam` | Summe schwacher Signale über Schwelle 1 |
| `CertainSpam` | Summe über Schwelle 2, z. B. zufällige Domain + Domain fehlt im Body + Auth fehlgeschlagen |

Signale v1: Authentication-Results (SPF/DKIM/DMARC) · Adressbuch-Treffer · Domain-Zufälligkeit (Bigramm-Häufigkeit,
Konsonantenketten, Ziffernanteil) · Absenderdomain kommt im Text/Links nicht vor · Anzeigename ≠ Adresse ·
Reply-To-Domain ≠ From-Domain · Link-Domains ≠ Absenderdomain · manuelle Allow-/Block-Listen (Adresse/Domain).
Später: Naive Bayes aus Dennis’ Entscheidungen, Domain-Alter per RDAP.

## Arbeitspakete

### Phase 1 – Grundlage
| AP | Inhalt | Ergebnis |
|---|---|---|
| AP-00 | **Spike Anmeldung:** MS-App-Registrierung (Kostenprüfung), MSAL-Login + MailKit-IMAP gegen hotmail.de; Google-Cloud-Projekt, OAuth-Login + IMAP gegen Gmail. Konsolenprogramm in `_scratch\`. | Go/No-Go, `docs\auth-setup.md` mit Schritt-für-Schritt-Anleitung |
| AP-01 | Solution-Skelett (inkl. startfähiger WinUI-3-Leer-App als Tooling-Check), Core-Modell (Account, Folder, Message, Contact), SQLite-Schema + Migration, EML-Store, FTS5 | Build grün, Storage-Tests |
| AP-02 | Kontenverwaltung + Zugangsdaten (DPAPI), Token-Cache, Provider-Profile. **AP-02a** ohne UI (Credential-Store, OAuth MS/Google, Provider-Profile, Ordnerrollen, Konto-Service, Verbindungstest); **AP-02b** = Konten-UI mit AP-04c | Konto anlegen/löschen/testen |
| AP-03 | Sync-Engine: Ordnerliste, inkrementeller Sync (UIDVALIDITY, UIDNEXT, Flags), IDLE für Posteingang, Junk-Ordner immer mitsynchronisieren, Befehlswarteschlange (Move/Delete/Flag) mit Wiederholung. **AP-03a** = Transport + Abgleich Server → lokal (Ordner, Mails, Flags, Löschungen, UIDVALIDITY, Schema v2); **AP-03b** = Abgleich robust und schnell (Blöcke, Fortschritt, Wiederverbindung im Lauf, Gmail Important); **AP-03c** = Befehlswarteschlange lokal → Server; **AP-03d** = Hintergrunddienst (Zeitplan, IDLE) | Mails aller Konten lokal, Änderungen beidseitig |
| AP-04 | WinUI-3-Oberfläche: Gruppenansichten Posteingang/Gesendet/Postausgang/Screener/Junk/Quarantäne über alle Konten, dieselben Ansichten je Konto, Ordnerbaum, Lesebereich (WebView2, bereinigt), Light/Dark/System. **AP-04a** = nur lesen (Laufzeit, Ansichten, Liste, Lesebereich, umschaltbare Aufteilung); **AP-04b** = Aktionen über die Warteschlange, Gelesen-Schalter, Suche, Fenstergröße, Link-Tooltip; **AP-04c** = Konten-UI (AP-02b), Anhänge öffnen/speichern; **AP-04d** = Lesebereich folgt dem Thema; **AP-04e** = Nightshade-Thema als Standard | Lesen aller Konten in einer Ansicht |
| AP-05 | Verfassen/Antworten/Weiterleiten, SMTP-Versand je Konto, Anhänge, Signatur je Konto, Gesendet-Ordner | Senden von allen Konten |

### Phase 2 – Der eigentliche Grund
| AP | Inhalt | Ergebnis |
|---|---|---|
| AP-06 | Adressbuch minimal: lokal, Import vCard/CSV, automatisches Sammeln aus gesendeten Mails, Allow-/Block-Listen | Grundlage für Stufe `TrustedContact` |
| AP-07 | Klassifizierer: Signale v1, Schwellen, Entscheidungsprotokoll, Anlegen von `Screener`/`Quarantine` je Konto, Aufbewahrung Quarantäne, Rettung sicherer Absender aus Junk. **AP-07a** = Kern (erledigt); **AP-07b** = Kalibrierung an echten Headern (PstProbe) + Einstellungsdatei; **AP-07c** = Anbindung an Mails/Ordner (nach AP-03c); **AP-07d** = Verdrahtung in die laufende App, Bremse, sichtbare Probleme | Vier Stufen sichtbar, jede Entscheidung erklärbar |
| AP-08 | Regel-Engine + UI: eigene Regeln, „Warum?“-Ansicht je Mail, Rückmeldung „kein Junk“/„Junk“ pflegt Listen, Zuordnung Stufe → Aktion, Schalter „CertainSpam direkt löschen“ | Regeln ersetzen Outlook-Regeln |
| AP-09 | Archivierung + Backup: Archiv-Ordner je Konto synchronisieren, Volltextsuche über alles, Export mbox/EML, Export/Import der Konto- und Regel-Einstellungen (verschlüsselt) | Datensicherung ohne Anbieter |

### Phase 3 – Ergänzungen
| AP | Inhalt |
|---|---|
| AP-10 | Kontakte-Sync: Microsoft Graph (Outlook.com), Google People API; Dublettenabgleich |
| AP-11 | Kalender: Microsoft Graph + Google Calendar API, Serientermine mit Ausnahmen, Zeitzonen |
| AP-12 | Lernender Filter (Naive Bayes), Domain-Alter per RDAP |
| AP-13 | Installer (Inno Setup): Zielordner wählbar, self-contained, Daten unter `<install>\data\`, Update behält Daten |

## Arbeitsweise (festgelegt 2026-10-31)
- AP-00 solo (Hauptagent). Ab AP-01 Drei-Rollen-Pipeline nach `agents.md` mit Git (Entscheidung Dennis 2026-10-31,
  ersetzt „ohne Git“): Builder (fable, höchstens das Modell des Koordinators – Modellregel `agents.md`) auf `feat/ap-NN-<slug>`, Tester (sonnet) auf `test/ap-NN-<slug>`, Reviewer
  (opus) über den Diff gegen `main`. Leseverbote im Auftrag (Tester: kein `src\`, kein Diff; Reviewer: kein `tests\`).
  Anforderung und Notizen je AP in `work\<AP>\`.
- Abschluss eines AP delegiert bei PASS + APPROVE (+ Security ohne Kritisch/Hoch): lokaler Merge-Commit in den
  AP-Branch erlaubt; **Merge nach `main` und Push nur nach CPM-Freigabe durch Dennis** (`git.md`).
- Budget: max. 4 Runden je AP, danach Entscheidung Dennis.
- Repo öffentlich auf GitHub (`Pharenir/MailWarden`), weil Google Homepage- und Datenschutz-URL verlangt.

## Sollverhalten je Arbeitspaket
Wird vor Beginn des jeweiligen AP ausformuliert (Normalfall · Randfälle · Fehlerfall · Positivkontrolle) und hier mit
Spalten *Umgesetzt* / *Getestet* geführt. Vorab festgelegt:

### AP-00 Spike
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Login hotmail.de über MSAL (Browserfenster), danach IMAP-Login per XOAUTH2, Ordnerliste wird ausgegeben | ✔ | ✔ Dennis 2026-10-31, beide Konten, eine Client-ID |
| 2 | Token wird gecacht; zweiter Start ohne Browserfenster | ✔ | ✔ Dennis 2026-10-31 (MS + Google) |
| 3 | Gmail-Login über Google-OAuth (Browserfenster, App-Status Production), danach IMAP per XOAUTH2, Ordnerliste inkl. `[Gmail]/Spam`; Token-Refresh ohne Browser | ✔ | ✔ Dennis 2026-10-31 |
| 4 | Falsches Passwort / abgelehnter Consent → klare Fehlermeldung, kein Absturz, kein Klartext-Secret im Log | ✔ | teilweise: fehlende Config geprüft; Google-403 im Status „Testing“ endete ohne `RESULT:`-Zeile (nicht reproduziert) |
| 5 | **Ergebnis AP-00: Go.** Kosten 0 €, SMTP beide Anbieter ok. Erkenntnisse in `docs\auth-setup.md` → Outlook.com ohne SPECIAL-USE: Ordnerrollen aus LIST-Attributen ableiten | ✔ | – |

### AP-01 Skelett + Storage
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Neue, leere DB wird mit aktuellem Schema angelegt; Schema-Version gespeichert | `SqliteDatabase`, `SchemaMigrations` | `SqliteDatabaseTests` (Fälle 1–4a) |
| 2 | Message speichern/lesen: Header, Flags, Konto, Ordner, UID, EML-Pfad; FTS findet Betreff und Textkörper | `SqliteMessageStore`, `MessageSearchIndex` | `SqliteMessageStoreTests`, `SqliteMessageSearchTests` (9, 17–19) |
| 3 | Gleiche (Ordner, UID) zweimal speichern → Update, kein Duplikat; Konto der Mail muss das Konto des Ordners sein (D3) | Schlüssel `UNIQUE(folder_id, uid)`, FK `(folder_id, account_id)` | `SqliteMessageStoreTests` (10, 11a) |
| 4 | EML-Store: Datei liegt unter `<daten>\mail\accounts\<accountId>\folders\<folderId>\<uid>.eml` (UIDs sind nur je Ordner eindeutig – Abweichung vom ersten Entwurf `yyyy\mm\uid`, Planer 2026-10-31); fehlende Datei → `EmlNotFoundException`, nie leerer Inhalt; Schreiben atomar | `FileEmlStore`, `DataPaths` | `FileEmlStoreTests`, `DataPathsTests` (24–28) |
| 5 | Ältere Schema-Version → Migration läuft; unbekannte höhere Version → `SchemaVersionException` vor jedem Schreibzugriff (fail fast) | `SqliteDatabase` (Migration 0→1; v2 existiert noch nicht) | `SqliteDatabaseTests` (2, 3) – Migration aus echter Altversion erst mit v2 testbar |
| 6 | `ListAsync(MessageQuery)` liefert Mails nach Ordner-Rolle über alle Konten oder gefiltert auf ein Konto, neueste zuerst, mit Limit/Offset – Grundlage der Gruppenansichten | `SqliteMessageStore.ListAsync`, Ausdrucksindizes (D4) | `SqliteMessageStoreTests` (12–16, 15a) |
| 7 | Fremdschlüssel: Mail für unbekannten Ordner → `StorageException`; Konto löschen entfernt Ordner und Mails (Cascade) | Schema v1 + `MessageSearchIndex` | `SqliteAccountStoreTests`, `SqliteFolderStoreTests` (11, 22, 23, D16) |
| 8 | WinUI-3-Leer-App baut mit `dotnet build` ohne Visual Studio und startet unpackaged (Fenster „MailWarden“, folgt dem Windows-Farbschema) | `src\MailWarden.App` (baut) | ✔ Dennis 2026-11-01: startet, Ordner `data\` entsteht, Hell/Dunkel folgt Windows |

Vollständiger Schnittstellenvertrag: `work\AP-01\requirement.md`.

### AP-02a Konten + Anmeldung (ohne UI)
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Secrets DPAPI-verschlüsselt je Schlüssel, atomar, ungültige Schlüssel abgelehnt, kaputte Datei → `CredentialStoreException` ohne Inhalt | `DpapiCredentialStore` | `DpapiCredentialStoreTests` (Fälle 1–7, A1) |
| 2 | Server-Ordner → `FolderRole` nach Flags und Provider-Regeln; Server-`Outbox` nie `FolderRole.Outbox`; Gmail `[Gmail]`/`All Mail` nie im gemeinsamen Posteingang | `FolderRoleResolver`, `MailKitFolderFlags` | `FolderRoleResolverTests`, `MailKitFolderFlagsTests` (8–14, A7) |
| 3 | Provider-Profile MS/Google mit festen Endpunkten; GenericImap → NotSupported | `ProviderProfiles` | `ProviderProfilesTests` (15–16) |
| 4 | Auth-Konfiguration aus Datei + Umgebung, fehlende Werte erst bei Nutzung → `Configuration`-Fehler mit Schlüsselnamen | `AuthConfiguration` | `AuthConfigurationTests` (18, A2) |
| 5 | MSAL-/Google-Fehler → providerneutraler `AuthFailureReason` | `AuthErrorMapping` | `AuthErrorMappingTests` (19–20) |
| 6 | Konto hinzufügen / Token / Re-Auth (falsches Konto → AccessDenied) / Entfernen | `AccountService` | `AccountServiceTests` (21–27, 22a/b, 25a–c, 26a) |
| 7 | Interaktive Anmeldung MS + Google mit echten Konten, Token-Cache, Abbruch → klare Meldung; IMAP + SMTP-Login | `MicrosoftTokenProvider`, `GoogleTokenProvider`, `ConnectionProbe` | manuell Dennis mit AuthProbe (Fälle 28–30) – offen |

Vollständiger Schnittstellenvertrag: `work\AP-02a\requirement.md`.

### AP-03a Abgleich Server → lokal
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Ordnerliste: sichtbare Server-Ordner werden mit Rolle (`FolderRoleResolver`) lokal angelegt/aktualisiert; versteckte (z. B. `[Gmail]/All Mail`) nie; auf dem Server verschwundene Ordner samt Mails und EML-Dateien lokal entfernt | ✅ `Infrastructure\Sync\SyncEngine.cs` (Ordnerliste), `Infrastructure\Sync\MailKitTransport.cs` |✅ Fälle 1–3, 33 – verify.2 S3 658/658 |
| 2 | Erster Abgleich lädt Mails der letzten `InitialSyncDays` (Standard 0 = alle; Entscheidung Dennis 2026-10-31) als EML + Metadaten + Volltext; ältere werden auch später nicht nachgeladen (Ausnahme: später mit altem Datum in den Ordner verschobene Mails ab dem zweiten Lauf – fix.3) | ✅ `Infrastructure\Sync\SyncEngine.cs`, `Infrastructure\Sync\SyncOptions.cs`, `Infrastructure\Mime\MimeBodyText.cs` |✅ Fälle 4–5, 13–15, 25, 34 – verify.2 S3 658/658 |
| 3 | Folgeabgleich lädt jede auf dem Server vorhandene, lokal fehlende UID (oberhalb der Startfenster-Grenze) – auch solche, die der Server beim ersten Mal nicht geliefert hat (fix.3, Befund Fall 28); bereits gespeicherte werden nicht erneut geladen; Flags (gelesen, beantwortet, markiert, gelöscht, Entwurf) werden vom Server übernommen (mit CONDSTORE nur Änderungen); auf dem Server gelöschte Mails verschwinden lokal samt EML | ✅ `Infrastructure\Sync\SyncEngine.cs`, `Infrastructure\Storage\SqliteMessageStore.cs` (UID-Abfragen, `UpdateFlagsAsync`) |✅ Fälle 6–10, 24, 35–38, 40 – verify.2 S3 658/658; fix.3 S3 761/761 |
| 4 | UIDVALIDITY geändert → alle lokalen Mails des Ordners verworfen und neu geladen; nie Mischbestand | ✅ `Infrastructure\Sync\SyncEngine.cs` |✅ Fall 11 – verify.2 S3 658/658 |
| 5 | Abgleich verändert den Server nicht (Ordner nur lesend geöffnet, `\Seen` bleibt unverändert) | ✅ `Core\Sync\IMailTransport.cs` (keine Schreiboperation), `Infrastructure\Sync\MailKitTransport.cs` (EXAMINE, BODY.PEEK[]) |✅ Fall 20 (Reflexion: keine Schreiboperation); EXAMINE/PEEK nur Fall 28 – verify.2 S3 658/658 |
| 6 | Abbruch (Netz, Abbruch-Token) mitten im Ordner → nächster Lauf setzt fort, keine Dubletten, keine halben Datensätze; Fehler in einem Ordner stoppen die anderen nicht (außer Netz/Anmeldung) | ✅ `Infrastructure\Sync\SyncEngine.cs` |✅ Fälle 12, 16–18, 29–32 – verify.2 S3 658/658 |
| 7 | Schema v3 (Abgleichstand je Ordner: Startfenster-Grenze, CONDSTORE-Wert) wird auf eine v1/v2-Datenbank migriert, ohne Daten zu verlieren; v2→v3 setzt die alte Hochwassermarke zurück | ✅ `Infrastructure\Storage\SchemaMigrations.cs`, `SqliteDatabase.cs`, `SqliteFolderSyncStateStore.cs` |✅ Fälle 22–23, 39 – verify.2 S3 658/658; fix.3 S3 761/761 |
| 8 | Echte Konten: Abgleich aller drei Konten mit der SyncProbe, Zahlen plausibel gegen Outlook/Gmail-Web | ✅ SyncProbe + `_scratch\sync-check` (Server-STATUS gegen lokalen Bestand) | ✅ 2026-10-31: nach fix.3 alle Ordner aller drei Konten deckungsgleich (2.317 / 328 / 30), Ungelesen unverändert; vorher fehlten Papierkorb-Mails |

Vollständiger Schnittstellenvertrag: `work\AP-03a\requirement.md`.

### AP-03b Abgleich robust und schnell
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Fehlende Mails werden in Blöcken (Standard 50) mit wenigen IMAP-Kommandos geladen statt zwei Round-Trips je Mail; schlägt ein Block fehl, wird er einzeln nachgeladen | ✅ `Core\Sync\IMailTransport.cs` (`FetchMessagesAsync`), `Infrastructure\Sync\MailKitTransport.cs`, `SyncEngine.cs`, `SyncOptions.cs` (`FetchBatchSize`) | ✅ `SyncBatchTests` (Fälle 1–6) – verify.2 S3 803/803 (Core 360, Infrastructure 443) |
| 2 | Fortschritt je Ordner (Nummer/Anzahl, Rolle, verarbeitet/fehlend) wird während des Laufs gemeldet, ohne Ordnernamen, Betreffs oder Adressen | ✅ `Infrastructure\Sync\SyncProgress.cs`, `SyncEngine.cs` (Überladung `SyncAccountAsync` mit `IProgress<SyncProgress>`) | ✅ `SyncProgressTests` (Fälle 7–8) – verify.2 S3 803/803 |
| 3 | Verbindungsabbruch in einem Ordner: Ordner bekommt Fehler, Abgleich verbindet neu (höchstens 3× je Lauf) und macht mit dem nächsten Ordner weiter; Anmeldefehler und Abbruch beenden den Lauf wie bisher | ✅ `Infrastructure\Sync\SyncEngine.cs`, `SyncOptions.cs` (`MaxReconnectsPerPass`); die Verbindung wird vor jedem Ordner nach dem ersten geprüft (fix.2) | ✅ `SyncReconnectTests` (Fälle 9–13, 19–22) – verify.2 S3 803/803 |
| 4 | Gmail `[Gmail]/Important` wird nicht abgeglichen (versteckt) | ✅ `Core\Accounts\FolderRoleResolver.cs` (Regel 5) | ✅ `FolderRoleResolverTests` (Fall 14) – verify.2 S3 803/803 |
| 5 | Verbindungsaufbau und Ordnerliste: ein gemeinsamer Code für Abgleich und Verbindungstest; Verbindungstest unverändert | ✅ `Infrastructure\Providers\ImapSession.cs`, `MailKitTransportFactory.cs`, `MailKitTransport.cs`, `ConnectionProbe.cs` | ⚠️ Kein Unit-Test; Planer 2026-10-31 mit dem AuthProbe-Befehl `probe` gegen die echten Server: IMAP/SMTP ok, Ordnerzahlen 34/15/15 wie vorher, Important versteckt |
| 6 | Echte Konten: Neuabgleich mindestens 3× schneller als 4 Mails/s, Fortschritt sichtbar, alle Ordner deckungsgleich | ✅ SyncProbe mit Fortschrittsausgabe, `_scratch\sync-check` | ✅ Fall 18, Planer 2026-10-31: 845 Mails in 60 s ≈ 14 Mails/s (vorher ≈ 4/s), Fortschritt sichtbar, 0 abweichende Ordner, Ungelesen unverändert |

Vollständiger Schnittstellenvertrag: `work\AP-03b\requirement.md`.

### AP-03c Befehlswarteschlange lokal → Server
Löschen wie Outlook (Entscheidung Dennis 2026-10-31): Entf → Papierkorb, Umschalt+Entf → sofort endgültig,
„Ordner leeren“ für Papierkorb und Junk → endgültig. AP-03d (Hintergrunddienst: Zeitplan + IDLE) folgt; AP-07c setzt
AP-03c voraus.
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Aktionen (Flags, Verschieben, Papierkorb, endgültig löschen, Ordner leeren) werden als Befehl gespeichert und erst danach am Server ausgeführt; lokal ändert sich erst etwas nach Bestätigung durch den Server | ✅ `Core\Commands\*`, `Storage\SqliteMailCommandQueue.cs`, `Commands\CommandExecutor.cs` | ✅ Fälle 1, 6–11, 19–20, 23; S3 verify.2 922/922 (Core 360, Infrastructure 562) |
| 2 | Ungültige Befehle werden beim Einreihen abgelehnt (fremder/gleicher Ordner, leere UIDs, `\Deleted` als Flag, Ordner leeren außer Papierkorb/Junk) | ✅ `Storage\SqliteMailCommandQueue.cs` | ✅ Fälle 2, 4–5, 26–27, 31; S3 922/922 |
| 3 | „Ordner leeren“ löscht nur die Mails, die beim Auslösen lokal bekannt waren; später eingetroffene bleiben | ✅ `Storage\SqliteMailCommandQueue.cs` (`EnqueueEmptyFolderAsync`) | ✅ Fälle 3, 25; S3 922/922 |
| 4 | Nie ein ungezieltes EXPUNGE/CLOSE: endgültig gelöscht werden nur genau die Mails des Befehls; geänderte UIDVALIDITY → Befehl scheitert ohne Serveraktion | ✅ `Sync\MailKitCommandTransport.cs`, `Commands\CommandExecutor.cs` (Nachweis MailKit-Quelltext: `work\AP-03c\impl-notes.md`) | ✅ Fälle 9–12, 28–30; S3 922/922 |
| 5 | Fehler: Netz → später erneut; vom Server abgelehnt → Wiederholung mit wachsendem Abstand (1, 2, 4 … max. 60 min), nach 5 Versuchen „fehlgeschlagen“; Anmeldefehler bricht ab ohne Versuch zu zählen | ✅ `Commands\CommandExecutor.cs`, `Commands\CommandOptions.cs` | ✅ Fälle 14–18, 21; S3 922/922 |
| 6 | Abgleich bleibt nur lesend (eigene Schnittstelle für Schreibbefehle) | ✅ `Core\Sync\IMailCommandTransport.cs`; `SyncEngine`/`IMailTransport` unverändert | ✅ Fall 22; S3 922/922 |
| 7 | Echte Konten (nur Junk-Mails, Freigabe Dennis): gelesen/ungelesen, Papierkorb, endgültig löschen, Junk leeren – Server und lokaler Bestand stimmen danach überein | ✅ Werkzeug `_scratch\command-probe` (git-ignoriert, im Worktree; Anleitung `work\AP-03c\commandprobe-usage.md`); Lauf durch den Planner 2026-10-31 | Fall 24 (echt, Kopie von `probe-data`, Konto 2, nur geprüfte Junk-Mails): gelesen/ungelesen 10→9→10 ungelesen; Papierkorb 117→118, Junk −1, dort endgültig gelöscht 117 (Outlook.com ändert die Größe beim MOVE um +22 Byte, Abgleich toleriert bis 1 KB); Junk-Löschen 11→10; Junk leeren: 10 → 0, übrige Ordner unverändert, Server gesamt 328→316; `sync-check` nach jedem Schritt 0 abweichende Ordner. Schutz (Konto 1): seen, delete, trash und `delete … trash` auf Posteingangs-UID verweigert, nichts gespeichert. Nicht gegen echten Server geprüft: Umschalt+Entf außerhalb Papierkorb/Junk (MOVE + COPYUID-Expunge) und F3-Rollback (nur Fakes/Code-Lesen) |

Vollständiger Schnittstellenvertrag: `work\AP-03c\requirement.md`.

### AP-03d Hintergrunddienst
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Je Konto: erst offene Befehle ausführen, dann abgleichen; danach alle 15 min erneut (einstellbar) | ✅ `Background\MailboxService.cs`, `Background\MailboxServiceOptions.cs`, `Background\IServiceDelay.cs`, `Background\TimeProviderServiceDelay.cs` | ✅ verify.2 (964/964), Review APPROVE; Fälle 3–6 |
| 2 | Neue Mail im Posteingang löst über IDLE innerhalb von Sekunden einen Abgleich aus; IDLE wird alle 9 min erneuert; ohne IDLE bleibt der Zeitplan | ✅ `Core\Sync\IMailIdleWatcher*.cs`, `Sync\MailKitIdleWatcher*.cs`, Watcher-Schleife in `Background\MailboxService.cs` (MailKit-Nachweis: `work\AP-03d\impl-notes.md`) | ✅ verify.2 (964/964), Review APPROVE; Fälle 11–14, 20, 21 |
| 3 | Fehler: Netz/Server → Wartezeit 1, 2, 4 … 30 min; Anmeldung nötig → Konto wartet auf „erneut versuchen“; ein Konto bringt die anderen nie zum Stehen | ✅ `Background\MailboxService.cs` | ✅ verify.2 (964/964), Review APPROVE; Fälle 7–9, 17 |
| 4 | Status je Konto (läuft, bereit, Fehler, Anmeldung nötig, gestoppt) mit letztem Erfolg, nächstem Lauf, Fortschritt – ohne Fehlertexte, Namen oder Inhalte | ✅ `Background\AccountSyncStatus.cs`, `Background\AccountSyncState.cs`, `Background\MailboxService.cs` | ✅ verify.2 (964/964), Review APPROVE; Fälle 3, 10, 17 |
| 5 | Ein Datenordner wird nur von einem Prozess benutzt (Sperrdatei); zweiter Start meldet „in Benutzung“ | ✅ `Storage\DataFolderLock.cs`, `Background\MailboxService.cs` (`StartAsync`) | ✅ verify.2 (964/964), Review APPROVE; Fälle 1, 2 |
| 6 | Echte Konten: 10 min Dienstlauf ohne Fehler, Testmail löst sofort einen Abgleich aus, sauberes Beenden | ✅ Werkzeug `_scratch\service-probe` (git-ignoriert, im Worktree), Nutzung: `work\AP-03d\serviceprobe-usage.md`; Komposition mit eigener SQLite-Verbindung je Konto und Ablauf, siehe `work\AP-03d\impl-notes.md` O1 | ✅ Fall 19 (Planner, 2026-10-31, 15 min, alle drei Konten): Start ohne Fehler; zweite Instanz „in Benutzung“; Testmail von Dennis → IDLE-Meldung 19:19:53, Abgleich 19:19:55–56 (Posteingang +1); IDLE nach 9 min bei allen Konten erneuert; Beenden in 0,1 s, Sperre freigegeben |

Vollständiger Schnittstellenvertrag: `work\AP-03d\requirement.md`.

### AP-06 Adressbuch
Das Adressbuch füllt sich selbst, damit der Filter entscheiden kann: Kontakte aus den Empfängern gesendeter Mails (nur wenn
`From` eine eigene Kontoadresse ist), Allow-/Block-Listen, Import von vCard und CSV. **Ohne Oberfläche** (Listen und Import
nur über `IContactBook` und `ContactImportFile`, Oberfläche in AP-08). Stand: verify.3c S3 **2406/2406** (Core 536,
Infrastructure 1514, Presentation 356), review.3 APPROVE, Sicherheitsprüfung ohne Kritisch/Hoch. `Act` ist nirgends eingeschaltet.
Fallnummern wie in `work\AP-06\requirement.md`.
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Schema v6: Tabellen `contact_harvest_marker` und `sender_list`; Datenbanken von v1 bis v5 migrieren ohne Datenverlust | ✅ `SchemaMigrations` (v6), `SqliteDatabase.CurrentSchemaVersion` = 6 | ✅ Fall 1 – S3 2406/2406 |
| 2 | Listenspeicher: Adresse oder Domain, eine Art je Eintrag (erlaubt/blockiert), Setzen ersetzt, Entfernen eines unbekannten Eintrags ist ein No-op; ungültige Einträge (Domain ohne Punkt, Art `None`, `*`, Public-Suffix wie `co.uk` oder `com`) → `ArgumentException` | ✅ `ISenderListStore`, `SqliteSenderListStore`, `DomainNames.IsPublicSuffix` | ✅ Fälle 2, 23, 24 – S3 2406/2406 |
| 3 | `AddIfMissingAsync`: ein vorhandener Kontakt bleibt unverändert (Quelle, Name, Schreibweise), auch ein von Hand angelegter; `RemoveAsync` eines unbekannten Kontakts ist ein No-op | ✅ `IContactStore`, `SqliteContactStore` | ✅ Fälle 3, 13 – S3 2406/2406 |
| 4 | Abgleich eines Absenders mit den Listen: exakter Adresseintrag vor Domain-Eintrag; ein Domain-Eintrag trifft die Domain und ihre Subdomains, nie nach oben; unter Domain-Einträgen gewinnt Blocked; Punycode und U-Form gelten als gleich; Blocked prüft tolerant (Leerraum, Anführungszeichen, Endpunkt, ähnlich aussehende Zeichen), Allowed nur bei wohlgeformtem, exakt geschriebenem Absender | ✅ `ContactSenderDirectory`, `AddressRules` (`ToLookupForm`, `NormalizeDomain`, `IsExactSender`) | ✅ Fälle 4, 23, 24, 27, 28 – S3 2406/2406. Verhalten der Internationalisierung (IDN) nur auf der Entwicklungsmaschine beobachtet |
| 5 | Durchlauf mit echtem Speicher: Absender auf der Block-Liste im Posteingang → `CertainSpam`; Allow-Absender mit fehlgeschlagener Prüfung → nicht `TrustedContact` | ✅ `ClassificationPass` (Parameter `ISenderListStore` nach `IContactStore`) | ✅ Fall 5 – S3 2406/2406 |
| 6 | Sammeln: Empfänger (To, Cc, Bcc, Gruppen) der Mails in Ordnern mit Rolle `Sent` und abgeschlossenem Abgleich werden Kontakte (`AutoCollected`, Name bereinigt, höchstens 200 Zeichen); nur Header werden gelesen; eingehende Mails (Posteingang, Junk) nie | ✅ `ContactHarvester`, `ContactNames` | ✅ Fälle 6, 9, 10, 18 – S3 2406/2406 |
| 7 | Inkrementell: je Ordner eine Marke (UIDVALIDITY und höchste gelesene UID), festgeschrieben je Block von höchstens 200 Mails; ohne Marke oder bei anderer UIDVALIDITY wird der ganze Bestand gelesen; nach Abbruch läuft der nächste Lauf weiter, ohne Duplikate | ✅ `IContactHarvestMarkerStore`, `SqliteContactHarvestMarkerStore`, `ContactHarvester` | ✅ Fälle 7, 10, 12 – S3 2406/2406 |
| 8 | Nicht aufgenommen: eigene Adressen aller Konten, fehlerhafte Adressen, Adressen ohne Domain, Rollenadressen (`noreply`, `no-reply`, `donotreply`, `do-not-reply`, `mailer-daemon`, `postmaster`, nur als ganzer lokaler Teil) | ✅ `ContactHarvester`, `AddressRules` | ✅ Fall 8 (mit Positivkontrolle) – S3 2406/2406 |
| 9 | Nur selbst geschriebene Mails: `From` muss genau eine eigene Kontoadresse sein (Alias-Adressen liefern keine Kontakte); übersprungen (`MailsSkipped`) werden Mails mit `Auto-Submitted` außer `no`, `multipart/report`, `X-Autoreply`/`X-Autorespond`, `Precedence` `auto_reply`/`bulk`/`junk`, `text/calendar` mit `method=REPLY` und `Content-Class` mit `calendarmessage` | ✅ `ContactHarvester` (fix.2 R3, fix.3 T5) | ✅ Fälle 22, 31 (mit Positivkontrollen) – S3 2406/2406 |
| 10 | Fehlende oder unlesbare EML: `MailsSkipped`, keine Ausnahme, die Marke rückt weiter; Abbruch wird weitergereicht, Fehler der Speicher ebenfalls | ✅ `ContactHarvester` | ✅ Fall 11 – S3 2406/2406 |
| 11 | Zyklus: Abgleich, Sammeln, Klassifizieren; wirft das Sammeln, wird nicht klassifiziert, eine Logzeile mit dem Typnamen steht im Log, die Ausnahme geht weiter; ein in diesem Zyklus gesammelter Kontakt zählt für die Klassifizierung desselben Zyklus | ✅ `ClassifyingSyncCycle` (Parameter `harvest`), `AccountCycleSteps.Harvest`, `AccountPipelines`, `MailWardenRuntime` | ✅ Fälle 14, 15 – S3 2406/2406. Ungetestet: **Verdrahtung in `AccountPipelines` und `AccountCycleSteps` (kein automatisierter Test), Lauf in der echten App** |
| 12 | Import: vCard (gefaltete Zeilen, mehrere `EMAIL` je Karte, `FN`) und CSV (Kopfzeile Pflicht, `,` oder `;`, Anführungszeichen nach RFC 4180); UTF-8 mit/ohne BOM und UTF-16 mit BOM; höchstens 10 MB und 50.000 Einträge; fehlerhafte Einträge zählen als `Rejected` und stoppen die Datei nie; eine Datei ohne brauchbare Kopfzeile bzw. ohne `BEGIN:VCARD` → `InvalidDataException`; eine nicht abgeschlossene Anführung betrifft nur ihre Zeile | ✅ `ContactImportFile`, `ContactImportReport` | ✅ Fälle 16, 17, 25 – S3 2406/2406. Eine leere vCard-Datei wird abgelehnt (Annahme des Testers, Bestätigung durch den Planer offen) |
| 13 | `IContactBook` als schmale Fassade (`AddContactAsync`, `RemoveContactAsync`, `AllowAsync`, `BlockAsync`, `UnlistAsync`); Adresse wird getrimmt und geprüft, sonst `ArgumentException`; Name bereinigt | ✅ `IContactBook`, `ContactBook` (fix.2 R7, fix.3 T4) | ✅ Fälle 26, 30 – S3 2406/2406 |
| 14 | Allow-Liste und Prüfung: `Authenticated` → `TrustedContact`; `Unknown` (auch `dmarc=none`, `softfail`, `compauth` bei DMARC-Fail) → höchstens `LikelyLegit`; `Failed` wie bisher nach Punkten; Block-Liste schlägt alles | ✅ `SpamClassifier` (fix.2 R1, siehe AP-07 #6) | ✅ Fall 20 – S3 2406/2406 |
| 15 | Ausrichtung: DKIM (`header.d`), SPF (`smtp.mailfrom`) und DMARC (`header.from`) bestätigen nur, wenn ihre Domain mit der `From`-Domain gleich ist oder in echter Eltern-/Kind-Beziehung steht (kein Geschwister; ein Public Suffix ist kein Elternteil); ein DMARC-Pass für eine fremde Domain zählt nicht, DKIM/SPF können dann noch bestätigen | ✅ `AuthStatusEvaluator`, `DomainNames.AreSameOrParentAndChild` (fix.2 R2, fix.3 T3) | ✅ Fälle 21, 29 – S3 2406/2406 |
| 16 | Echte Daten (Planer, Kopie, nur Zählungen): Sammeln aus dem Bestand, danach `stock` des `classification-probe`; Zahlen stimmig zur Simulation (418), keine Namen ausgegeben | ✅ Prüfprogramm `_scratch\contact-probe` (nicht im Repo) | ✅ Fall 19: zuerst 1.000 Mails gelesen in etwa 2 s, 417 Kontakte (370 mit Name), keine übersprungen, kein Befehl. Nach fix.2 R3: 966 gelesen, 34 übersprungen, 410 Kontakte (die 7 Kontakte weniger sind der Preis von R3). Nach T5 nicht erneut gezählt |

Nicht von Tests gedeckt: Fall 19 (manuell, Planer), die Verdrahtung des Sammelns in `AccountPipelines`/`AccountCycleSteps`, das
Prüfprogramm, jeder Lauf in der echten App und gegen einen echten Server. Offene Punkte und Bedingungen vor `Act`: `TODO.md`,
„Vertagt aus AP-06“. Vollständiger Schnittstellenvertrag: `work\AP-06\requirement.md`.

### AP-04b Oberfläche, Aktionen
Aktionen laufen ausschließlich über die Befehlswarteschlange (AP-03c). Stand: S3 2655/2655 (verify.6), Core 536, Presentation 532, Infrastructure 1587; review.5 APPROVE; secure.4 0 Kritisch / 0 Hoch / 1 Mittel (durch Y1 behoben; Niedrig und Hinweise in `TODO.md`); „Ordner leeren“ gegen Rettung aus Junk entscheidet Dennis, `TODO.md`. Alle Fälle nur mit Fakes bzw. künstlichen Daten und der echten Warteschlange auf SQLite, kein echter Server. **Die Oberfläche selbst (Fenster, XAML, WebView2-Control) wurde nie ausgeführt**: was nur der manuelle Test belegt, hat Dennis am 2026-11-01 bestanden (Fälle 21 mit Nachtests, 22, 32, X-h, Y-c; dazu der Nachweis Y5: Tooltip zeigt das echte Ziel bei ausgeschalteter Statusleiste). Merge nach `main` am 2026-11-01. AP-04c (Konten-UI, Anhänge) folgt.
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Aktionen (gelesen/ungelesen, Kennzeichen, Papierkorb, endgültig löschen, verschieben) je Ordner als Befehl, höchstens 1.000 UIDs je Befehl, UIDVALIDITY des Ordners, danach Trigger je Konto; Ablehnung der Warteschlange als `MailActionException` mit festem Text, bei teilweisem Speichern eigener Text | ✅ `Infrastructure\Composition\IMailboxActions.cs`, `MailboxActions.cs` | ✅ Fälle 1–3, 5, 6, 27 – S3 2348/2348 (verify.4) |
| 2 | Auswahl mehrerer Zeilen; Aktionen wirken auf die Auswahl zum Zeitpunkt des Tastendrucks; zweites Del vor dem Ausblenden trifft nicht die nächste Zeile; Lesebereich nur bei genau einer Zeile | ✅ `Presentation\ShellViewModel.cs`, `MessageListViewModel.cs` | ✅ Fälle 16, 23, 24 – S3 2348/2348 (verify.4) |
| 3 | Gelesen/Ungelesen/Kennzeichnen: Zustand sofort sichtbar, Wahrheit nach dem Neuladen | ✅ `ShellViewModel.cs`, `PendingMailState.cs` | ✅ Fall 12 – S3 2348/2348 (verify.4) |
| 4 | Del → Papierkorb, in Papierkorb/Junk mit Rückfrage endgültig; Shift+Del fragt immer (nur Anzahl, nie Absender oder Betreff); Zeilen verschwinden sofort, nächste Zeile wird gewählt, bei Fehlschlag oder Verwerfen kommen sie zurück; Hinweis, wenn Shift+Del nur den Papierkorb erreichte | ✅ `ShellViewModel.cs`, `PermanentDeleteWatcher.cs`, `UiText.cs` | ✅ Fälle 7–11, 28 – S3 2348/2348 (verify.4) |
| 5 | Verschieben nur im selben Konto; Ziel eines anderen Kontos und Auswahl über mehrere Konten werden vor der Warteschlange abgewiesen | ✅ `ShellViewModel.cs`, `MailboxActions.cs` | ✅ Fälle 3, 15 – S3 2348/2348 (verify.4) |
| 6 | „Ordner leeren“ nur Papierkorb und Junk; Rückfrage mit Anzahl und Kontoname; gelöscht wird nur, was bestätigt wurde (UIDVALIDITY und UID-Menge, Prüfung und Eintrag in einer Transaktion); neue Mail oder geänderter Ordner → verweigert, neue Rückfrage; ausstehendes Verschieben aus dem Ordner → verweigert | ✅ `MailboxActions.cs`, `Infrastructure\Storage\SqliteMailCommandQueue.cs`, `EmptyFolderChangedException` (Core) | ✅ Fälle 4, 25, 26, 30; W-a bis W-g – S3 2348/2348 (verify.4) |
| 7 | Fehlgeschlagene Aktionen: Liste mit festem Text, „Verwerfen“, Hinweis in der Infoleiste | ✅ `ShellViewModel.cs` (`FailedActions`), `MainWindow.xaml` | ✅ Fall 8 – S3 2348/2348 (verify.4) |
| 8 | Suche lokal (FTS, höchstens 500 Treffer), leerer Text zurück zur Ansicht, nur das neueste Ergebnis zählt, Suchtext in keinem Log | ✅ `Presentation\SearchViewModel.cs`, `IMailboxReadModel.SearchAsync` | ✅ Fall 17 – S3 2348/2348 (verify.4) |
| 9 | Einstellungen `markReadOnSelect` (Standard aus) und Fensterwerte; fehlende Schlüssel = Standard, Bereichsprüfung; Position außerhalb aller Monitore wird ignoriert | ✅ `Presentation\UiSettings.cs`, `UiSettingsFile.cs`, `WindowPlacement.cs`; `App\WindowPlacementKeeper.cs` | ✅ Fall 18 (Datei, Bereich, Hilfsfunktion) – S3 2348/2348 (verify.4). Ungetestet: **Größe und Position nach Neustart, maximiert, anderer Monitor – manuell Dennis – offen** (Fall 22) |
| 10 | Gelesen beim Anzeigen: nur mit Schalter, nach mindestens 1,5 s; Schalter aus → nie ein Befehl | ✅ `ShellViewModel.cs` (`MarkReadOnSelect`, `TimeProvider`) | ✅ Fälle 13, 14 – S3 2348/2348 (verify.4) |
| 11 | Linkziel als Tooltip an der Maus (Fremdtext bereinigt, höchstens 501 Zeichen, bei langen http/https-Zielen zuerst der Host); die Statusleiste des Lesebereichs ist aus (X4), der Tooltip ist die einzige Anzeige des Linkziels | ✅ `Presentation\ReadingPaneViewModel.cs` (`HoverLinkText`), `App\MailWebViewHost.cs` | ✅ Fall 19, 29 (Text) – S3 2655/2655 (verify.6). Fall 32 sah Dennis mit eingeschalteter Statusleiste. ✅ Tooltip im WebView2-Control bei ausgeschalteter Statusleiste, wahres Ziel bei abweichendem Linktext: manuell Dennis 2026-11-01 (Y5, Y-c) |
| 12 | Nach `Dispose` wird nichts mehr gespeichert, auch nicht während eines `await`; Tastenwiederholung (Del halten) wirkt einmal | ✅ `MailboxActions.cs`, `ShellViewModel.cs`, `App\MainWindow.xaml.cs` | ✅ Fälle 20, 31 – S3 2348/2348 (verify.4). Tastenwiederholung und Fokus nach dem Entfernen: **manuell Dennis – offen** (Fall 22) |
| 13 | Manuell (Dennis): Kopie der Daten, Testmails nur zwischen Konto 2 und Konto 3: gelesen, Kennzeichen, Löschen, endgültig, Verschieben, Junk leeren; Webmail stimmt überein | ✅ gebaut | **✅ manuell Dennis 2026-11-01** (Fall 21, `work\manual-tests.md`) |
| 14 | Manuell (Dennis): Fensterposition und -größe nach Neustart, auch maximiert; Tooltip an der Maus; Tasten | ✅ gebaut | **✅ manuell Dennis 2026-11-01** (Fall 22) |
| 15 | Manuell (Dennis): Link, dessen Text vom Ziel abweicht, zeigt das wahre Ziel im Tooltip (Statusleiste aus, Y5); Fenster breit und schmal, Hinweis ganz lesbar (Y2) | ✅ gebaut | Fall 32 mit eingeschalteter Statusleiste von Dennis gesehen; mit ausgeschalteter Statusleiste **✅ manuell Dennis 2026-11-01** (Y5, Y-c) |
| 16 | X1: Rechtsklick (und Kontexttaste) auf einen Ordner im Baum bietet „Ordner leeren“ genau dann an, wenn der Eintrag leerbar ist (genau ein Papierkorb- oder Junk-Ordner hinter der Ansicht); sonst kein Menü | ✅ `Presentation\MailView.cs`, `App\MainWindow.xaml.cs` | ✅ X-a bis X-d – S3 2655/2655 (verify.6). Ungetestet: **Menü im Baum, Kontexttaste – manuell Dennis – offen** (X-h) |
| 17 | X2: wirkt auf den angeklickten Ordner, nicht auf die gewählte Ansicht; Auswahl und Liste bleiben; gleicher Pfad `EmptyFolderAsync(view)` mit Rückfrage, bestätigter UID-Menge und Verweigerung; aktive Suche blockiert nicht; Ordner ohne lokale Mails: keine Rückfrage, kein Befehl | ✅ `Presentation\ShellViewModel.cs` | ✅ X-e bis X-g – S3 2655/2655 (verify.6) |
| 18 | X3: die Zahl ungelesener Mails im Baum sinkt, sobald die Mails die Ansicht verlassen (Löschen, endgültig löschen, Verschieben, Leeren); bei Fehlschlag kommt sie mit den Zeilen zurück; beim Leeren eines Ordners, dessen Mails nicht in der Liste stehen, sinkt sie, sobald der Befehl gespeichert ist; Gelesen/Ungelesen ausgenommen; nichts wird doppelt abgezogen | ✅ `Presentation\NavigationViewModel.cs`, `ShellViewModel.cs`, `Infrastructure\Storage\SqliteMessageStore.cs` | ✅ X-i bis X-m – S3 2655/2655 (verify.6) |
| 19 | X4: Statusleiste des Lesebereichs aus (`IsStatusBarEnabled = false`); Tooltip bleibt die einzige Anzeige des Linkziels | ✅ `App\MailWebViewHost.cs` | **✅ manuell Dennis 2026-11-01** (Y5, Y-c); die App ist nicht ausgeführt |
| 20 | Y1: `title` gehört nicht mehr zu den erlaubten Attributen der Bereinigung (secure.4 M1); `href` bleibt | ✅ `Infrastructure\Mime\MailHtmlSanitizer.cs` | ✅ Y-a – S3 2655/2655 (verify.6) |
| 21 | Y2: der Linkhinweis passt in ein schmales Fenster (`MaxWidth` = kleinerer Wert aus 600 und Fensterbreite minus beide Randabstände) | ✅ `App\MainWindow.xaml.cs` | **✅ manuell Dennis 2026-11-01** (Y-c); reine Oberfläche |
| 22 | Y3: in der Rollenansicht eines Kontos filtert die Zählung nach `f.account_id` statt `m.account_id`; Zahlen unverändert, Neuzählung (X3) kommt rechtzeitig | ✅ `Infrastructure\Storage\SqliteMessageStore.cs` | ✅ Y-b (echter SQLite-Treiber) – S3 2655/2655 (verify.6) |

Nicht von Tests gedeckt, von Dennis am 2026-11-01 bestanden: Fälle 21, 22, 32, Rechtsklick im Baum, Tooltip ohne Statusleiste; ohne automatischen Test bleiben alle Fenster- und XAML-Teile und jeder Lauf gegen einen echten Server. Nicht öffentlich erreichbar und daher ohne Test: `MailArrivedText` nur als fester Text geprüft, `TryGetPartlyDone`, `MaxEmptyConfirmations`. Vollständiger Schnittstellenvertrag: `work\AP-04b\requirement.md`.

### AP-04a Oberfläche, nur lesen
Parallel zu AP-07c (Dennis 2026-11-01). HTML-Bereinigung mit HtmlSanitizer (Freigabe Dennis 2026-11-01). AP-04b folgt:
Konten-UI (AP-02b), Aktionen, Suche, Anhänge.

Stand: fix.6 aus dem ersten Test von Dennis: verify.6 S3 **1732/1732** (Core 430, Infrastructure 958, Presentation 344), review.6 APPROVE. Dennis 2026-11-01: App startet, Links und `mailto:` öffnen das Standardprogramm, Abgleich bringt neue Mails, kein Gelesen-Befund im Webmail. Zweiter Durchgang: Linkziel sichtbar, neue Mail erscheint schnell, zweite Instanz mit Hinweis blockiert, fix.6 passt (Zahlen, Kontonamen, Bildsperre in Junk). Davor: verify.5 S3 1700/1700, review.5 APPROVE; die Sicherheitsbefunde
aus secure.4 sind in fix.5 behoben, eine weitere Sicherheitsprüfung gibt es nach Entscheidung Dennis nicht. Fälle 1–41 und
die Zusatzfälle 49–95 sind automatisiert. **Die Oberfläche selbst (Fenster, XAML, WebView2-Control) wurde nie ausgeführt**:
was nur der manuelle Test belegt, steht als „manuell Dennis – offen“ (Fälle 42–48, `work\manual-tests.md` Teil 6).
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | App startet Laufzeit und Hintergrunddienst; Sperre vor dem Öffnen der DB; Fehler beim Start (ungültiger Datenordner, „in Benutzung“) zeigen ein Fenster statt stillem Ende | ✅ `Infrastructure\Composition\MailWardenRuntime.cs` (Sperre vor `InitializeAsync`), `AccountPipelines.cs`, `RuntimeConnections.cs` (eine Verbindung je Ablauf), `MailboxService` mit `heldLock`, `App\App.xaml.cs` (Fehlerfenster, Beenden schließt die Laufzeit) | ✅ Fälle 24–28 (Sperre, Öffnen/Schließen, `heldLock`, parallele Schreiber, kritisches Log). Ungetestet: **Fenster bei Startfehler, Start und Beenden mit echten Daten – manuell Dennis – offen** (Fälle 42, 47) |
| 2 | Gruppenansichten Posteingang/Gesendet/Postausgang/Screener/Junk/Quarantäne über alle Konten, dieselben je Konto, übrige Ordner als Baum, Ungelesen-Zähler | ✅ `Presentation\MailView.cs`, `FolderTree.cs`, `NavigationViewModel.cs`, `IMessageStore.CountUnreadAsync`; `App\MainWindow.xaml` | ✅ Fälle 20–22, 29, 30. Ungetestet: **Baum, Vorauswahl „Posteingang“ und Zähler im echten Fenster – manuell Dennis – offen** (Fall 42) |
| 3 | Mail-Liste neueste zuerst, seitenweise, Konto in jeder Zeile; neue Mails erscheinen nach dem Abgleich ohne Benutzeraktion | ✅ `Presentation\MessageListViewModel.cs`, `ShellViewModel.cs` (Statusbeobachtung, Aktualisieren) | ✅ Fälle 31–34, 38, 39. Ungetestet: **Nachladen beim Scrollen und neue Mail im laufenden Betrieb – manuell Dennis – offen** (Fall 46) |
| 4 | Liste und Lesebereich umschaltbar nebeneinander/untereinander, Navigation immer links; Wahl und Größen bleiben erhalten | ✅ `Presentation\UiSettings.cs`, `UiSettingsFile.cs` (`ui-settings.json`), `App\PaneSplitter.cs`, `MainWindow.xaml` | ✅ Fälle 23, 40 (Datei, Speichern, Fehler). Ungetestet: **Umschalten, Splitter, Größen nach Neustart – manuell Dennis – offen** (Fall 43) |
| 5 | Lesebereich: HTML bereinigt, kein Skript, externe Inhalte blockiert bis „Bilder laden“ je Mail; Links öffnen nur im Standardbrowser | ✅ `Infrastructure\Mime\MailContentReader.cs`, `MailHtmlSanitizer.cs`, `HtmlNestingLimiter.cs`, `ReadingDocumentBuilder.cs`; `Presentation\WebContentPolicy.cs`, `ReadingPaneViewModel.cs` (Hinweis `ContentNotice`); `App\MailWebViewHost.cs`. Schutz gegen präparierte Mails (Grenzen, Parser-Budget, Aufbau im eigenen Thread), Restpunkte in `TODO.md` | ✅ Fälle 2–19, 36, 37 und Zusatzfälle 49–95. Ungetestet: **WebView2-Verdrahtung im WinUI-Control (nur per Probe mit dem WinForms-Control belegt), „Bilder laden“, Klick auf Links, Hinweis bei gekürzten Mails, `mailto:` – manuell Dennis – offen** (Fall 45) |
| 6 | Fremdtexte ohne Steuer-/Bidi-Zeichen; Logs enthalten nur Typnamen und feste Texte | ✅ `Core\Text\SafeDisplayText.cs`, `Presentation\MailDisplayText.cs`, `Infrastructure\Logging\CriticalFileLog.cs` | ✅ Fälle 1, 7, 22, 28, 36, 38 (Markertext erscheint nie in Anzeige oder Log) |
| 7 | Keine Änderung am Server durch die Oberfläche (auch Lesen setzt kein `\Seen`) | ✅ `IMailboxReadModel` (nur Lesemember), `IMailboxController` (nur Trigger/Retry) | ✅ Fall 41 (Reflexion, aufgezeichnete Aufrufe, Lesen setzt kein `\Seen`). Ungetestet: **Webmail nach dem Lesen unverändert – manuell Dennis – offen** (Fall 48) |
| 8 | Hell/Dunkel/System umschaltbar; der Lesebereich folgt dem Design, das die App tatsächlich zeigt: Mail ohne eigene Farben im dunklen Design dunkel, Mail mit eigenen Farben oder Bildern bleibt helles Blatt unter einem Abdunkelungsschleier (25 % schwarz), Hell unverändert (AP-04d, Tabelle dort) | ✅ `Presentation\UiSettings.cs` (`AppTheme`), `MainWindow.xaml` (`RequestedTheme`), `MainWindow.xaml.cs` (`ActualTheme`), `ReadingPaneViewModel.ApplyThemeAsync`, `ReadingDocumentBuilder` (Seitenstil je Design), `OwnColorDetector` (eigene Farben) | ✅ Fall 40 (Wechsel, Speichern); AP-04d: siehe dessen Tabelle. Ungetestet: **Aussehen beider Designs – manuell Dennis; Wechsel Hell/Dunkel bestätigt 2026-11-01, Schleier (AP-04d Fall 21) offen** (Fall 44) |

Vollständiger Schnittstellenvertrag: `work\AP-04a\requirement.md`.

### AP-04e Nightshade-Thema
Wunsch Dennis 2026-11-01: viertes Thema, als Standard. Spike und vollständiger Vertrag: `work\AP-04e\requirement.md`. Die Oberfläche selbst läuft in keinem Test; was nur der Handtest belegt, steht dann als „manuell Dennis“.
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| E1 | `AppTheme.Nightshade`, gespeichert als `nightshade`; unbekannte Werte werden weiter abgelehnt | ✅ `Presentation\UiSettings.cs`, `Presentation\UiSettingsFile.cs` |  |
| E2 | Standard ist Nightshade; ein gespeichertes Thema bleibt | ✅ `Presentation\UiSettings.cs` (`Default`); `UiSettingsFile` beginnt beim Laden mit `Default` |  |
| E3 | Auswahl in der Reihenfolge Nightshade, System, Hell, Dunkel (`AppThemes.ChoiceOrder`) | ✅ `Presentation\AppThemes.cs`, `Presentation\UiText.cs`, `App\MainWindow.xaml.cs` |  |
| E4 | `ReadingTheme.Nightshade`; `AppThemes.ToReadingTheme` liefert es unabhängig vom Fenster | ✅ `Infrastructure\Mime\ReadingTheme.cs`, `Presentation\AppThemes.cs` |  |
| E5 | Mail ohne eigene Farben und Klartext: Seite `#231E33`, Text `#ECE8F7`, Links `#FF8A3D`, Auswahl `#4A3B7A`, kein Schleier | ✅ `Infrastructure\Mime\ReadingDocumentBuilder.cs` (`NightshadeStyle`) |  |
| E6 | Mail mit eigenen Farben: helles Blatt unter dem Schleier, Stil wie im Dunkelmodus | ✅ `Infrastructure\Mime\ReadingDocumentBuilder.cs` (`SelectPageStyle`, dieselbe Konstante `DimmedLightStyle`) |  |
| E7 | `PageBackground(Nightshade)` = `#231E33`, kein Aufblitzen | ✅ `Infrastructure\Mime\ReadingDocumentBuilder.cs` |  |
| E8 | Hell und Dunkel bleiben byte-gleich | ✅ `Infrastructure\Mime\ReadingDocumentBuilder.cs` (Stile textgleich; nur die Regel für `mark` als gemeinsame Konstante) |  |
| E9 | Bereinigung und CSP unverändert, Stil nur aus eigenen Konstanten | ✅ `Infrastructure\Mime\ReadingDocumentBuilder.cs` (Bereinigung und CSP nicht angefasst, Auswahl nur aus Thema und Ja/Nein) |  |
| E10 | Wechsel bei offener Mail ohne erneutes Lesen, auch Dunkel ↔ Nightshade | ✅ `App\MainWindow.xaml.cs` (`ApplyReadingTheme` über `AppThemes.ToReadingTheme`); `ReadingPaneViewModel` unverändert |  |
| E11 | `Themes\NightshadeTheme.xaml`: nur Wörterbuch `Dark`, keine doppelten Schlüssel, jeder Alias vorher definiert | ✅ `App\Themes\NightshadeTheme.xaml`, erzeugt von `tools\make_nightshade_theme.py`; dazu der eigene Schlüssel `NightshadeWindowBackgroundBrush` |  |
| E12 | Fenster zeigt die Palette: Steuerelemente, Flyouts, Menüs, Dialoge, Titelleiste, kein Mica | ✅ `App\Themes\NightshadeTheme.xaml`, `App\WindowThemeChrome.cs` (nicht gestartet, Handtest offen) |  |
| E13 | Wechsel live in jede Richtung ohne sichtbares Aufblitzen; Verlassen stellt Mica und Titelleiste wieder her | ✅ `App\WindowThemeChrome.cs` (nicht gestartet, Handtest offen) |  |
| E14 | Start mit Nightshade ohne Aufblitzen eines anderen Themas | ✅ `App\MainWindow.xaml.cs` (Palette vor `InitializeComponent`), `App\WindowThemeChrome.cs` (nicht gestartet, Handtest offen) |  |
| E15 | Windows-Hochkontrast: Palette, Fensterfläche und Titelleiste werden nicht angewendet | ✅ `App\WindowThemeChrome.cs` (`AccessibilitySettings`, auch beim Umschalten zur Laufzeit; nicht gestartet, Handtest offen) |  |
| E16 | `Core`/`Infrastructure` ohne WinUI, `AppThemes` in `Presentation`, Code-Behind verdrahtet nur | ✅ `Presentation\AppThemes.cs`, `App\WindowThemeChrome.cs`, `App\MainWindow.xaml.cs` |  |

### AP-04d Lesebereich folgt dem Thema
Wunsch Dennis 2026-11-01. Die Regel „Mail-Inhalt nie dunkel“ in `ReadingDocumentBuilder` entfällt; geblieben ist der Grund dahinter: eine Mail muss lesbar bleiben. Stand: verify.4 S3 2523/2523 (Core 536, Infrastructure 1604, Presentation 383) vor dem Merge mit `main`; verify.5 S3 auf dem zusammengeführten Stand 2772/2772 (Core 536, Presentation 559, Infrastructure 1677), secure.2 ohne Befund über Hinweise hinaus. review.1 CHANGES, review.2 APPROVE; secure.1 0 Kritisch / 0 Hoch / 0 Mittel / 1 Niedrig (L1, durch F1 behoben) / 2 Hinweise. Alle Fälle nur mit künstlichen Daten. **Von Dennis am 2026-11-01 bestanden: Fälle 13 und 21 (Schleier, Links und Auswahl hindurch, Hell, Design System im laufenden Betrieb, kein störendes Aufblitzen); damit sind die manuellen ⚠️ unten erledigt.** **Die Oberfläche selbst (Fenster, WebView2-Control) läuft in keinem Test**: was nur der manuelle Test belegt, steht als „manuell Dennis“. Dennis 2026-11-01: Klartext-Mails folgen dem Thema, Umschalten Hell/Dunkel funktioniert; Fall 21 (abgedunkeltes Blatt, Links und Auswahl durch den Schleier) ist offen. Vollständiger Vertrag: `work\AP-04d\requirement.md`.
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| D1 | Der Lesebereich folgt dem Thema, das die App tatsächlich zeigt (auch bei „Design System“); Wechsel bei laufender App ohne neuen Klick und ohne erneutes Lesen vom Store oder Server | ✅ `App\MainWindow.xaml.cs` (`ActualTheme`), `Presentation\ReadingPaneViewModel.ApplyThemeAsync` | ✅ Fälle 10, 11, 20 (kein Lesen, Auswahl und Bildfreigabe bleiben; ohne offene Mail passiert nichts). Manuell Dennis: Wechsel Hell/Dunkel bestätigt 2026-11-01; „Design System“ bei laufender App nicht gemeldet |
| D2 | Mail ohne eigene Farben im dunklen Thema: dunkle Seite, helle Schrift, lesbare Links, dunkle Scrollleisten (Klartext und bereinigtes HTML ohne Farbangaben) | ✅ `Infrastructure\Mime\ReadingDocumentBuilder.cs` (dunkler Seitenstil), `ReadingTheme.cs` | ✅ Fälle 1, 3, 5a, 6, 18 (verify.4). Manuell Dennis: Klartext folgt dem Thema, bestätigt 2026-11-01 |
| D3 | Mail mit eigenen Farben bleibt im dunklen Thema ein helles Blatt mit ihren Farben; seit G1 unter dem Schleier | ✅ `ReadingDocumentBuilder.cs` (heller Seitenstil = früherer `BaseStyle`) | ✅ Fälle 4 (neu), 17 |
| D4 | „Eigene Farben“ wird am bereinigten HTML entschieden und im Zweifel hell: `color`, `background*` in `style`, Attribute `color`/`bgcolor`, `<img>`; unklar zählt als eigene Farben; Prüfung vor den URL-Regeln (blockiertes `background-image` und entferntes `<img>` zählen); `<style>` und `<body>`-Attribute erreichen das bereinigte HTML nie und zählen daher nicht | ✅ `Infrastructure\Mime\OwnColorDetector.cs`, `MailHtmlSanitizer.cs` (`HasOwnColors`) | ✅ Fälle 5, 5a, 6, 7. Nicht behauptet: verwirft der Filter Stildaten wegen eines Limits (300 Attribute an einem Tag, 100 000 Deklarationen in einem `style`), ist `HasOwnColors` für den Rest `false` (Beobachtung 2 im test-result) |
| D5 | Hell sieht für jede Mail aus wie bisher, Byte für Byte | ✅ `ReadingDocumentBuilder.cs` (Standardthema `Light`) | ✅ Fälle 2, 8, 19 |
| D6 | Kein weißes Aufblitzen im dunklen Thema: WebView2-Standardhintergrund und Seitenhintergrund stammen aus einer Quelle | ✅ `ReadingDocumentBuilder.PageBackground`, `App\MailWebViewHost.SetDefaultBackground` | ⚠️ Das Aussehen ist nicht automatisiert prüfbar. **Manuell Dennis – offen** (Fall 13; review.1 S2: altes weißes Blatt beim Wechsel zu einer Klartext-Mail beobachten, nur ändern, wenn Dennis es sieht) |
| D7 | Bereinigung und Content-Security-Policy unverändert; das Thema wählt nur einen festen Stil aus eigenen Konstanten, aus der Mail kommt nur das Ja/Nein von D4; Bildfreigabe und Lesebereichszustand überstehen den Wechsel | ✅ `MailHtmlSanitizer.cs` (kein Themaparameter), `ReadingPaneViewModel.cs` | ✅ Fälle 9, 12, `The_mail_cannot_influence_the_veil`. secure.1: D7 gilt, 16 000 Fuzz-Ausgaben byte-gleich |
| D8 | `Core` und `Infrastructure` ohne WinUI; das Thema kommt als eigenes Enum (`ReadingTheme`) in den Builder, `MainWindow` meldet das tatsächliche Thema an das ViewModel, Code-Behind verdrahtet nur | ✅ `Infrastructure\Mime\ReadingTheme.cs`, `Presentation\ShellViewModel.cs`, `App\MainWindow.xaml.cs` | ✅ Builder und ViewModel über `ReadingPaneThemeTests`. Das Verdrahten in `MainWindow` ist nicht automatisiert prüfbar (manuell, siehe D1) |
| F1 | Auch Deklarationen, die Text umfärben, zählen als eigene Farben: `all`, `filter`, `backdrop-filter`, `mix-blend-mode`, `box-shadow`, `border-image*`; `content` und `list-style-image` nur mit einem Wert außer `none`, `normal`, `initial`; `border`, `outline`, `text-shadow`, `list-style:none` zählen nicht | ✅ `OwnColorDetector.cs` (benannte Menge) | ✅ Fälle 14, 14a, 15 und die Werte-Fälle aus verify.3. Angenommen: `box-shadow:none` bleibt hell |
| F3 | Ein Fehler beim Setzen des WebView2-Hintergrunds beendet den Prozess nicht (wie zuvor) | ✅ `App\MailWebViewHost.cs` | ❌ Kein Test (WinUI-Control), nicht automatisiert prüfbar |
| F4 | Scheitert der Neuaufbau beim Themawechsel, bleibt das lesbare Dokument stehen (Zustand `Ready`); das kritische Log nennt nur den Typnamen | ✅ `ReadingPaneViewModel.cs` | ✅ Fall 16 |
| F5 | Links in `<mark>` haben im dunklen Stil eine lesbare Farbe; Kommentare an den Fail-safe-Zweigen und am Build-Limit stimmen | ✅ `ReadingDocumentBuilder.cs`, `MailHtmlSanitizer.cs` | ✅ Regel `mark a` nur im dunklen Stil (verify.2); die Kommentare prüft kein Test |
| G1 | Im dunklen Thema liegt über dem hellen Blatt einer Mail mit eigenen Farben ein fester Schleier (25 % schwarz, Weiß wird etwa `#bfbfbf`); keine Farbe wird umgeschrieben; Hell bleibt byte-gleich; Klartext und Mail ohne Farben bekommen keinen Schleier | ✅ `ReadingDocumentBuilder.cs` (Regel `html::after`) | ✅ Fälle 4 (neu), 17, 18, 19. Manuell Dennis: **Fall 21 offen** |
| G2 | Der Schleier nimmt keine Klicks und keine Auswahl an (`pointer-events:none`); Links, Linkhinweis und Textauswahl gehen hindurch; D7 gilt weiter | ✅ `ReadingDocumentBuilder.cs` | ⚠️ Nur das Merkmal `pointer-events:none` im Seitenstil. Klick, Hinweis und Auswahl im echten Control: **manuell Dennis – offen** (Fall 21) |
| G3 | Eine Mail mit eigenen Farben bekommt beim Themawechsel ein anderes Dokument und wird neu geladen; eine Mail mit freigegebenen Fernbildern darf diese erneut abrufen (nur freigegebene URLs) | ✅ `ReadingPaneViewModel.cs`, `MailWebViewHost.cs` | ✅ Fall 20 (`DocumentHtml` ändert sich, kein Lesen, Bildfreigabe bleibt). Das erneute Abrufen im echten Control ist nicht beobachtet |

Bekannte Grenze (F2, kein Code): entfernt die Bereinigung den dunklen Hintergrund einer Mail und bleibt ihre helle Schrift, steht heller Text auf dem hellen Blatt; `main` verhält sich genauso. Nach einem Themawechsel kann die Bildlaufposition einer Mail ohne eigene Farben nach oben springen (angenommen). F6 (altes weißes Blatt beim Wechsel zu Klartext) wird nur auf Wunsch geändert.

### AP-07d Verdrahtung in die App
Der Durchlauf von AP-07c läuft nach jedem Abgleich in der laufenden App. Standard bleibt nur protokollieren; `Act` ruht
weiter, solange das Adressbuch leer ist (→ AP-06). Testlauf: S3 2137/2137 (der Planer trägt die Zahl nach dem Lauf ein).
Alle Fälle bisher nur mit Fakes bzw. künstlichen Daten, kein echter Server.
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Ein Zyklus je Konto: Abgleich, dann Durchlauf, dann Meldung; Rückgabe ist der Bericht des Abgleichs | ✅ `ClassifyingSyncCycle` (`Composition`), `AccountPipelines` (Durchlauf auf der Verbindung des Zyklus) | ✅ Fall 1 – S3 2137/2137 |
| 2 | Hat der Durchlauf Verschiebungen oder Aufbewahrungs-Löschungen eingestellt, läuft der Zyklus des Kontos bald erneut (Befehle laufen sofort); zurückgewiesene Löschungen und Bremse lösen nichts aus | ✅ `ClassifyingSyncCycle` → `MailboxService.TriggerAccount` | ✅ Fall 2 (6 Zeilen) – S3 2137/2137 |
| 3 | Wirft der Abgleich, läuft der Durchlauf nicht, kein Logeintrag aus dem Zyklus, die Ausnahme geht weiter | ✅ `ClassifyingSyncCycle` | ✅ Fall 3 – S3 2137/2137 |
| 4 | Wirft der Durchlauf, steht eine Zeile mit dem Typnamen im Log (ohne Mail-Daten), die Ausnahme geht weiter (`WaitingAfterError`); Abbruch durch Beenden schreibt nichts | ✅ `ClassifyingSyncCycle` (`ICriticalLog`) | ✅ Fall 4 – S3 2137/2137 |
| 5 | Wirft ein Empfänger der Meldung, steht eine Logzeile, der Zyklus ist trotzdem normal | ✅ `ClassifyingSyncCycle` | ✅ Fall 5 – S3 2137/2137 |
| 6 | `EvaluationFailed` > 0 oder `BrakeHeld` > 0: eine Logzeile mit Zahlen, ohne Mail-Daten | ✅ `ClassifyingSyncCycle` | ✅ Fall 6 – S3 2137/2137 |
| 7 | `classification-policy.json` kennt `maxMovesPerPass` (1 bis 1000, Standard 100); 0, 1001 oder falscher Typ → Fehler mit Schlüsselnamen | ✅ `ClassificationPolicy`, `ClassificationPolicyFile` | ✅ Fall 7 – S3 2137/2137 |
| 8 | Bremse: genau der Grenzwert wird verschoben; eine Verschiebung mehr → keine, alle `BrakeHeld`, Warteschlange leer, Marke rückt weiter | ✅ `ClassificationPass` (je Quellordner, über alle Ziele), `ClassificationOutcome.BrakeHeld` | ✅ Fall 8 – S3 2137/2137 |
| 9 | Die Bremse gilt je Quellordner: Posteingang verschiebt, Junk wird zurückgehalten (und umgekehrt) | ✅ `ClassificationPass` | ✅ Fall 9 – S3 2137/2137 |
| 10 | `RecordOnly` und ruhendes `Act` kennen keine Bremse (200 Mails, kein `BrakeHeld`, kein Befehl) | ✅ `ClassificationPass` | ✅ Fall 10 – S3 2137/2137 |
| 11 | Zurückgehaltene Mails werden im nächsten Durchlauf nicht erneut entschieden (`SkippedKnown`) | ✅ `ClassificationPass` | ✅ Fall 11 – S3 2137/2137 |
| 12 | Beim Start werden beide Dateien gelesen; ungültige Datei → `InvalidDataException`, Sperre frei, App zeigt Fenster mit Dateinamen; fehlende Dateien → Standardwerte | ✅ `MailWardenRuntime.OpenAsync`, `App.xaml.cs` (`UiText.StartErrorInvalidClassifierFile`) | ✅ Fall 12 (Laufzeit) – S3 2137/2137. Ungetestet: das Fehlerfenster selbst (GUI, nur von Hand) |
| 13 | Die Laufzeit meldet `ClassificationPassCompleted` aus der Dienstschleife, löst Folgezyklen aus und meldet nach dem Beenden nichts mehr | ✅ `MailWardenRuntime` (Ereignis), `OpenWithCycleStepsAsync` (Nahtstelle, ohne Server) | ✅ Fall 13 – S3 2137/2137 |
| 14 | Hinweis in der Infoleiste bei `EvaluationFailed` oder `BrakeHeld`, mit Zahlen; unbekannte Konten werden ignoriert | ✅ `ShellViewModel`, `ShellBanner.Classification`, `UiText.BannerClassificationProblems` | ✅ Fall 14 (geändert durch fix.2, siehe 21) – S3 2137/2137 |
| 15 | `ActSuspendedNoContacts`: eigener fester Text „wartet auf das Adressbuch“, keine Zahl; ohne `Act` nie ein Hinweis | ✅ `ShellBanner.ClassificationWaiting`, `UiText.BannerClassificationWaiting` | ✅ Fall 15 – S3 2137/2137 |
| 16 | Das Ereignis erreicht die Oberfläche über den Dispatcher; ein Fehler im Empfänger wird protokolliert, nicht weitergereicht | ✅ `IMailboxController.ClassificationPassCompleted` | ✅ Fall 16 – S3 2137/2137 |
| 17 | Manuell (Dennis): App auf der Datenkopie mit neuer Mail: nichts wandert, kein Hinweis, der Abgleich wird nicht langsamer | ✅ gebaut | **offen – manuell Dennis** (`work\manual-tests.md`, Teil 7) |
| 18 | Manuell (Planer, Kopie, nur Zählungen): `classification-policy.json` mit `act`, ein Testkontakt, Testmails nur zwischen Konto 2 und Konto 3 nach Freigabe von Dennis → Mails wandern durch den laufenden Dienst, bald nach Ankunft | ✅ gebaut | ✅ Planer 2026-11-01 auf der Kopie mit AP-06, nur Konto 2: 2 Testmails von Konto 3 kamen bei Outlook in Junk an, der Dienst stufte sie als `TrustedContact` ein (Kontakt, DMARC bestanden) und verschob sie auf dem Server in den Posteingang. „Bald nach Ankunft“ gilt für Junk-Ankünfte nur bis zum nächsten Zyklus: Im ersten 4-Minuten-Lauf kam kein Zyklus, sie wurden erst beim nächsten Dienststart verschoben → `TODO.md` |
| 19 | Eine Abbruch-Ausnahme, deren eigenes Token nicht abgebrochen ist (z. B. Zeitüberschreitung beim Token), schreibt eine Logzeile mit dem Typnamen und geht weiter | ✅ `ClassifyingSyncCycle` (fix.2, Q1) | ✅ Fall 19 – S3 2137/2137. Erledigt damit PLAN AP-07 #4b „Fehler im Log“ (mit Fällen 3, 4) |
| 20 | Dieselbe Ausnahme mit abgebrochenem eigenem Token: keine Logzeile, geht weiter | ✅ `ClassifyingSyncCycle` (fix.2, Q1) | ✅ Fall 20 – S3 2137/2137 |
| 21 | Hinweis-Summen seit App-Start: 2 nicht bewertet, dann ein sauberer Durchlauf, dann 3 zurückgehalten bei einem anderen Konto → Summen bleiben 2, dann 2 und 3; ein sauberer Durchlauf löscht nichts | ✅ `ShellViewModel` (fix.2, Q2), Text beginnt „Spamfilter seit dem Start:“ | ✅ Fall 21 – S3 2137/2137 |

Nicht von Tests gedeckt: Fälle 17 und 18 (manuell, offen), das Fehlerfenster bei ungültiger Datei, der Hinweis in der echten
Oberfläche, jeder Lauf gegen einen echten Server. Bedingungen vor `Act` auf einem echten Konto: `TODO.md`, „Vertagt aus
AP-07d“. Vollständiger Schnittstellenvertrag: `work\AP-07d\requirement.md`.

### AP-07c Anbindung des Klassifizierers
Sicherheitsregeln (Planer 2026-11-01): Standard nur protokollieren; Altbestand wird nie automatisch einsortiert; Handeln
ruht, solange das Adressbuch leer ist (→ AP-06); je Mail nur eine Entscheidung; Aufbewahrung Standard 0 = nie löschen.
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Nach dem Abgleich werden nur neu eingetroffene Mails aus Posteingang und Junk klassifiziert; erster Lauf setzt nur die Marke | ✅ `ClassificationPass` (Marke je Ordner; nur Ordner mit abgeschlossenem Abgleich) | ✅ Fälle 11, 12, 21, 34, 35 – S3 1276/1276 |
| 2 | Jede Entscheidung wird mit Stufe, Punkten und allen Begründungen gespeichert, auch beim bloßen Protokollieren | ✅ `ClassificationLogEntry`, `SqliteClassificationLog` (Schema v5) | ✅ Fälle 5, 12, 22, 52–54 – S3 1276/1276 |
| 3 | Modus `Act`: Verschieben über die Warteschlange nach Tabelle (Posteingang → Screener/Junk/Quarantäne; Junk → Posteingang nur für `TrustedContact`) | ✅ `ClassificationRouting`, `ClassificationPass`; Modus `act` ist standardmäßig aus | ✅ Fälle 1, 13–16, 23–25, 37, 42, 47, 48 – S3 1276/1276; nur mit künstlichen Daten und Fake-Servern, am echten Server ungetestet (Fall 33 offen) |
| 4 | Fehlende Ordner `Screener`/`Quarantine` werden angelegt; schlägt das fehl, bleibt die Mail liegen, nichts geht verloren | ✅ `MailKitFolderProvisioner`; `ClassificationPass` bricht bei einem Fehler ab, ohne zu speichern | Durchlauf-Seite mit Fake-Provisioner ✅ Fälle 17, 18 – S3 1276/1276; Provisioner ✅ Fall 33: Ordner auf Gmail und Outlook.com angelegt; Fehlerfall am echten Server nicht geprüft |
| 5 | Eine von Hand zurückgeholte Mail wird nicht erneut entschieden | ✅ `MessageKey` (Message-ID + oberstes `Received`), Entscheidungsprotokoll | ✅ Fälle 2, 19, 20, 36–39 – S3 1276/1276; ✅ Fall 33: Schlüssel bleibt beim Verschieben auf Gmail und Outlook.com gleich, danach `SkippedKnown`; Zurückholen per Server-MOVE, nicht im Webmail |
| 6 | Aufbewahrung Quarantäne: nur von der App verschobene Mails, Uhr ist der Entscheidungszeitpunkt; 0 = nie | ✅ `ClassificationPass` (Aufbewahrung, Standard 0) | ✅ Fälle 26–28, 39, 43, 44, 50 – S3 1276/1276; Löschen am echten Server ungetestet |
| 7 | Schema v5 migriert ältere Datenbanken ohne Datenverlust | ✅ `SchemaMigrations` (v5), `SqliteDatabase.CurrentSchemaVersion` = 5 | ✅ Fälle 4, 6 – S3 1276/1276 |
| 8 | Echte Daten: Bestand auf einer Kopie plausibel; am Server nur nach Freigabe mit Testmails von Dennis | ⚠️ Prüfprogramm `_scratch\classification-probe` (nicht im Repo) | ✅ Fall 32 erledigt (Planer 2026-11-01, Kopie der Entwicklungsdaten, nur Zählungen; plausibel, kein Absturz). ✅ Fall 33 (Planer 2026-11-01, Freigabe Dennis, Konten Konto 2 und Google, Testmails per Prüfprogramm): siehe Anforderung. `Act` auf echten Konten braucht weiter AP-06 und D8 |

Weitere gelaufene Fälle (S3 1276/1276): 40, 41, 45, 46, 49, 51, 55 (Leser, Tokenizer, verschachtelte MIME-Teile). Nicht automatisiert: Fälle 31–33.

Vollständiger Schnittstellenvertrag: `work\AP-07c\requirement.md`. Verdrahtung in die Laufzeit (D8) erst, wenn AP-04a
und AP-07c auf `main` liegen.

### AP-07b Kalibrierung + Einstellungsdatei
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | `dmarc=fail` mit `compauth=pass` (Microsoft) → Status „nicht verifiziert“ statt „fehlgeschlagen“, 0 Punkte mit Begründung; ohne `compauth=pass` bleibt DMARC fail unüberstimmbar | ✅ `AuthenticationResults.cs` (`CompAuth`), `AuthenticationResultsParser.cs`, `AuthStatusEvaluator.cs`, `Signals\AuthenticationSignal.cs` | ✅ `CompAuthAndVerdictTokenTests` (1–5) – verify.2 395/395 |
| 2 | `dmarc=bestguesspass` → wie kein DMARC-Ergebnis; `dkim=timeout` → TempError | ✅ `AuthenticationResultsParser.cs` (`ParseVerdict`, gilt für alle Methoden) | ✅ `CompAuthAndVerdictTokenTests` (6–7) |
| 3 | Bei authentifizierten Mails geben `link-domain-mismatch`, `domain-not-in-body` und die schwache Stufe von `random-domain` keine Punkte; bei nicht authentifizierten unverändert | ✅ `Signals\LinkDomainMismatchSignal.cs`, `Signals\DomainNotInBodySignal.cs`, `Signals\RandomDomainSignal.cs` | ✅ `AuthGatedContentSignalTests` (8–12) |
| 4 | `classifier-settings.json`: fehlt → Standardwerte; einzelne Schlüssel überschreiben Standardwerte; unbekannter Schlüssel, falscher Typ oder ungültige Schwellen → Fehler beim Laden mit Schlüsselname | ✅ `Infrastructure\Classification\ClassifierSettingsFile.cs` (bei ungültigen Schwellen nennt die Meldung den Eigenschaftsnamen aus `Validate()`, F3 akzeptiert; zusätzlich doppelter Schlüssel → Fehler) | ✅ `ClassifierSettingsFileTests` (13–20) |
| 5 | PstProbe-Lauf nach der Änderung: Fehlalarm aus Befund `compauth` behoben, Junk-Erkennung nicht schlechter | – | ✅ Planer 2026-10-31: andere Ordner LikelySpam 1 → 0, Junk unverändert 1 Certain / 3 Likely / 2 LikelyLegit |

Vollständiger Schnittstellenvertrag: `work\AP-07b\requirement.md`.

### AP-07 Klassifizierer (Kern des Projekts, deshalb vorab)
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Absender im Adressbuch + SPF/DKIM pass → `TrustedContact`, auch wenn Mail im Junk-Ordner lag → wird in Posteingang verschoben | AP-07a: Stufe (`SpamClassifier`, Kontakt + Authenticated; seit C11 verhindert DMARC fail das); Verschieben: AP-07c `ClassificationPass` (Modus `act`, standardmäßig aus; AP-07c #3) | `SpamClassifierTests` (15, 21a); Verschieben: AP-07c Fälle 13, 14 – am echten Server ungetestet (Fall 33 offen) |
| 2 | Absender im Adressbuch, DKIM fail → **nicht** `TrustedContact`; Begründung nennt die fehlgeschlagene Prüfung | AP-07a `SpamClassifier`, `AuthenticationSignal` | `SpamClassifierTests` (16), `SignalReasonTests` (30) |
| 3 | Domain `qzxkvb73.top`, nicht im Body, Auth fail → `CertainSpam` → Ordner `Quarantine`; Protokoll enthält jedes Signal mit Punkten | AP-07a: Stufe + Signale mit Begründung; Ordner `Quarantine`: AP-07c `ClassificationPass` (#3, #4) | `SpamClassifierTests` (14), `ClassifierSmokeTests`; AP-07c Fall 13 – Anlegen und Verschieben am echten Server ungetestet |
| 4 | Unbekannter Absender, Auth pass, Domain plausibel → `LikelyLegit` → Ordner `Screener` | AP-07a: Stufe; Ordner `Screener`: AP-07c `ClassificationPass` (#3, #4) | `SpamClassifierTests` (13); AP-07c Fall 13 – am echten Server ungetestet |
| 4a | Dennis bestätigt Screener-Mail („kein Junk“) → Absender ins Adressbuch, Mail in Posteingang, künftige Mails `TrustedContact` | → AP-08 | |
| 4b | Fehlt `Screener` oder `Quarantine` auf dem Server, legt die App den Ordner an; schlägt das fehl → Mail bleibt liegen, Fehler im Log, kein Verlust | AP-07c #4: `MailKitFolderProvisioner` | AP-07c Fälle 17, 18 (Fake) und Fall 33 (echte Server, Anlegen); „Fehler im Log“: erledigt mit AP-07d (Fälle 3–4, 19; S3 2137/2137), nur mit Fakes |
| 5 | Mail ohne `Authentication-Results`-Header → neutral (keine Punkte), nie Absturz | AP-07a `AuthStatusEvaluator`, Parser wirft nie | `SpamClassifierTests` (18), `AuthenticationResultsParserTests` (5) |
| 6 | Block-Liste schlägt alles, Allow-Liste schlägt alles außer fehlgeschlagener Prüfung; `TrustedContact` über die Allow-Liste nur mit bestandener Prüfung (`Authenticated`), sonst höchstens `LikelyLegit` (geändert durch AP-06 fix.2, R1) | AP-07a `SpamClassifier` (Entscheidungsreihenfolge); AP-06 fix.2 | `SpamClassifierTests` (19, 20); AP-06 Fall 20 – S3 2406/2406 |
| 7 | Quarantäne: Mails älter als X Tage werden gelöscht; X=0 bedeutet nie automatisch löschen | AP-07c #6: `ClassificationPass` (Aufbewahrung, Standard 0 = nie) | AP-07c Fälle 26–28, 39, 43, 44, 50; Löschen am echten Server ungetestet |
| 8 | Schwellen aus Einstellungen; ungültige Schwellen (Schwelle 2 ≤ Schwelle 1) → Fehler beim Laden | AP-07a `ClassifierSettings.Validate()` (Laden aus Datei: AP-07b `ClassifierSettingsFile`; Verwendung im Durchlauf: AP-07c) | `ClassifierSettingsTests` (27), `ClassifierSettingsFileTests` (13–20, AP-07b) |

## Risiken und offene Annahmen
- **Microsoft App-Registrierung:** evtl. nur mit (kostenlosem) Azure-Konto, dafür ggf. Kreditkarte zur Verifizierung.
  Klärt AP-00. Ohne Registrierung kein Hotmail-Zugriff → Projekt-Stopper.
- **WinUI 3 unpackaged + self-contained:** funktioniert laut Windows App SDK ab 1.1; wird in AP-01 mit der Leer-App
  verifiziert. Fallback bei Blocker: WPF mit Fluent-Theme.
- **Google-OAuth unverifiziert:** Consent zeigt eine Warnung „nicht verifiziert“; akzeptabel für Eigengebrauch.
  Google kann unverifizierte Apps mit sensiblen Scopes auf 100 Nutzer beschränken – unkritisch.
- **Gmail-Labels** statt Ordner: Verschieben = Label ändern; `All Mail` ausblenden.
- **IDLE/Verbindungsabbrüche:** Wiederverbindung mit Backoff; Sync bleibt ohne App nicht aktuell (Filter läuft nur
  bei laufender App).
- **HTML-Mails im Dark Mode** sehen teils schlecht aus, ihre Farben werden nie umgeschrieben: eine Mail mit eigenen Farben
  bleibt als helles Blatt unter einem festen Abdunkelungsschleier (25 % schwarz) im dunklen Fenster, nur eine Mail ohne eigene Farben wird dunkel (AP-04d, G1). Bekannte Grenze: entfernt
  die Bereinigung den dunklen Hintergrund einer Mail und bleibt ihre helle Schrift, steht heller Text auf dem hellen Blatt
  (`<body style="background:#111">` mit `<td style="color:#eee">`). `main` verhält sich genauso; die Helligkeit zu erraten
  gehört nicht zum Umfang (AP-04d, F2).
- **Öffentliches Repo:** Secrets/Tokens/Maildaten nur über `.gitignore` geschützt; vor jedem Commit `git status`
  gegen die Liste in `CLAUDE.md` prüfen. Test-Fixtures ausschließlich künstlich.

## Manuelle Prüfung durch Dennis
- Alle OAuth-/Consent-Abläufe (Browserfenster, Microsoft-/Google-Konto).
- Echte Konten: Sync-Vollständigkeit, Verschieben auf dem Server (Kontrolle am Handy/Web).
- Klassifizierer gegen echte Spam-Mails aus dem Junk-Ordner (Treffer und Fehlalarme).
- Versand: kommt die Mail beim Empfänger korrekt an (Signatur, Anhang, Antwort-Verkettung)?
