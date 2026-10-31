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
