using MailWarden.Infrastructure.Background;

namespace MailWarden.Presentation;

/// <summary>
/// What the user interface may ask of the background service. It only schedules cycles; a cycle executes nothing but
/// commands that are already in the queue.
/// </summary>
public interface IMailboxController
{
    /// <summary>Raised on a thread-pool thread whenever the status of an account changes.</summary>
    event EventHandler<AccountSyncStatus>? StatusChanged;

    /// <summary>Snapshot of the status of every account the service has run.</summary>
    IReadOnlyList<AccountSyncStatus> CurrentStatus { get; }

    /// <summary>Schedules a cycle for the account soon.</summary>
    /// <param name="accountId">Account id.</param>
    void TriggerAccount(Guid accountId);

    /// <summary>Ends the error or sign-in wait of the account and schedules a cycle soon.</summary>
    /// <param name="accountId">Account id.</param>
    void RetryAccount(Guid accountId);
}

/// <summary>
/// <see cref="IMailboxController"/> as a thin adapter over the <see cref="MailboxService"/>.
/// </summary>
/// <param name="service">The background service.</param>
public sealed class MailboxServiceController(MailboxService service) : IMailboxController
{
    private readonly MailboxService service = service ?? throw new ArgumentNullException(nameof(service));

    /// <inheritdoc />
    public event EventHandler<AccountSyncStatus>? StatusChanged
    {
        add => service.StatusChanged += value;
        remove => service.StatusChanged -= value;
    }

    /// <inheritdoc />
    public IReadOnlyList<AccountSyncStatus> CurrentStatus => service.CurrentStatus;

    /// <inheritdoc />
    public void TriggerAccount(Guid accountId) => service.TriggerAccount(accountId);

    /// <inheritdoc />
    public void RetryAccount(Guid accountId) => service.RetryAccount(accountId);
}
