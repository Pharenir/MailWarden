namespace MailWarden.Core.Commands;

/// <summary>
/// Executor side of the command queue.
/// </summary>
public interface IMailCommandStore
{
    /// <summary>Lists the pending commands of the account that are due (NextAttemptAt null or ≤ now), oldest first.</summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="now">Current time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Due commands in queue order; failed commands are never listed.</returns>
    Task<IReadOnlyList<MailCommand>> ListDueAsync(Guid accountId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Removes a completed command. Unknown id = no-op.</summary>
    /// <param name="commandId">Command id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the command is gone.</returns>
    Task CompleteAsync(long commandId, CancellationToken ct = default);

    /// <summary>
    /// Records a failed attempt: Attempts + 1, LastError and NextAttemptAt are set; permanent → Status Failed (never
    /// retried). Unknown id = no-op.
    /// </summary>
    /// <param name="commandId">Command id.</param>
    /// <param name="error">Exception type name or a fixed English text of this app; never server text.</param>
    /// <param name="nextAttemptAt">Earliest next attempt; null = due at once (ignored for scheduling when permanent).</param>
    /// <param name="permanent">True = the command is never retried.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the failure is stored.</returns>
    Task RecordFailureAsync(
        long commandId,
        string error,
        DateTimeOffset? nextAttemptAt,
        bool permanent,
        CancellationToken ct = default);
}
