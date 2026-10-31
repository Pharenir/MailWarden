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
| AP-03 | Sync-Engine: Ordnerliste, inkrementeller Sync (UIDVALIDITY, UIDNEXT, Flags), IDLE für Posteingang, Junk-Ordner immer mitsynchronisieren, Befehlswarteschlange (Move/Delete/Flag) mit Wiederholung | Mails aller Konten lokal, Änderungen beidseitig |
| AP-04 | WinUI-3-Oberfläche: Gruppenansichten Posteingang/Gesendet/Postausgang/Screener/Junk/Quarantäne über alle Konten, dieselben Ansichten je Konto, Ordnerbaum, Lesebereich (WebView2, bereinigt), Light/Dark/System | Lesen aller Konten in einer Ansicht |
| AP-05 | Verfassen/Antworten/Weiterleiten, SMTP-Versand je Konto, Anhänge, Signatur je Konto, Gesendet-Ordner | Senden von allen Konten |

### Phase 2 – Der eigentliche Grund
| AP | Inhalt | Ergebnis |
|---|---|---|
| AP-06 | Adressbuch minimal: lokal, Import vCard/CSV, automatisches Sammeln aus gesendeten Mails, Allow-/Block-Listen | Grundlage für Stufe `TrustedContact` |
| AP-07 | Klassifizierer: Signale v1, Schwellen, Entscheidungsprotokoll, Anlegen von `Screener`/`Quarantine` je Konto, Aufbewahrung Quarantäne, Rettung sicherer Absender aus Junk | Vier Stufen sichtbar, jede Entscheidung erklärbar |
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
  ersetzt „ohne Git“): Builder (fable) auf `feat/ap-NN-<slug>`, Tester (sonnet) auf `test/ap-NN-<slug>`, Reviewer
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

### AP-07 Klassifizierer (Kern des Projekts, deshalb vorab)
| # | Sollverhalten | Umgesetzt | Getestet |
|---|---|---|---|
| 1 | Absender im Adressbuch + SPF/DKIM pass → `TrustedContact`, auch wenn Mail im Junk-Ordner lag → wird in Posteingang verschoben | | |
| 2 | Absender im Adressbuch, DKIM fail → **nicht** `TrustedContact`; Begründung nennt die fehlgeschlagene Prüfung | | |
| 3 | Domain `qzxkvb73.top`, nicht im Body, Auth fail → `CertainSpam` → Ordner `Quarantine`; Protokoll enthält jedes Signal mit Punkten | | |
| 4 | Unbekannter Absender, Auth pass, Domain plausibel → `LikelyLegit` → Ordner `Screener` | | |
| 4a | Dennis bestätigt Screener-Mail („kein Junk“) → Absender ins Adressbuch, Mail in Posteingang, künftige Mails `TrustedContact` | | |
| 4b | Fehlt `Screener` oder `Quarantine` auf dem Server, legt die App den Ordner an; schlägt das fehl → Mail bleibt liegen, Fehler im Log, kein Verlust | | |
| 5 | Mail ohne `Authentication-Results`-Header → neutral (keine Punkte), nie Absturz | | |
| 6 | Block-Liste schlägt alles, Allow-Liste schlägt alles außer Auth-fail | | |
| 7 | Quarantäne: Mails älter als X Tage werden gelöscht; X=0 bedeutet nie automatisch löschen | | |
| 8 | Schwellen aus Einstellungen; ungültige Schwellen (Schwelle 2 ≤ Schwelle 1) → Fehler beim Laden | | |

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
