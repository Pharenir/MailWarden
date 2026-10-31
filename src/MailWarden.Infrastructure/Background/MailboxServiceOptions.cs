namespace MailWarden.Infrastructure.Background;

/// <summary>
/// Settings of the <see cref="MailboxService"/>.
/// </summary>
/// <param name="PollInterval">Time from a successful cycle to the next scheduled one; at least 1 minute.</param>
/// <param name="IdleRenewInterval">
/// Longest single IDLE wait on the inbox before it is renewed; 1..28 minutes (servers end an IDLE after about 29 minutes).
/// </param>
/// <param name="TriggerDebounce">Wait after a trigger before the cycle starts, so bursts coalesce; 0..60 seconds.</param>
public sealed record MailboxServiceOptions(TimeSpan PollInterval, TimeSpan IdleRenewInterval, TimeSpan TriggerDebounce)
{
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinIdleRenewInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxIdleRenewInterval = TimeSpan.FromMinutes(28);
    private static readonly TimeSpan MaxTriggerDebounce = TimeSpan.FromSeconds(60);

    private readonly TimeSpan pollInterval = ValidatePollInterval(PollInterval);
    private readonly TimeSpan idleRenewInterval = ValidateIdleRenewInterval(IdleRenewInterval);
    private readonly TimeSpan triggerDebounce = ValidateTriggerDebounce(TriggerDebounce);

    /// <summary>Poll 15 minutes, IDLE renewal 9 minutes, debounce 2 seconds.</summary>
    public static MailboxServiceOptions Default { get; } =
        new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(9), TimeSpan.FromSeconds(2));

    /// <summary>Time from a successful cycle to the next scheduled one; at least 1 minute.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1 minute.</exception>
    public TimeSpan PollInterval
    {
        get => pollInterval;
        init => pollInterval = ValidatePollInterval(value);
    }

    /// <summary>Longest single IDLE wait before it is renewed; 1..28 minutes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1..28 minutes.</exception>
    public TimeSpan IdleRenewInterval
    {
        get => idleRenewInterval;
        init => idleRenewInterval = ValidateIdleRenewInterval(value);
    }

    /// <summary>Wait after a trigger before the cycle starts; 0..60 seconds.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0..60 seconds.</exception>
    public TimeSpan TriggerDebounce
    {
        get => triggerDebounce;
        init => triggerDebounce = ValidateTriggerDebounce(value);
    }

    private static TimeSpan ValidatePollInterval(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, MinPollInterval, nameof(PollInterval));
        return value;
    }

    private static TimeSpan ValidateIdleRenewInterval(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, MinIdleRenewInterval, nameof(IdleRenewInterval));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxIdleRenewInterval, nameof(IdleRenewInterval));
        return value;
    }

    private static TimeSpan ValidateTriggerDebounce(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(TriggerDebounce));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxTriggerDebounce, nameof(TriggerDebounce));
        return value;
    }
}
