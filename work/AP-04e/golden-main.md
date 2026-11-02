# Golden hashes for E8 (AP-04e)

SHA-256 (upper-case hex) over the UTF-8 bytes of `ReadingDocument.Html`, from `main` at 966df28, before AP-04e. `allowEmbeddedImages: true` in every row. All mails use subject `Subject`, from `sender_a@example.test`, to `recipient_a@example.test`, no Cc, no date, no inline images, no attachments.

| Name | Html | PlainText |
|---|---|---|
| plain | null | `Hello <world>` newline (`\n`) `Line 2 & more` |
| html-without-colors | `<p>Hallo <a href="https://links.example.test/a">Link</a> <mark><a href="https://links.example.test/b">Mark</a></mark></p>` | `Hallo` |
| html-with-colors | `<p style="color:#000">Text</p><img src="https://images.example.test/a.png">` | `Text` |

| Mail | Theme, call | allowRemoteImages | SHA-256 |
|---|---|---|---|
| plain | Light | False | 0E84BED217D164B44E9D567BE7D8C5D9D154445B342845401EE6667FE0E82C1D |
| plain | Light | True | 0E84BED217D164B44E9D567BE7D8C5D9D154445B342845401EE6667FE0E82C1D |
| plain | Dark | False | 38FC44AA9CCBC5595BAE5C79F4E6362BCE8237E330C5E2D9913C83B7ED8125B4 |
| plain | Dark | True | 38FC44AA9CCBC5595BAE5C79F4E6362BCE8237E330C5E2D9913C83B7ED8125B4 |
| plain | Light, BuildFromPlainText | - | 0E84BED217D164B44E9D567BE7D8C5D9D154445B342845401EE6667FE0E82C1D |
| plain | Dark, BuildFromPlainText | - | 38FC44AA9CCBC5595BAE5C79F4E6362BCE8237E330C5E2D9913C83B7ED8125B4 |
| html-without-colors | Light | False | D3FB9A1CF7B2400D4E2226AF44B9D10E84D3F06B04D4E55217C68CBC5F36DC86 |
| html-without-colors | Light | True | 4C627454B531FD78A7722DF937A686FBD81FA997D9FE624B836837CC0642B66C |
| html-without-colors | Dark | False | 609BFA710CA52513C8D7098BF70551AE34C77F895938C73E7DFD4801148C0448 |
| html-without-colors | Dark | True | A90C43E14694D6A867C6192500E657B29B12CBDE9DC0ED3B28583923853FD14D |
| html-without-colors | Light, BuildFromPlainText | - | A2E55612D599CA318C4CAF5C36D76F611087ED94E18D6B5984C9D6A8C5F9EA02 |
| html-without-colors | Dark, BuildFromPlainText | - | 34F35317FF4C3F0198A9C408E8E04FB839D9FEB5626B9114364110EA13D4A67B |
| html-with-colors | Light | False | 7B9621B1AC48F207C74472B7B57034C17F3A683BF8EFBA3DBDBDD9059C8A8F08 |
| html-with-colors | Light | True | 7DB74459E60CE29A069F80C97841F0737199060F9DFF5DE894A8D50E6E1A8ED2 |
| html-with-colors | Dark | False | 88575697597701E5171C9F589B53EF5987C5E25EA996931E5E942B849B58BC88 |
| html-with-colors | Dark | True | 7853E3A86A257332812D022D4B05DD9C3DB71573F9811303AD028A595589AF1E |
| html-with-colors | Light, BuildFromPlainText | - | 319C242E1BBB089BF39EAB274B83AFF15FCEAB7BF00713793B6879E35A27F0B7 |
| html-with-colors | Dark, BuildFromPlainText | - | 5BDD5BC469507D06330CEBBAC52A292294A04C01F8743548E4DC428235538A95 |
