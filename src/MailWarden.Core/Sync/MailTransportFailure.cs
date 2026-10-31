namespace MailWarden.Core.Sync;

/// <summary>
/// Category of a mail transport failure; decides whether a sync aborts or only one folder fails.
/// </summary>
public enum MailTransportFailure
{
    /// <summary>Connection could not be established, broke off or timed out.</summary>
    Network,

    /// <summary>The server rejected the login.</summary>
    Authentication,

    /// <summary>The server rejected a command or answered unexpectedly (e.g. folder does not exist).</summary>
    Protocol,
}
