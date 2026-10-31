namespace MailWarden.Core.Model;

/// <summary>
/// Mail provider of an account. Selects the provider profile (hosts, authentication, special-folder mapping).
/// </summary>
public enum MailProvider
{
    /// <summary>Microsoft consumer account (Outlook.com / Hotmail), OAuth via MSAL.</summary>
    Microsoft,

    /// <summary>Google account (Gmail), OAuth via Google.</summary>
    Google,

    /// <summary>Any other IMAP/SMTP provider.</summary>
    GenericImap,
}
