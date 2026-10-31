using MailWarden.Core.Accounts;
using MailWarden.Core.Model;

namespace MailWarden.Core.Sync;

/// <summary>
/// Opens inbox watchers (<see cref="IMailIdleWatcher"/>) for accounts.
/// </summary>
public interface IMailIdleWatcherFactory
{
    /// <summary>Connects (silent token), opens the inbox read-only (EXAMINE).</summary>
    /// <param name="account">Account whose inbox is watched.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open watcher; the caller disposes it.</returns>
    /// <exception cref="AuthenticationFailedException">The token could not be obtained (e.g. a sign-in is needed).</exception>
    /// <exception cref="MailTransportException">Connecting, logging in or opening the inbox failed.</exception>
    /// <exception cref="NotSupportedException">The account type cannot be watched (e.g. generic IMAP).</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    Task<IMailIdleWatcher> ConnectAsync(Account account, CancellationToken ct = default);
}
