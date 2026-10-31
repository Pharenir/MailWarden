namespace MailWarden.Core.Sync;

/// <summary>
/// One authenticated IMAP session, read-only towards the server. Not thread-safe; one open folder at a time.
/// Failures surface as <see cref="MailTransportException"/>; cancellation as <see cref="OperationCanceledException"/>.
/// </summary>
public interface IMailTransport : IAsyncDisposable
{
    /// <summary>Lists all folders of the account, including NoSelect/NonExistent containers (the caller filters).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All folders in server order.</returns>
    Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct = default);

    /// <summary>Opens the folder read-only (EXAMINE) and closes the previously open one.</summary>
    /// <param name="fullName">Full folder name as listed by <see cref="ListFoldersAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>State of the folder right after opening.</returns>
    Task<RemoteFolderState> OpenFolderAsync(string fullName, CancellationToken ct = default);

    /// <summary>Returns the UIDs of the open folder in ascending order.</summary>
    /// <param name="since">
    /// Null = all messages; otherwise only messages whose INTERNALDATE is on or after that calendar day (IMAP SINCE;
    /// the day is the date part of the value in its own offset).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UIDs ascending.</returns>
    Task<IReadOnlyList<uint>> SearchUidsAsync(DateTimeOffset? since, CancellationToken ct = default);

    /// <summary>Returns the system flags of messages in the open folder.</summary>
    /// <param name="changedSinceModSeq">0 = all messages; greater than 0 = only messages changed since (CONDSTORE).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UID and flags per message.</returns>
    Task<IReadOnlyList<RemoteFlags>> FetchFlagsAsync(ulong changedSinceModSeq, CancellationToken ct = default);

    /// <summary>Fetches the complete message without setting <c>\Seen</c> (BODY.PEEK[]).</summary>
    /// <param name="uid">UID in the open folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The message (the caller disposes its content), or null when the server returns nothing for the UID (gone or not delivered).</returns>
    Task<RemoteMessage?> FetchMessageAsync(uint uid, CancellationToken ct = default);
}
