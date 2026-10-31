# AP-03a – secure.1 (security-reviewer, opus; recorded by the planner)

**Result: 0 Kritisch · 0 Hoch · 2 Mittel · 4 Niedrig** – no objection to closing the AP.

## Mittel
- **M1 Poison message** (`SyncEngine.cs:250-256`, `:183-186`, `:341-351`): one message whose EML is locked by an AV
  scanner, a non-FormatException from MimeKit or UID 0 stops the folder at that UID on every run; newer mail never
  arrives. Same for a locked file of a removed folder (whole account). → fix.2 F1/F2.
- **M2 Plain-text mail readable by other local accounts**: `K:\` and `K:\Projekte` inherit "Authenticated Users (M)"
  and "Users (RX)"; data folder created without ACL (`FileEmlStore.cs:43`). Since AP-03a this holds private mail and
  the FTS text. Recommendation: ACL without inheritance (current user + SYSTEM) when creating the data folder, or
  default `%LOCALAPPDATA%\MailWarden`; decide before first real use. → Dennis decides (TODO AP-04).

## Niedrig
- **N1** `SyncEngine.cs:185` folder Error copies `exception.Message` (IO messages contain absolute paths, folder names
  are personal). → logging rule AP-04.
- **N2** no memory limit per message (`MailKitTransport.cs:201`, `SyncEngine.cs:346`, `MimeBodyText.cs:41,49`);
  use `persistent: true`. → fix.2 F6.
- **N3** empty/incomplete LIST deletes the local store (`SyncEngine.cs:149-165`) → require an Inbox. → fix.2 F4.
- **N4** data remnants after deletion (orphan EML, empty dirs, SQLite without `secure_delete`). → TODO AP-03b/AP-04.

## Hints
- `.gitignore` covers only `data/` → add `*.db*`, `*.eml` (except fixtures). → fix.2 F7.
- Subject/FromName/folder names may contain control/bidi characters (RTLO) – relevant for the UI AP.
- Authentication failure does not invalidate the token (availability only).

## Checked and fine
Paths only from account GUID, folder id, UID + `ResolveMailPath` checks; deletes only via own `EmlPath`; read-only
(EXAMINE, BODY.PEEK, no STORE/EXPUNGE/MOVE/APPEND/CREATE); TLS endpoints fixed, default certificate validation, no
protocol logger; token only to `SaslMechanismOAuth2`; parameterised SQL; SyncProbe prints counts only; no vulnerable
packages (`dotnet list package --vulnerable --include-transitive`).

## Entry points
Outgoing IMAP (TLS, XOAUTH2) to the two providers; foreign mail content as data; SyncProbe (local, Dennis). No
listening ports.

## Not checked
Real MailKit/MimeKit behaviour against servers (case 28); Defender behaviour; AP-01 stores only superficially.
