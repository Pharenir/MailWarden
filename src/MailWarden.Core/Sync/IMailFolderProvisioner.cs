using MailWarden.Core.Accounts;
using MailWarden.Core.Model;

namespace MailWarden.Core.Sync;

/// <summary>
/// Creates the folders the app owns on the server. Separate from <see cref="IMailCommandTransport"/>: it issues only
/// CREATE, SUBSCRIBE for a folder it just created, and read-only commands; no SELECT for writing and no EXPUNGE.
/// </summary>
public interface IMailFolderProvisioner
{
    /// <summary>
    /// Connects, lists the folders, creates the top-level folders "Screener" and "Quarantine" when missing, reads their
    /// UIDVALIDITY and stores them locally with their roles, so the next command cycle can use them before the next sync.
    /// An existing server folder of that name is reused, never recreated. A stored UIDVALIDITY other than 0 is never
    /// overwritten: the sync needs it to detect a reset.
    /// </summary>
    /// <param name="account">Account to provision.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when both folders are stored locally.</returns>
    /// <exception cref="MailTransportException">The server could not be reached or rejected a command, or a server folder of
    /// that name does not resolve to the expected role.</exception>
    /// <exception cref="AuthenticationFailedException">No token could be obtained (e.g. a sign-in is needed).</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    Task EnsureAppFoldersAsync(Account account, CancellationToken ct = default);
}
