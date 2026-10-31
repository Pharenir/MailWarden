using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>
/// <see cref="IProgress{T}"/> that records reports synchronously on the calling thread (unlike
/// <see cref="Progress{T}"/>, which posts to a synchronization context).
/// </summary>
internal sealed class RecordingProgress : IProgress<SyncProgress>
{
    public List<SyncProgress> Reports { get; } = [];

    public void Report(SyncProgress value) => Reports.Add(value);
}
