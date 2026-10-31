namespace MailWarden.Core.Sync;

/// <summary>
/// A mail transport operation failed. The message is one English sentence; it never contains a token or a full
/// server response.
/// </summary>
public sealed class MailTransportException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">Failure category.</param>
    /// <param name="message">One English sentence describing the failure.</param>
    /// <param name="inner">Underlying library exception, or null.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="failure"/> is not a defined value.</exception>
    public MailTransportException(MailTransportFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown mail transport failure.");
        }

        Failure = failure;
    }

    /// <summary>Failure category.</summary>
    public MailTransportFailure Failure { get; }
}
