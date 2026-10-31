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
| AP-02 | Kontenverwaltung + Zugangsdaten (DPAPI), Token-Cache, Provider-Profile. **AP-02a** ohne UI (Credential-Store, OAuth MS/Google, Provider-Profile, Ordnerrollen, Konto-Service, Verbindungstest); **AP-02b** = Konten-UI mit AP-04 | Konto anlegen/löschen/testen |
| AP-03 | Sync-Engine: Ordnerliste, inkrementeller Sync (UIDVALIDITY, UIDNEXT, Flags), IDLE für Posteingang, Junk-Ordner immer mitsynchronisieren, Befehlswarteschlange (Move/Delete/Flag) mit Wiederholung. **AP-03a** = Transport + Abgleich Server → lokal (Ordner, Mails, Flags, Löschungen, UIDVALIDITY, Schema v2); **AP-03b** = Abgleich robust und schnell (Blöcke, Fortschritt, Wiederverbindung im Lauf, Gmail Important); **AP-03c** = Befehlswarteschlange lokal → Server; **AP-03d** = Hintergrunddienst (Zeitplan, IDLE) | Mails aller Konten lokal, Änderungen beidseitig |
| AP-04 | WinUI-3-Oberfläche: Gruppenansichten Posteingang/Gesendet/Postausgang/Screener/Junk/Quarantäne über alle Konten, dieselben Ansichten je Konto, Ordnerbaum, Lesebereich (WebView2, bereinigt), Light/Dark/System | Lesen aller Konten in einer Ansicht |
| AP-05 | Verfassen/Antworten/Weiterleiten, SMTP-Versand je Konto, Anhänge, Signatur je Konto, Gesendet-Ordner | Senden von allen Konten |

### Phase 2 – Der eigentliche Grund
| AP | Inhalt | Ergebnis |
|---|---|---|
| AP-06 | Adressbuch minimal: lokal, Import vCard/CSV, automatisches Sammeln aus gesendeten Mails, Allow-/Block-Listen | Grundlage für Stufe `TrustedContact` |
| AP-07 | Klassifizierer: Signale v1, Schwellen, Entscheidungsprotokoll, Anlegen von `Screener`/`Quarantine` je Konto, Aufbewahrung Quarantäne, Rettung sicherer Absender aus Junk. **AP-07a** = Kern (erledigt); **AP-07b** = Kalibrierung an echten Headern (PstProbe) + Einstellungsdatei; **AP-07c** = Anbindung an Mails/Ordner (nach AP-03c) | Vier Stufen sichtbar, jede Entscheidung erklärbar |
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
| 8 | WinUI-3-Leer-App baut mit `dotnet build` ohne Visual Studio und startet unpackaged (Fenster „MailWarden“, folgt dem Windows-Farbschema) | `src\MailWarden.App` (baut) | manuell Dennis – offen |

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
| 6 | Echte Konten: 10 min Dienstlauf ohne Fehler, Testmail löst sofort einen Abgleich aus, sauberes Beenden | ⚠️ Werkzeug `_scratch\service-probe` (git-ignoriert, im Worktree), Nutzung: `work\AP-03d\serviceprobe-usage.md`; Komposition mit eigener SQLite-Verbindung je Konto und Ablauf, siehe `work\AP-03d\impl-notes.md` O1 | ⚠️ Fall 19 läuft (Planner, 2026-10-31): Start, zweite Instanz („in Benutzung“) und Testmail → Abgleich nach 2 s ✅; IDLE-Erneuerung nach 9 min und sauberes Beenden ausstehend |

Vollständiger Schnittstellenvertrag: `work\AP-03d\requirement.md`.

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
| 1 | Absender im Adressbuch + SPF/DKIM pass → `TrustedContact`, auch wenn Mail im Junk-Ordner lag → wird in Posteingang verschoben | AP-07a: Stufe (`SpamClassifier`, Kontakt + Authenticated; seit C11 verhindert DMARC fail das); Verschieben → AP-07c | `SpamClassifierTests` (15, 21a) |
| 2 | Absender im Adressbuch, DKIM fail → **nicht** `TrustedContact`; Begründung nennt die fehlgeschlagene Prüfung | AP-07a `SpamClassifier`, `AuthenticationSignal` | `SpamClassifierTests` (16), `SignalReasonTests` (30) |
| 3 | Domain `qzxkvb73.top`, nicht im Body, Auth fail → `CertainSpam` → Ordner `Quarantine`; Protokoll enthält jedes Signal mit Punkten | AP-07a: Stufe + Signale mit Begründung; Ordner `Quarantine` → AP-07c | `SpamClassifierTests` (14), `ClassifierSmokeTests` |
| 4 | Unbekannter Absender, Auth pass, Domain plausibel → `LikelyLegit` → Ordner `Screener` | AP-07a: Stufe; Ordner → AP-07c | `SpamClassifierTests` (13) |
| 4a | Dennis bestätigt Screener-Mail („kein Junk“) → Absender ins Adressbuch, Mail in Posteingang, künftige Mails `TrustedContact` | → AP-08 | |
| 4b | Fehlt `Screener` oder `Quarantine` auf dem Server, legt die App den Ordner an; schlägt das fehl → Mail bleibt liegen, Fehler im Log, kein Verlust | → AP-07c | |
| 5 | Mail ohne `Authentication-Results`-Header → neutral (keine Punkte), nie Absturz | AP-07a `AuthStatusEvaluator`, Parser wirft nie | `SpamClassifierTests` (18), `AuthenticationResultsParserTests` (5) |
| 6 | Block-Liste schlägt alles, Allow-Liste schlägt alles außer Auth-fail | AP-07a `SpamClassifier` (Entscheidungsreihenfolge) | `SpamClassifierTests` (19, 20) |
| 7 | Quarantäne: Mails älter als X Tage werden gelöscht; X=0 bedeutet nie automatisch löschen | → AP-07c | |
| 8 | Schwellen aus Einstellungen; ungültige Schwellen (Schwelle 2 ≤ Schwelle 1) → Fehler beim Laden | AP-07a `ClassifierSettings.Validate()` (Laden aus Datei → AP-07c) | `ClassifierSettingsTests` (27) |

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
- **HTML-Mails im Dark Mode** sehen teils schlecht aus; v1 invertiert nicht, zeigt Hell-Hintergrund im Lesebereich.
- **Öffentliches Repo:** Secrets/Tokens/Maildaten nur über `.gitignore` geschützt; vor jedem Commit `git status`
  gegen die Liste in `CLAUDE.md` prüfen. Test-Fixtures ausschließlich künstlich.

## Manuelle Prüfung durch Dennis
- Alle OAuth-/Consent-Abläufe (Browserfenster, Microsoft-/Google-Konto).
- Echte Konten: Sync-Vollständigkeit, Verschieben auf dem Server (Kontrolle am Handy/Web).
- Klassifizierer gegen echte Spam-Mails aus dem Junk-Ordner (Treffer und Fehlalarme).
- Versand: kommt die Mail beim Empfänger korrekt an (Signatur, Anhang, Antwort-Verkettung)?
