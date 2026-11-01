# AP-04a – Testplan (author.0, ohne Produktcode geschrieben, nicht gebaut, nicht gelaufen)

Branch `test/ap-04a-ui-read`. Alle Tests gegen `requirement.md` (D1-D11, Fälle 1-41). Fälle 42-48 sind manuell.
Tags: `feature:ui` an jedem Test, zusätzlich `feature:storage` / `feature:sync` wo passend; Smoke-Tests sind
`SafeDisplayTextTests`, `ReadingDocumentBuilderTests`, `WebContentPolicyTests`, `MailWardenRuntimeTests.Open_exposes_...`,
`NavigationViewModelTests.Six_group_items_...`, `ReadingPaneViewModelTests.Ready_state_...`.

## Fall -> Test

| Fall | Datei (unter `tests\`) | Tests |
|---|---|---|
| 1 | `MailWarden.Core.Tests\Text\SafeDisplayTextTests.cs` | alle Methoden (Bidi-Theory, CR/LF/TAB, Cc-Theory, Nachbarzeichen bleiben, Emoji, null) |
| 2-8 | `MailWarden.Infrastructure.Tests\Mime\MailContentReaderTests.cs` | 2: Alternative, Stream bleibt offen, From bereinigt; 3: Text-only; 4: Html-only; 5: PNG inline + 3 weitere erlaubte Typen; 6: SVG, >5 MB, 6x4,5 MB (25-MB-Grenze); 7: Pfad/Bidi-Name, Name fehlt; 8: leerer Stream, Abbruch |
| 9-15 | `...\Mime\MailHtmlSanitizerTests.cs` | 9: 21 Elemente x (klein, gemischte Schreibweise, verschachtelt) + Script/Style-Text + Positivkontrolle; 10: Attribute auf allen Elementen; 11: 4 erlaubte / 15 gesperrte hrefs; 12: cid bekannt/unbekannt, 4 erlaubte data-Typen, 8 gesperrte Quellen, remote blockiert/erlaubt (+Zählung); 13: 3 Remote-Bilder; 14: remote `url()`, 11 gefährliche Stil-Fälle mit Farb-Positivkontrolle; 15: 18 kaputte Eingaben, Steuerzeichen, 5000 Ebenen (offen/geschlossen), null, leere Inline-Map |
| 16-17 | `...\Mime\ReadingDocumentBuilderTests.cs` | CSP exakt blockiert/erlaubt (per Parser, `content` exakt), fremde Meta-Tags, HTML5/charset/Farbe, Zähler aus Sanitizer, Klartext mit `<script>` kodiert, Klartext-CSP |
| 18 | `MailWarden.Presentation.Tests\WebContentPolicyTests.cs` | Allow, Varianten der Dokument-URI, OpenExternally (5), Block (21 inkl. userinfo, javascript, data, file, about, relativ, Müll) |
| 19 | dieselbe Datei | Dokument-URI immer, Remote-Bild nur mit Flag, Nicht-Bild nie, file/data/javascript/ftp/mailto/relativ nie |
| 20 | `...Infrastructure.Tests\Storage\SqliteMessageStoreUnreadCountTests.cs` | leer, Seen/Flagged, Rolle über Konten, Konto, Ordner (gewinnt gegen Rolle), Limit/Offset ignoriert, folgt UpdateFlags |
| 21 | `...Presentation.Tests\MailViewAndFolderTreeTests.cs` | Fabriken, Wertgleichheit, `ToQuery`, `GroupRoles`-Reihenfolge |
| 22 | dieselbe Datei | Hierarchie, fehlende Eltern (Folder null), Bidi-Namen bereinigt, Geschwister `OrdinalIgnoreCase` (Eingabereihenfolge egal), nur `/` trennt, Eltern+Kind merge |
| 23 | `...Presentation.Tests\UiSettingsFileTests.cs` | Datei fehlt, `{}`, partiell, voll, Grenzwerte 150/2000, Round-Trip, Überschreiben, kein BOM + Schlüsselnamen + keine Temp-Datei, 15 ungültige Fälle (Dateiname + Schlüssel, nie Werte), kaputtes JSON leckt keinen Inhalt, Erholung |
| 24 | `...Infrastructure.Tests\Composition\MailWardenRuntimeTests.cs` | zweiter Open -> `DataFolderInUseException`, DB-Datei unverändert (Größe+Zeit); fremde Sperre -> DB wird nicht angelegt; fehlgeschlagener zweiter Open gibt Sperre nicht frei |
| 25 | dieselbe Datei | Open/Dispose/Open, Lock-Datei weg, Dispose doppelt, alle Handles frei (Ordner löschbar), neueres Schema -> `SchemaVersionException` und Sperre wieder frei |
| 26 | `...Infrastructure.Tests\Background\MailboxServiceHeldLockTests.cs` | Start mit `heldLock` (Zyklus läuft), Stop und Dispose lassen Sperre gehalten, Kontrolle ohne `heldLock` unverändert. Dafür bekam `MailboxServiceHarness` den optionalen Parameter `heldLockFactory` |
| 27 | `MailWardenRuntimeTests` | zwei `OpenDatabaseAsync`-Verbindungen schreiben je 200 Nachrichten parallel, 400 Zeilen vorhanden |
| (D6) | `MailWardenRuntimeTests` | ReadModel: Konten/Ordner/Nachrichten/Ungelesen, `ReadContentAsync` (Inhalt, kein offener Reader, null bei fehlendem Pfad/Datei), Lesen setzt kein `\Seen` |
| 28 | `...Infrastructure.Tests\Logging\CriticalFileLogTests.cs` | Format, Anhängen über Instanzen, CR/LF je Feld, zwei Threads x 200, Ordner fehlt, Ordner unbenutzbar (Datei im Weg), Logdatei exklusiv gesperrt |
| 29 | `...Presentation.Tests\NavigationViewModelTests.cs` | sechs Gruppen (Labels, Views, Ungelesen), Kontoknoten, Kind-Ansichten + Ordnerbaum, keine Rollenordner doppelt, Standardauswahl Posteingang, Labels bereinigt/Fallback Adresse, StatusText je Zustand fix und verschieden, leere Konten -> Banner `AuthProbe` |
| 30 | dieselbe Datei | Kontoknoten und Baum-Elternknoten ignoriert (Auswahl + keine Ladeaufrufe), Positivkontrolle |
| 31 | `...Presentation.Tests\MessageListViewModelTests.cs` | Zeilenfelder, Name vs. Adresse, leerer Betreff, Bereinigung, Kontolabel, Unread/Flagged/Anhang, Datumstexte (Fake-Uhr 2026-10-31 12:00 UTC; Erwartung in lokaler Zone der Maschine) |
| 32 | dieselbe Datei | 250 Nachrichten: 100/200/250, HasMore, keine Duplikate, Offsets 0/100/200, kein Nachladen ohne HasMore, genau eine volle Seite, leere Ansicht, Ansichtswechsel setzt Offset zurück |
| 33 | dieselbe Datei | Reload behält Anzahl + Auswahl per Id, leert Auswahl, mind. eine Seite, ohne Auswahl |
| 34 | dieselbe Datei | langsame erste Ladung kommt nach der zweiten: zweite bleibt; Gegenrichtung; Auswahl steuert Lesebereich (Empty bei keiner Zeile/anderer Ansicht) |
| 35 | `...Presentation.Tests\ReadingPaneViewModelTests.cs` | langsames erstes `ShowAsync` kommt nach dem zweiten; spätes Ergebnis überschreibt kein späteres `Empty` |
| 36 | dieselbe Datei | Empty, Ready (Kopf, Anhangnamen, Dokument), Bereinigung, Klartext, NotDownloaded (kein Pfad / Datei fehlt), Error (Marker nie in Text/Log, nur Typname), Erholung, Abbruch ist kein Error |
| 37 | dieselbe Datei | geblockt+gezählt, Command baut ohne zweites Lesen neu (PropertyChanged), Freigabe gilt nur für diese Nachricht, ohne Remote-Bilder kein Angebot |
| 38 | `...Presentation.Tests\ShellViewModelTests.cs` | nur Fortschritt: kein Reload; unbekanntes Konto; Running->Idle lädt Navigation+Liste neu; Burst koalesziert zu genau einem Zusatz-Reload; Dispatcher-Marshalling; Handler-Ausnahme (Dispatcher wirft) und fehlschlagender Reload: nur Typname im Log, nie propagiert |
| 39 | dieselbe Datei | Idle/Running -> Trigger, NeedsSignIn/WaitingAfterError -> Retry, jeweils genau einmal; ohne Konten nichts |
| 40 | dieselbe Datei | Einstellungen werden übernommen, Layout/Theme: PropertyChanged + Save aller vier Werte, Größen, Toggle-Command, Save-Fehler -> Banner + Log (nur Typname) + App läuft, Load-Fehler (echte `UiSettingsFile` mit kaputtem JSON) -> Defaults + Banner |
| 41 | dieselbe Datei | Reflexion: `IMailboxReadModel` genau 5 Lese-Member, `IMailboxController` nur Trigger/Retry, keine Schreib-Methodennamen in den ViewModels; Lesesitzung ohne Controller-Aufruf, Ungelesen-Zahl unverändert; nur Refresh ruft den Controller |

Nicht automatisiert: 42-48 (manuell), WebView2-Verdrahtung und XAML (D4 unten, D11), `ObservableObject`/Command-Klassen
nur indirekt (PropertyChanged, `ICommand`) über die ViewModels.

## Anpassungen bestehender Test-Infrastruktur
- `TestSupport\FaultyMessageStore.cs`: `CountUnreadAsync` (Weiterleitung).
- `TestSupport\MailboxServiceHarness.cs`: optionaler Parameter `heldLockFactory` (nur Fall 26), Eigenschaft `HeldLock`, Konstruktor ruft `MailboxService(..., heldLock: HeldLock)`.
- `tests\test-map.json`: Presentation, Core\Text, Infrastructure\Composition und \Logging, Mime jetzt auch `feature:ui`, App -> `feature:ui`.
- Neues Projekt `tests\MailWarden.Presentation.Tests` (in `MailWarden.sln`, ohne BOM, Mappe `tests`).

## Annahmen (für verify.1 wichtig)
- Test-EMLs entstehen im Code mit MimeKit (`TestSupport\ReadingEml.cs`) statt als Dateien unter `tests\fixtures\`; so lassen sich 5-MB-/25-MB-Fälle ohne große Binärdateien erzeugen.
- Sanitizer-Tests parsen das Ergebnis mit AngleSharp (kommt transitiv über HtmlSanitizer in `MailWarden.Infrastructure`); sollte der Builder das Paket `PrivateAssets` setzen, braucht das Testprojekt eine eigene Referenz (nur mit Rückfrage).
- Die ViewModels werden nur über `ShellViewModel` konstruiert (`Navigation`/`Messages`/`Reading`); `NavigationItem`, `MessageRowViewModel` usw. haben im Vertrag keine Konstruktoren.
- Alles, was ein Property-Setter anstößt (Auswahl), wird mit Polling (`Wait.UntilAsync`, 10 s) abgewartet; direkt `await`bare Methoden (`LoadAsync`, `ShowAsync`, `ReloadAsync`, `LoadMoreAsync`, `InitializeAsync`) werden direkt erwartet.

## Fragen an den Planer
1. **`MailContentReader` bei kaputtem Stream (Fall 8):** nur der leere Stream ist sicher "nicht lesbar" (MimeKit parst Müll meist tolerant). Getestet wird nur leerer Stream -> `MailContentException`. Soll ein weiterer Fall (z. B. Stream, dessen Read wirft) ebenfalls `MailContentException` ergeben?
2. **`UiSettingsFile(string path)`:** ich lese `path` als Pfad der Datei (`...\ui-settings.json`), nicht des Ordners. Falsch, wenn der Ordner gemeint ist.
3. **`SelectedItem`/`SelectedRow`-Setter:** ich nehme an, dass sie das Laden anstoßen, ohne dass der Aufrufer es abwarten kann (deshalb Polling). Wartet `InitializeAsync` auf die erste Liste? Die Tests funktionieren in beiden Fällen.
4. **`MessageRowViewModel.DateText` "local time":** Erwartung nutzt `TimeZoneInfo.Local`; die Fake-Uhr (`FixedClock`) überschreibt `LocalTimeZone` bewusst nicht, sodass `TimeProvider.LocalTimeZone` und `TimeZoneInfo.Local` gleich sind.
5. **Sanitizer, Stil `url(https://...)` bei `allowRemoteImages=true`:** Test erwartet, dass `background-image: url(https://...)` im `style` bleibt (D3). Die Standard-Liste erlaubter CSS-Eigenschaften von HtmlSanitizer enthält `background-image`; falls der Builder sie streicht, ist das ein Befund oder eine Vertragsänderung.
6. **Fehlerklasse bei Abbruch:** ReadingPane/Liste bei `OperationCanceledException` des Lesers: Test erwartet nur "kein Error, kein Logeintrag", lässt offen, ob die Ausnahme durchgereicht oder geschluckt wird.
7. **StatusText:** nur "nicht leer, je Zustand verschieden, enthält keinen Exception-Text" geprüft, keine exakten deutschen Texte (in `UiText` frei wählbar). Banner-Texte ebenso (nur `AuthProbe` für leere Konten).
8. **Risiko Stapelüberlauf:** der 5000-Ebenen-Test (Fall 15) würde bei rekursiver Verarbeitung den Testhost abschießen statt nur zu scheitern; ggf. einzeln mit `--filter "FullyQualifiedName~Very_deep_nesting"` starten.
