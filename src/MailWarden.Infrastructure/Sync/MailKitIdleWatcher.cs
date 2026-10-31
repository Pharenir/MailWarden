using MailKit;
using MailKit.Net.Imap;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Providers;

namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// <see cref="IMailIdleWatcher"/> on an authenticated MailKit <see cref="ImapClient"/> whose inbox is open read-only
/// (EXAMINE). A wait is one <c>IDLE</c> command (<see cref="ImapClient.IdleAsync"/>); it ends with <c>DONE</c> when the
/// inbox raises <see cref="IMailFolder.CountChanged"/> (EXISTS/EXPUNGE), <see cref="IMailFolder.MessageExpunged"/>
/// or <see cref="IMailFolder.MessageFlagsChanged"/> (FETCH FLAGS), or when the maximum wait elapsed. No other command
/// than IDLE/DONE is sent on this connection, so it never changes the server state.
/// </summary>
internal sealed class MailKitIdleWatcher : IMailIdleWatcher
{
    // After the done token fired (change, maxWait or stop) DONE is sent first; the IDLE command is aborted only when the
    // server does not answer in time (MailKit: cancel the done token before the cancellation token).
    private static readonly TimeSpan DoneGracePeriod = TimeSpan.FromSeconds(10);

    private readonly ImapClient client;
    private readonly IMailFolder inbox;
    private readonly ServerEndpoint endpoint;

    // Done token of the running IDLE; null outside a wait. Read by the folder events (MailKit's reader thread).
    private CancellationTokenSource? currentDone;

    // 1 when the inbox reported a change that no wait returned yet (also a change that arrives after DONE).
    private int changePending;
    private bool isDisposed;

    /// <summary>Wraps a connected client with the opened inbox; the watcher owns and disposes the client.</summary>
    /// <param name="client">Authenticated client.</param>
    /// <param name="inbox">Inbox, already opened read-only. Events raised while opening are not counted.</param>
    /// <param name="endpoint">Endpoint the client is connected to (used in error messages only).</param>
    internal MailKitIdleWatcher(ImapClient client, IMailFolder inbox, ServerEndpoint endpoint)
    {
        this.client = client;
        this.inbox = inbox;
        this.endpoint = endpoint;
        inbox.CountChanged += OnInboxChanged;
        inbox.MessageExpunged += OnInboxChanged;
        inbox.MessageFlagsChanged += OnInboxChanged;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxWait"/> is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The watcher is disposed.</exception>
    public async Task<bool> WaitForChangeAsync(TimeSpan maxWait, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxWait, TimeSpan.Zero);
        ObjectDisposedException.ThrowIf(isDisposed, this);
        ct.ThrowIfCancellationRequested();
        if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
        {
            throw new NotSupportedException("The IMAP server does not support the IDLE extension.");
        }

        // A change reported while the previous IDLE was ending (between DONE and its tagged OK) is returned now.
        if (Interlocked.Exchange(ref changePending, 0) == 1)
        {
            return true;
        }

        // maxWait elapses through the done token: DONE is sent and IdleAsync returns normally. MailKit reads without
        // timeout while idling (ReadTimeout -1), so the answer to DONE is bounded by the abort token: a dead connection
        // ends as a Network failure instead of hanging.
        using var doneSource = new CancellationTokenSource(maxWait);
        using var abortSource = new CancellationTokenSource();
        Volatile.Write(ref currentDone, doneSource);
        try
        {
            using CancellationTokenRegistration doneRegistration =
                doneSource.Token.Register(() => abortSource.CancelAfter(DoneGracePeriod));
            using CancellationTokenRegistration stopRegistration = ct.Register(doneSource.Cancel);
            await MailKitTransport
                .RunAsync(
                    endpoint,
                    "idle",
                    async () =>
                    {
                        await client.IdleAsync(doneSource.Token, abortSource.Token).ConfigureAwait(false);
                        return true;
                    },
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref currentDone, null);
        }

        ct.ThrowIfCancellationRequested();
        return Interlocked.Exchange(ref changePending, 0) == 1;
    }

    /// <summary>Logs out (best effort, short timeout) and releases the connection. Safe to call more than once.</summary>
    /// <returns>A task that completes when the client is disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        inbox.CountChanged -= OnInboxChanged;
        inbox.MessageExpunged -= OnInboxChanged;
        inbox.MessageFlagsChanged -= OnInboxChanged;
        await MailKitTransport.DisconnectQuietlyAsync(client).ConfigureAwait(false);
    }

    // Raised by MailKit on the thread that reads the IDLE response; ends the running IDLE like MailKit's IDLE example.
    private void OnInboxChanged(object? sender, EventArgs eventArgs)
    {
        Interlocked.Exchange(ref changePending, 1);
        Volatile.Read(ref currentDone)?.Cancel();
    }
}
