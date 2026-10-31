# MailWarden – Privacy Policy

Last updated: 2026-10-31

MailWarden is a desktop application that runs entirely on the user's own Windows computer. It is developed and
operated by its author for personal use. The source code is public; the application itself is not distributed as a
service.

## What data the app accesses
With the user's explicit consent (Google OAuth scope `https://mail.google.com/`, Microsoft scopes
`IMAP.AccessAsUser.All` and `SMTP.Send`), MailWarden reads, moves, deletes and sends e-mail in the user's own mail
accounts via IMAP and SMTP.

## Where data is stored
All e-mail data, contacts and settings are stored locally on the user's computer. OAuth tokens are stored encrypted
with Windows Data Protection (DPAPI) and never leave the device.

## What is not done with the data
- No data is transmitted to the author or to any third party.
- No data is sold, shared, analysed for advertising or used to train machine-learning models outside the device.
- The author operates no servers; the app talks only to the mail servers of Google and Microsoft.

## Limited Use disclosure
MailWarden's use and transfer of information received from Google APIs adheres to the
[Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including
the Limited Use requirements.

## Revoking access
Access can be revoked at any time at <https://myaccount.google.com/permissions> (Google) or
<https://account.live.com/consent/Manage> (Microsoft). Local data is removed by deleting the application's data folder.

## Contact
Open an issue in this repository.
