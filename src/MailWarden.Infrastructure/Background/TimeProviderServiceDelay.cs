namespace MailWarden.Infrastructure.Background;

/// <summary>
/// <see cref="IServiceDelay"/> on the timers of a <see cref="TimeProvider"/>; the production implementation.
/// </summary>
/// <param name="timeProvider">Clock whose timers drive the waits (normally <see cref="TimeProvider.System"/>).</param>
public sealed class TimeProviderServiceDelay(TimeProvider timeProvider) : IServiceDelay
{
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is negative.</exception>
    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        return Task.Delay(delay, timeProvider, ct);
    }
}
