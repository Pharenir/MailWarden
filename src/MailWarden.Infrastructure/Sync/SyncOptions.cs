namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// Settings of the sync engine.
/// </summary>
/// <param name="InitialSyncDays">
/// Window of the first sync of a folder in days (local calendar days back from today); 0 = everything.
/// </param>
public sealed record SyncOptions(int InitialSyncDays = 90)
{
    private readonly int initialSyncDays = Validate(InitialSyncDays);

    /// <summary>Window of the first sync of a folder in days; 0 = everything; never negative.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int InitialSyncDays
    {
        get => initialSyncDays;
        init => initialSyncDays = Validate(value);
    }

    private static int Validate(int initialSyncDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialSyncDays, nameof(InitialSyncDays));
        return initialSyncDays;
    }
}
