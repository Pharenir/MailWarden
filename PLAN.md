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
| AP-02 | Kontenverwaltung + Zugangsdaten (DPAPI), Token-Cache, Provider-Profile. **AP-02a** ohne UI (Credential-Store, OAuth MS/Google, Provider-Profile, Ordnerrollen, Konto-Service, Verbindungstest); **AP-02b** = Konten-UI, nach AP-04c | Konto anlegen/löschen/testen |
| AP-03 | Sync-Engine: Ordnerliste, inkrementeller Sync (UIDVALIDITY, UIDNEXT, Flags), IDLE für Posteingang, Junk-Ordner immer mitsynchronisieren, Befehlswarteschlange (Move/Delete/Flag) mit Wiederholung. **AP-03a** = Transport + Abgleich Server → lokal (Ordner, Mails, Flags, Löschungen, UIDVALIDITY, Schema v2); **AP-03b** = Abgleich robust und schnell (Blöcke, Fortschritt, Wiederverbindung im Lauf, Gmail Important); **AP-03c** = Befehlswarteschlange lokal → Server; **AP-03d** = Hintergrunddienst (Zeitplan, IDLE) | Mails aller Konten lokal, Änderungen beidseitig |
| AP-04 | WinUI-3-Oberfläche: Gruppenansichten Posteingang/Gesendet/Postausgang/Screener/Junk/Quarantäne über alle Konten, dieselben Ansichten je Konto, Ordnerbaum, Lesebereich (WebView2, bereinigt), Light/Dark/System. **AP-04a** = nur lesen (Laufzeit, Ansichten, Liste, Lesebereich, umschaltbare Aufteilung); **AP-04b** = Aktionen über die Warteschlange, Gelesen-Schalter, Suche, Fenstergröße, Link-Tooltip; **AP-04c** = Anhänge öffnen/speichern (Konten-UI: AP-02b, Dennis 2026-11-02); **AP-04d** = Lesebereich folgt dem Thema; **AP-04e** = Nightshade-Thema als Standard | Lesen aller Konten in einer Ansicht |
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
Abgeschlossene Pakete stehen ungekürzt in `PLAN-ARCHIVE.md` (nur bei Bedarf lesen, Regel `~/.claude/refs/planning.md`): AP-00, AP-03b, AP-03c, AP-03d, AP-06, AP-04d.

Wird vor Beginn des jeweiligen AP ausformuliert (Normalfall · Randfälle · Fehlerfall · Positivkontrolle) und hier mit
Spalten *Umgesetzt* / *Getestet* geführt. Vorab festgelegt:

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

### AP-04c Anhänge öffnen und speichern
Freigabe Dennis 2026-11-02. Vollständiger Vertrag: `work\AP-04c\requirement.md`. Das Fenster läuft in keinem Test; was nur der Handtest belegt, steht dann als „manuell Dennis“.
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| A1 | Jeder Anhang der offenen Mail steht im Lesebereich mit Name, Größe, Öffnen und Speichern | ✅ `Presentation\AttachmentRow.cs`, `ReadingPaneViewModel.Attachments`, `App\MainWindow.xaml` |  |
| A2 | Speichern nur über den Dialog von Windows, Inhalt als Strom, kein Speichern ohne Dialog | ✅ `ReadingPaneViewModel.SaveAttachmentAsync`, `Infrastructure\Mime\AttachmentFiles.SaveAsync`, `MailContentReader.CopyAttachmentAsync`, `App\WindowAttachmentHost.cs` (Dialog noch nicht gemessen) |  |
| A3 | Öffnen aus eigenem Ordner unter den Daten, mit Vermerk „aus dem Internet“; ohne Vermerk kein Öffnen; Ordner wird beim Start und Beenden geleert | ✅ `AttachmentFiles.PrepareForOpeningAsync`/`Clear`, `Storage\DataPaths.AttachmentsOpenRoot`, `App\App.xaml.cs`, `App\WindowAttachmentHost.cs` (Start noch nicht gemessen) |  |
| A4 | Sicherer Dateiname: keine Pfade, kein `:`, keine Steuerzeichen, keine reservierten Namen, Länge begrenzt | ✅ `Presentation\AttachmentPolicy.cs` (`AttachmentFileName`), `Storage\DataPaths.IsDosDeviceName` |  |
| A5 | Drei Stufen nach der letzten Endung: Programme und Skripte nur speichern, harmlose sofort öffnen, alles andere nach Rückfrage | ✅ `Presentation\AttachmentPolicy.cs` (`Classify`), Listen in `work\AP-04c\impl-notes.md` |  |
| A6 | Aus Junk, Quarantäne und Screener fragt jedes Öffnen nach; Gesperrtes bleibt gesperrt; es entscheidet die Rolle, nie der Ordnername | ✅ `AttachmentPolicy.MustAskBeforeOpening`, `ReadingPaneViewModel.OpenAttachmentAsync`, `UiText.ConfirmOpenAttachment`, `ShellViewModel` (Frage reiht sich ein) |  |
| A7 | Der angegebene Dateityp entscheidet nichts, nur die Endung | ✅ `AttachmentPolicy.Classify` kennt nur den Namen |  |
| A8 | Fehler beim Dekodieren, gelöschte Mail, volle Platte, abgebrochener Dialog: lesbare Meldung oder nichts, kein Absturz, keine halbe Datei | ✅ `ReadingPaneViewModel` (`AttachmentNotice`, Log nur Typname), `AttachmentFiles` |  |
| A9 | Ein Anhang wird über seine Stelle in der Liste angesprochen und gegen Name und Größe geprüft | ⚠️ `MailContentReader.CopyAttachmentAsync`, `MailboxReadModel`: Der Vertrag übergibt keine Erwartungswerte, also merkt sich das Read-Model die zuletzt gelieferten Listen je EML und vergleicht damit; Begründung in `impl-notes.md` |  |
| A10 | Unbekannte Ordnerrolle zählt für A6 wie Junk | ✅ `AttachmentPolicy.MustAskBeforeOpening` (null), Rolle aus `ShowAsync` |  |
| A11 | `message/rfc822` wird als `.eml` gespeichert oder geöffnet; Teile unter der Tiefengrenze haben keine Aktion | ✅ `MailContentReader.WriteContentAsync`, `AttachmentFileName.ToSafeForMessage`, `MailAttachmentInfo.IsAttachedMessage` |  |
| A12 | Öffnen und Speichern setzen kein Flag und berühren den Server nicht | ✅ `ReadingPaneViewModel` (Zeilen nur in `ShowHeader`/`ShowContentHeader`, kein Aufruf von `IMailboxActions`) |  |

### AP-04e Nightshade-Thema
Wunsch Dennis 2026-11-01: viertes Thema, als Standard. Spike und vollständiger Vertrag: `work\AP-04e\requirement.md`. Stand: verify.2 S3 2965/2965 (Core 536, Presentation 629, Infrastructure 1800), secure.1 ohne Kritisch/Hoch. Die Oberfläche selbst läuft in keinem Test; was nur der Handtest belegt, steht als „manuell Dennis“. Die Hover-Farben und Grenzen stehen in den Decisions der Anforderung.
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| E1 | `AppTheme.Nightshade`, gespeichert als `nightshade`; unbekannte Werte werden weiter abgelehnt | ✅ `Presentation\UiSettings.cs`, `Presentation\UiSettingsFile.cs` | ✅ Fälle 3, 4, 5 |
| E2 | Standard ist Nightshade; ein gespeichertes Thema bleibt | ✅ `Presentation\UiSettings.cs` (`Default`); `UiSettingsFile` beginnt beim Laden mit `Default` | ✅ Fälle 1, 2, 3 |
| E3 | Auswahl in der Reihenfolge Nightshade, System, Hell, Dunkel (`AppThemes.ChoiceOrder`) | ✅ `Presentation\AppThemes.cs`, `Presentation\UiText.cs`, `App\MainWindow.xaml.cs` | ✅ Fälle 6, 7. Dass die Auswahl im Fenster sie nutzt: manuell Dennis, Fall 25 |
| E4 | `ReadingTheme.Nightshade`; `AppThemes.ToReadingTheme` liefert es unabhängig vom Fenster | ✅ `Infrastructure\Mime\ReadingTheme.cs`, `Presentation\AppThemes.cs` | ✅ Fall 8 |
| E5 | Mail ohne eigene Farben und Klartext: Seite `#231E33`, Text `#ECE8F7`, Links `#FF8A3D`, Auswahl `#4A3B7A`, kein Schleier | ✅ `Infrastructure\Mime\ReadingDocumentBuilder.cs` (`NightshadeStyle`) | ✅ Fälle 9, 10, 21 |
| E6 | Mail mit eigenen Farben: helles Blatt unter dem Schleier, Stil wie im Dunkelmodus | ✅ `Infrastructure\Mime\ReadingDocumentBuilder.cs` (`SelectPageStyle`, dieselbe Konstante `DimmedLightStyle`) | ✅ Fall 11 |
| E7 | `PageBackground(Nightshade)` = `#231E33`, kein Aufblitzen | ✅ `Infrastructure\Mime\ReadingDocumentBuilder.cs` | ✅ Fall 15 |
| E8 | Hell und Dunkel bleiben byte-gleich | ✅ `Infrastructure\Mime\ReadingDocumentBuilder.cs` (Stile textgleich; nur die Regel für `mark` als gemeinsame Konstante) | ✅ Fall 12: 18 SHA-256-Werte vom Stand `main` |
| E9 | Bereinigung und CSP unverändert, Stil nur aus eigenen Konstanten | ✅ `Infrastructure\Mime\ReadingDocumentBuilder.cs` (Bereinigung und CSP nicht angefasst, Auswahl nur aus Thema und Ja/Nein) | ✅ Fälle 13, 14; secure.1: 264 Vergleiche ohne Unterschied außerhalb des Seitenstils |
| E10 | Wechsel bei offener Mail ohne erneutes Lesen, auch Dunkel ↔ Nightshade | ✅ `App\MainWindow.xaml.cs` (`ApplyReadingTheme` über `AppThemes.ToReadingTheme`); `ReadingPaneViewModel` unverändert | ✅ Fälle 16, 17, 18 |
| E11 | `Themes\NightshadeTheme.xaml`: nur Wörterbuch `Dark`, keine doppelten Schlüssel, jeder Alias vorher definiert | ✅ `App\Themes\NightshadeTheme.xaml`, erzeugt von `tools\make_nightshade_theme.py`; dazu der eigene Schlüssel `NightshadeWindowBackgroundBrush` | ✅ Fälle 19, 20 |
| E12 | Fenster zeigt die Palette: Steuerelemente, Flyouts, Menüs, Dialoge, Titelleiste, kein Mica | ✅ `App\Themes\NightshadeTheme.xaml`, `App\WindowThemeChrome.cs` (im Spike gelaufen, in der App Handtest offen) | ⚠️ Nicht automatisiert prüfbar. Echte `WindowThemeChrome` und echtes Wörterbuch im Spike gemessen. **Manuell Dennis – offen** (Fälle 26, 27) |
| E13 | Wechsel live in jede Richtung ohne sichtbares Aufblitzen; Verlassen stellt Mica und Titelleiste wieder her | ✅ `App\WindowThemeChrome.cs` (im Spike gelaufen, alle Richtungen ohne gezeichnetes Bild dazwischen; in der App Handtest offen) | ⚠️ Nicht automatisiert prüfbar. Spike: alle Richtungen ohne gezeichnetes Bild dazwischen, Mica und Titelleiste zurück. **Manuell Dennis – offen** (Fall 25) |
| E14 | Start mit Nightshade ohne Aufblitzen eines anderen Themas | ✅ `App\MainWindow.xaml.cs` (Palette vor `InitializeComponent`), `App\WindowThemeChrome.cs` (in der App Handtest offen) | ⚠️ Nicht automatisiert prüfbar. Spike: Text und Buttons beim Start in der Palette (fix.2). **Manuell Dennis – offen** (Fall 23) |
| E15 | Windows-Hochkontrast: Palette, Fensterfläche und Titelleiste werden nicht angewendet | ✅ `App\WindowThemeChrome.cs` (`AccessibilitySettings`, gelesen bei jedem Anwenden; eine Änderung in Windows gilt ab dem nächsten Themenwechsel oder Start, weil das Abonnieren in der App ohne Paket wirft; im Spike gelaufen, in der App Handtest offen) | ⚠️ Nicht automatisiert prüfbar, im Spike nur der Zweig ohne Hochkontrast. Manuell Dennis, optional (Fall 29) |
| E16 | `Core`/`Infrastructure` ohne WinUI, `AppThemes` in `Presentation`, Code-Behind verdrahtet nur | ✅ `Presentation\AppThemes.cs`, `App\WindowThemeChrome.cs`, `App\MainWindow.xaml.cs` | ✅ Build mit `-warnaserror`; Schichten laut review.1 eingehalten |
| E17 | Ein Fehler beim Themenwechsel beendet die laufende App nicht (kritische Logzeile mit Typname); beim Start bleibt das Fehlerfenster | ✅ `App\MainWindow.xaml.cs` (`ApplyThemeWhileRunning`) | ❌ Kein Test (WinUI, Fehlerfall nicht auslösbar); nur gelesen, review.2 |

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
