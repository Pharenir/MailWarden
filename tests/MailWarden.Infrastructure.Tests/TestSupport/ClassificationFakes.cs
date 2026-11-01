using MailWarden.Core.Classification;
using MailWarden.Core.Commands;
using MailWarden.Core.Sync;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>
/// Fake <see cref="IMailFolderProvisioner"/>: counts calls and, like the real one, stores the top-level folders
/// "Screener" and "Quarantine" locally when they are missing. Can throw instead.
/// </summary>
internal sealed class FakeFolderProvisioner(IFolderStore folders) : IMailFolderProvisioner
{
    public const uint ScreenerUidValidity = 3000;

    public const uint QuarantineUidValidity = 4000;

    public int Calls { get; private set; }

    /// <summary>Thrown by every call when set (nothing is stored then).</summary>
    public Exception? ThrowOnCall { get; set; }

    /// <summary>When false, a call stores nothing (the server could not create the folders).</summary>
    public bool CreateFolders { get; set; } = true;

    /// <summary>Invoked at the start of every call, before anything is stored.</summary>
    public Func<Task>? OnCall { get; set; }

    public async Task EnsureAppFoldersAsync(Account account, CancellationToken ct = default)
    {
        Calls++;
        if (OnCall is not null)
        {
            await OnCall();
        }

        if (ThrowOnCall is not null)
        {
            throw ThrowOnCall;
        }

        if (!CreateFolders)
        {
            return;
        }

        IReadOnlyList<MailFolder> existing = await folders.ListAsync(account.Id, ct);
        if (!existing.Any(folder => folder.Role == FolderRole.Screener))
        {
            await folders.UpsertAsync(new MailFolder(0, account.Id, "Screener", FolderRole.Screener, ScreenerUidValidity), ct);
        }

        if (!existing.Any(folder => folder.Role == FolderRole.Quarantine))
        {
            await folders.UpsertAsync(new MailFolder(0, account.Id, "Quarantine", FolderRole.Quarantine, QuarantineUidValidity), ct);
        }
    }
}

/// <summary>
/// <see cref="IMailCommandQueue"/> decorator: delegates to the real queue, but rejects moves to the configured target
/// folders with the configured exception (which carries a marker text that must never reach a log entry).
/// </summary>
internal sealed class RejectingMoveQueue(IMailCommandQueue inner) : IMailCommandQueue
{
    public const string RejectionMarker = "marker_queue_rejection_text";

    /// <summary>Target folder ids whose moves are rejected.</summary>
    public HashSet<long> RejectedTargets { get; } = [];

    public Exception RejectWith { get; set; } = new ArgumentException(RejectionMarker);

    /// <summary>When true, every permanent delete is rejected with <see cref="RejectWith"/>.</summary>
    public bool RejectDeletes { get; set; }

    /// <summary>Every move attempt: target and UID count (also rejected ones).</summary>
    public List<(long Target, int UidCount)> MoveAttempts { get; } = [];

    public Task<MailCommand> EnqueueMoveAsync(
        long folderId,
        uint expectedUidValidity,
        IReadOnlyCollection<uint> uids,
        long targetFolderId,
        CancellationToken ct = default)
    {
        MoveAttempts.Add((targetFolderId, uids.Count));
        if (RejectedTargets.Contains(targetFolderId))
        {
            throw RejectWith;
        }

        return inner.EnqueueMoveAsync(folderId, expectedUidValidity, uids, targetFolderId, ct);
    }

    public Task<MailCommand> EnqueueSetFlagsAsync(
        long folderId,
        uint expectedUidValidity,
        IReadOnlyCollection<uint> uids,
        MessageFlags flagsToAdd,
        MessageFlags flagsToRemove,
        CancellationToken ct = default) =>
        inner.EnqueueSetFlagsAsync(folderId, expectedUidValidity, uids, flagsToAdd, flagsToRemove, ct);

    public Task<MailCommand> EnqueueMoveToTrashAsync(
        long folderId,
        uint expectedUidValidity,
        IReadOnlyCollection<uint> uids,
        CancellationToken ct = default) =>
        inner.EnqueueMoveToTrashAsync(folderId, expectedUidValidity, uids, ct);

    public Task<MailCommand> EnqueueDeletePermanentlyAsync(
        long folderId,
        uint expectedUidValidity,
        IReadOnlyCollection<uint> uids,
        CancellationToken ct = default)
    {
        if (RejectDeletes)
        {
            throw RejectWith;
        }

        return inner.EnqueueDeletePermanentlyAsync(folderId, expectedUidValidity, uids, ct);
    }

    public Task<MailCommand?> EnqueueEmptyFolderAsync(long folderId, uint confirmedUidValidity, IReadOnlyCollection<uint> confirmedUids, CancellationToken ct = default) =>
        inner.EnqueueEmptyFolderAsync(folderId, confirmedUidValidity, confirmedUids, ct);

    public Task<IReadOnlyList<MailCommand>> ListAsync(Guid accountId, CancellationToken ct = default) =>
        inner.ListAsync(accountId, ct);

    public Task DiscardAsync(long commandId, CancellationToken ct = default) => inner.DiscardAsync(commandId, ct);
}

/// <summary>
/// <see cref="IEmlStore"/> decorator for reads: <see cref="OnOpen"/> may return an exception to throw for a path. Used
/// to inject unreadable files (with a marker text) and cancellation.
/// </summary>
internal sealed class ThrowingEmlStore(IEmlStore inner) : IEmlStore
{
    public const string FailureMarker = "marker_eml_failure_text";

    /// <summary>Returns the exception to throw when the path is opened, or null to delegate.</summary>
    public Func<string, Exception?>? OnOpen { get; set; }

    public string RelativePathFor(Guid accountId, long folderId, uint uid) => inner.RelativePathFor(accountId, folderId, uid);

    public Task WriteAsync(string relativePath, Stream content, CancellationToken ct = default) =>
        inner.WriteAsync(relativePath, content, ct);

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
    {
        Exception? failure = OnOpen?.Invoke(relativePath);
        if (failure is not null)
        {
            throw failure;
        }

        return inner.OpenReadAsync(relativePath, ct);
    }

    public bool Exists(string relativePath) => inner.Exists(relativePath);

    public Task DeleteAsync(string relativePath, CancellationToken ct = default) => inner.DeleteAsync(relativePath, ct);
}

/// <summary>
/// <see cref="IMailCommandQueue"/> decorator that lists every command as permanently failed (the real executor is not
/// part of the classification tests); all other calls go to the real queue.
/// </summary>
internal sealed class FailedStatusQueue(IMailCommandQueue inner) : IMailCommandQueue
{
    public Task<MailCommand> EnqueueSetFlagsAsync(long folderId, uint expectedUidValidity, IReadOnlyCollection<uint> uids, MessageFlags flagsToAdd, MessageFlags flagsToRemove, CancellationToken ct = default) =>
        inner.EnqueueSetFlagsAsync(folderId, expectedUidValidity, uids, flagsToAdd, flagsToRemove, ct);

    public Task<MailCommand> EnqueueMoveAsync(long folderId, uint expectedUidValidity, IReadOnlyCollection<uint> uids, long targetFolderId, CancellationToken ct = default) =>
        inner.EnqueueMoveAsync(folderId, expectedUidValidity, uids, targetFolderId, ct);

    public Task<MailCommand> EnqueueMoveToTrashAsync(long folderId, uint expectedUidValidity, IReadOnlyCollection<uint> uids, CancellationToken ct = default) =>
        inner.EnqueueMoveToTrashAsync(folderId, expectedUidValidity, uids, ct);

    public Task<MailCommand> EnqueueDeletePermanentlyAsync(long folderId, uint expectedUidValidity, IReadOnlyCollection<uint> uids, CancellationToken ct = default) =>
        inner.EnqueueDeletePermanentlyAsync(folderId, expectedUidValidity, uids, ct);

    public Task<MailCommand?> EnqueueEmptyFolderAsync(long folderId, uint confirmedUidValidity, IReadOnlyCollection<uint> confirmedUids, CancellationToken ct = default) =>
        inner.EnqueueEmptyFolderAsync(folderId, confirmedUidValidity, confirmedUids, ct);

    public async Task<IReadOnlyList<MailCommand>> ListAsync(Guid accountId, CancellationToken ct = default) =>
        (await inner.ListAsync(accountId, ct)).Select(command => command with { Status = MailCommandStatus.Failed }).ToList();

    public Task DiscardAsync(long commandId, CancellationToken ct = default) => inner.DiscardAsync(commandId, ct);
}

/// <summary>A readable stream whose reads fail with <see cref="IOException"/>.</summary>
internal sealed class ThrowingStream : Stream
{
    public const string FailureMarker = "marker_stream_failure_text";

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new IOException(FailureMarker);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        throw new IOException(FailureMarker);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw new IOException(FailureMarker);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A signal that always fails with an exception carrying <see cref="FailureMarker"/>.</summary>
internal sealed class ThrowingSignal : ISpamSignal
{
    public const string FailureMarker = "marker_signal_failure_text";

    public string Id => "throwing-signal";

    public SignalResult? Evaluate(ClassificationInput input, ClassificationContext context) =>
        throw new InvalidOperationException(FailureMarker);
}
