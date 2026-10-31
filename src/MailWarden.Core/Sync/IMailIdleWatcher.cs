namespace MailWarden.Core.Sync;

/// <summary>
/// Watches the inbox of one account on an open, read-only (EXAMINE) session and reports server-side changes (IMAP
/// IDLE). Never changes anything on the server. Not thread-safe; one wait at a time.
/// </summary>
public interface IMailIdleWatcher : IAsyncDisposable
{
    /// <summary>
    /// Waits on the inbox until the server reports a change (new message, expunge, flag change) or
    /// <paramref name="maxWait"/> elapsed.
    /// </summary>
    /// <param name="maxWait">Longest time to stay in one wait; the caller renews the wait after it.</param>
    /// <param name="ct">Cancellation token; cancelling ends the wait.</param>
    /// <returns>True on a change, false on timeout.</returns>
    /// <exception cref="MailTransportException">The connection broke off, timed out or the server rejected the wait.</exception>
    /// <exception cref="NotSupportedException">The server has no IDLE capability.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    Task<bool> WaitForChangeAsync(TimeSpan maxWait, CancellationToken ct = default);
}
