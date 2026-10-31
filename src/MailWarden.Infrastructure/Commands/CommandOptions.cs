namespace MailWarden.Infrastructure.Commands;

/// <summary>
/// Settings of the command executor.
/// </summary>
/// <param name="MaxAttempts">
/// Failed attempts after which a command that the server rejects (or that fails otherwise, except network failures) is
/// marked Failed and never retried; 1..20, default 5.
/// </param>
public sealed record CommandOptions(int MaxAttempts = 5)
{
    private const int MinMaxAttempts = 1;
    private const int MaxMaxAttempts = 20;

    private readonly int maxAttempts = ValidateMaxAttempts(MaxAttempts);

    /// <summary>Failed attempts until a command is marked Failed; 1..20.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1..20.</exception>
    public int MaxAttempts
    {
        get => maxAttempts;
        init => maxAttempts = ValidateMaxAttempts(value);
    }

    private static int ValidateMaxAttempts(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, MinMaxAttempts, nameof(MaxAttempts));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxMaxAttempts, nameof(MaxAttempts));
        return value;
    }
}
