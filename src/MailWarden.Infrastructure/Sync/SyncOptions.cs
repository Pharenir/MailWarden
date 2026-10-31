namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// Settings of the sync engine.
/// </summary>
/// <param name="InitialSyncDays">
/// Window of the first sync of a folder in days (local calendar days back from today); 0 = everything (default).
/// Applies only to the first completed sync of a folder: it fixes the folder's floor
/// (<see cref="Core.Sync.FolderSyncState.SkippedUpToUid"/>, lowest UID inside the window − 1, or the highest server UID
/// when nothing lies inside). Later passes fetch every missing UID above that floor, so a message outside the window
/// whose UID is above the floor (e.g. moved into the folder later with an old date) is fetched from the second pass on.
/// </param>
public sealed record SyncOptions(int InitialSyncDays = 0)
{
    private const int MinFetchBatchSize = 1;
    private const int MaxFetchBatchSize = 500;
    private const int MaxReconnectsPerPassLimit = 10;

    private readonly int initialSyncDays = ValidateInitialSyncDays(InitialSyncDays);
    private readonly int fetchBatchSize = 50;
    private readonly int maxReconnectsPerPass = 3;

    /// <summary>Window of the first sync of a folder in days; 0 = everything; never negative.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int InitialSyncDays
    {
        get => initialSyncDays;
        init => initialSyncDays = ValidateInitialSyncDays(value);
    }

    /// <summary>
    /// Number of messages fetched with one batch (one metadata FETCH plus one body FETCH); 1..500, default 50.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1..500.</exception>
    public int FetchBatchSize
    {
        get => fetchBatchSize;
        init => fetchBatchSize = ValidateRange(value, MinFetchBatchSize, MaxFetchBatchSize, nameof(FetchBatchSize));
    }

    /// <summary>
    /// How often one pass may replace a lost connection with a new one; 0..10, default 3. One more lost connection
    /// aborts the pass.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0..10.</exception>
    public int MaxReconnectsPerPass
    {
        get => maxReconnectsPerPass;
        init => maxReconnectsPerPass = ValidateRange(value, 0, MaxReconnectsPerPassLimit, nameof(MaxReconnectsPerPass));
    }

    private static int ValidateInitialSyncDays(int initialSyncDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialSyncDays, nameof(InitialSyncDays));
        return initialSyncDays;
    }

    private static int ValidateRange(int value, int minimum, int maximum, string propertyName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, minimum, propertyName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, maximum, propertyName);
        return value;
    }
}
