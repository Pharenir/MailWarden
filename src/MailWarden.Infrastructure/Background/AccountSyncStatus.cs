using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Background;

/// <summary>
/// Status of one account for the UI. Carries no message texts, names or addresses.
/// </summary>
/// <param name="AccountId">Account the status belongs to.</param>
/// <param name="State">State of the account loop.</param>
/// <param name="LastSuccessAt">End of the last successful cycle, or null when none succeeded yet.</param>
/// <param name="LastErrorType">
/// Exception type name of the last failed cycle and, for a <c>MailTransportException</c>, its failure value (e.g.
/// <c>"MailTransportException(Network)"</c>); never a message. Null after a successful cycle.
/// </param>
/// <param name="NextRunAt">Time of the next scheduled cycle, or null when none is scheduled (running, sign-in needed, stopped).</param>
/// <param name="Progress">Latest sync progress while a cycle is running; null otherwise.</param>
public sealed record AccountSyncStatus(
    Guid AccountId,
    AccountSyncState State,
    DateTimeOffset? LastSuccessAt,
    string? LastErrorType,
    DateTimeOffset? NextRunAt,
    SyncProgress? Progress);
