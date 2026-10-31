namespace MailWarden.Infrastructure.Background;

/// <summary>
/// Waiting abstraction so tests can control time: completes after the delay or when cancelled. Every wait of the
/// <see cref="MailboxService"/> (poll, backoff, debounce, watcher reconnect) goes through it.
/// </summary>
public interface IServiceDelay
{
    /// <summary>Waits for the given time.</summary>
    /// <param name="delay">Time to wait; not negative.</param>
    /// <param name="ct">Cancellation token; cancelling ends the wait with an <see cref="OperationCanceledException"/>.</param>
    /// <returns>A task that completes after the delay.</returns>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}
