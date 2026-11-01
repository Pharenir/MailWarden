# AP-04a – Testergebnis (verify.3, nach fix.3)

```
Stufe:        S3
Auswahl nach: voll
Ergebnis:     1618 von 1623 Tests grün (Core 430/430, Infrastructure 880/884, Presentation 308/309)
Fehlschläge:  5 (siehe unten; alle Einordnung b oder c, kein eigener Testfehler offen)
Nicht gelaufen: Fälle 42-48 (manuell, Dennis)
Laufzeit:     ca. 15 s
Abbruch:      –
Auffällig:    Fälle 66-68 und 49 (extreme Eingaben) liefen ohne Prozessende und im Timeout.
```

Stand: `feat/ap-04a-ui-read` nach Merge von `test/ap-04a-ui-read`, Build `-warnaserror` grün. Ergebnis: **FAIL** (5 Tests rot).

## Offene Fehlschläge
1. **`OpaquePartsTests.Message_rfc822_below_the_depth_limit_is_flagged_and_not_listed_as_attachment` (depth 40 und 1023)** – (b) Produkt weicht von L5 ab.
   Erwartet: `HasOpaqueParts` true und `Attachments` leer. Tatsächlich: `HasOpaqueParts` true, aber `Attachments` enthält `{ FileName = "", ContentType = "message/rfc822", Size = null }`.
   Reproduktion: `dotnet test tests\MailWarden.Infrastructure.Tests --filter "FullyQualifiedName~OpaquePartsTests"`; EML = `DeepMimeEml.NestedRfc822(40)` (40 geschachtelte `message/rfc822`). Der Multipart-Fall (Tiefe 40/100/1023) ist grün.
2. **`SenderLineTests.Name_of_exactly_100_characters_is_not_cut`** – (b)/(c). L6: "The name stays cut at 100". Erwartet: Name mit genau 100 Zeichen unverändert. Tatsächlich: Zeile `LLL…(ca. 76 L)… <sender_a@example.test>` mit "…", die ganze Zeile ist auf 101 Zeichen gekürzt, also scheint die Gesamtzeile statt nur der Name zu kürzen. Ein Name mit 300 Zeichen verhält sich wie erwartet.
   Reproduktion: `--filter "FullyQualifiedName~Name_of_exactly_100"`.
3. **`ReadingDocumentBuilderTests.Document_contains_the_no_referrer_meta_and_the_unchanged_csp(withHtml: False, allowRemoteImages: True)` und `Plain_text_document_with_allowed_images_has_the_allowed_csp`** – (c) Anforderung unklar / Verhaltensänderung durch fix.3. Erwartet (D3): Das Klartext-Dokument bei `allowRemoteImages: true` hat die CSP mit `img-src data: http: https:`. Tatsächlich: `img-src data:`. In verify.2 war das grün; das Klartext-Dokument ist jetzt immer streng. Fachlich unkritisch (Klartext hat keine Bilder), aber der Wortlaut von D3 gilt nicht mehr. Planer: Test auf "streng" anpassen oder Produkt zurücknehmen.
   Reproduktion: `--filter "FullyQualifiedName~ReadingDocumentBuilderTests"`.

## Eigene Testfehler in diesem Lauf (behoben)
- `Selecting_the_same_mail_again_during_its_own_build_starts_no_second_build`: Die Anforderung legt nur "kein zweiter Aufbau" fest. Das Produkt zeigt danach die Textfassung mit Hinweis "dauert zu lange", obwohl der Aufbau schnell fertig wurde (Uhr nicht vorgestellt). Test prüft jetzt "kein zweiter Aufbau" und "HTML-Dokument oder Textfassung mit Hinweis". **Hinweis an den Planer (c):** Für den Nutzer wirkt das wie ein falscher Hinweis; ob gewollt, bitte entscheiden.
