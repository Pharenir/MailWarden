using MailWarden.Core.Accounts;
using MailWarden.Core.Model;

namespace MailWarden.Core.Sync;

/// <summary>
/// Opens authenticated mail transport sessions for accounts.
/// </summary>
public interface IMailTransportFactory
{
    /// <summary>Connects and authenticates a new session for the account.</summary>
    /// <param name="account">Account to connect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open session; the caller disposes it.</returns>
    /// <exception cref="AuthenticationFailedException">The token could not be obtained (e.g. a sign-in is needed).</exception>
    /// <exception cref="MailTransportException">Connecting or logging in failed.</exception>
    Task<IMailTransport> ConnectAsync(Account account, CancellationToken ct = default);

    /// <summary>
    /// Connects and authenticates a new session for server changes (command queue), separate from the read-only sync
    /// session.
    /// </summary>
    /// <param name="account">Account to connect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open session; the caller disposes it.</returns>
    /// <exception cref="AuthenticationFailedException">The token could not be obtained (e.g. a sign-in is needed).</exception>
    /// <exception cref="MailTransportException">Connecting or logging in failed.</exception>
    Task<IMailCommandTransport> ConnectForCommandsAsync(Account account, CancellationToken ct = default);
}
