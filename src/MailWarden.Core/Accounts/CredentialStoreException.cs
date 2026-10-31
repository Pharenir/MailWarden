namespace MailWarden.Core.Accounts;

/// <summary>
/// A stored secret cannot be decrypted or is corrupt. The message never contains secret bytes.
/// </summary>
public sealed class CredentialStoreException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Description of the failure (names the key, never the secret).</param>
    /// <param name="inner">Underlying exception (e.g. the DPAPI failure), or null.</param>
    public CredentialStoreException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
