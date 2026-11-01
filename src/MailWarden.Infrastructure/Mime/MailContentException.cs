namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// A stream could not be parsed as a message. The message is a fixed text and there is no inner exception, so no mail
/// content can reach a log through this exception.
/// </summary>
public sealed class MailContentException : Exception
{
    /// <summary>Creates the exception with its fixed message.</summary>
    public MailContentException()
        : base("The stream could not be parsed as a mail message.")
    {
    }
}
