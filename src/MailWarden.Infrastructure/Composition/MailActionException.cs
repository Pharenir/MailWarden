namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// An action on mail was refused. The message is a fixed German text the user interface can show; it never holds mail data
/// or the text of a lower layer. Commands stored before the refusal stay in the queue.
/// </summary>
public sealed class MailActionException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Fixed text without any mail data.</param>
    public MailActionException(string message)
        : base(message)
    {
    }
}
