using MailWarden.Core.Commands;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>
/// AP-04b fix.4: the queue empties a folder only for UIDs the caller confirmed. These helpers confirm exactly what is
/// stored locally right now (UIDVALIDITY of the folder, all local UIDs), which is what the old one-argument call did.
/// </summary>
internal static class EmptyFolderHelpers
{
    public static async Task<MailCommand?> EnqueueEmptyConfirmedAsync(
        this CommandQueueFixture fixture, long folderId, CancellationToken ct = default) =>
        await EnqueueAsync(fixture.Queue, fixture.Storage, folderId, ct);

    public static async Task<MailCommand?> EnqueueEmptyConfirmedAsync(
        this CommandHarness harness, long folderId, CancellationToken ct = default) =>
        await EnqueueAsync(harness.Queue, harness.Storage, folderId, ct);

    private static async Task<MailCommand?> EnqueueAsync(IMailCommandQueue queue, StorageFixture storage, long folderId, CancellationToken ct)
    {
        MailFolder? folder = await storage.Folders.GetAsync(folderId, ct);
        IReadOnlyDictionary<uint, MessageFlags> local = await storage.Messages.GetUidFlagsAsync(folderId, ct);
        return await queue.EnqueueEmptyFolderAsync(folderId, folder?.UidValidity ?? 0, [.. local.Keys], ct);
    }
}
