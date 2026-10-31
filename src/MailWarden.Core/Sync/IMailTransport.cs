namespace MailWarden.Core.Sync;

/// <summary>
/// One authenticated IMAP session, read-only towards the server. Not thread-safe; one open folder at a time.
/// Failures surface as <see cref="MailTransportException"/>; cancellation as <see cref="OperationCanceledException"/>.
/// </summary>
public interface IMailTransport : IAsyncDisposable
{
    /// <summary>
    /// True while the session is connected and authenticated; false after a lost connection or after dispose.
    /// </summary>
    bool IsConnected { get; }

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

    /// <summary>
    /// Fetches several messages of the open folder (metadata + BODY.PEEK[], never sets <c>\Seen</c>) with a bounded
    /// number of IMAP commands that does not grow with the number of UIDs (target: one FETCH for the metadata of all
    /// UIDs plus one for all bodies). Calls <paramref name="onMessage"/> once per delivered message, in ascending UID
    /// order, and awaits it before the next call. The callback owns <see cref="RemoteMessage.Content"/> and disposes it.
    /// The content is valid only until the task returned by the callback completes: the callback must consume it
    /// before that and must not keep it or hand it to other work. UIDs the server returns nothing for are skipped
    /// (no callback).
    /// </summary>
    /// <param name="uids">UIDs in the open folder.</param>
    /// <param name="onMessage">Called per delivered message with the message and the cancellation token.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes after the last callback returned.</returns>
    /// <exception cref="MailTransportException">
    /// Transport failure, as for the other methods; messages delivered before the failure were already passed to
    /// <paramref name="onMessage"/>.
    /// </exception>
    /// <remarks>An exception thrown by <paramref name="onMessage"/> propagates unchanged and ends the call.</remarks>
    Task FetchMessagesAsync(
        IReadOnlyList<uint> uids,
        Func<RemoteMessage, CancellationToken, Task> onMessage,
        CancellationToken ct = default);
}
