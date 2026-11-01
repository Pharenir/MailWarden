using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Composition;

namespace MailWarden.Presentation;

/// <summary>
/// What the user interface may ask of the background service. It only schedules cycles; a cycle executes nothing but
/// commands that are already in the queue.
/// </summary>
public interface IMailboxController
{
    /// <summary>Raised on a thread-pool thread whenever the status of an account changes.</summary>
    event EventHandler<AccountSyncStatus>? StatusChanged;

    /// <summary>Raised on a thread-pool thread after every successful classification pass; counts only.</summary>
    event EventHandler<ClassificationPassSummary>? ClassificationPassCompleted;

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
/// <see cref="IMailboxController"/> as a thin adapter over the <see cref="MailboxService"/> and the classification events of
/// the <see cref="MailWardenRuntime"/>.
/// </summary>
/// <param name="runtime">The opened runtime with the background service.</param>
public sealed class MailboxServiceController(MailWardenRuntime runtime) : IMailboxController
{
    private readonly MailWardenRuntime runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <inheritdoc />
    public event EventHandler<AccountSyncStatus>? StatusChanged
    {
        add => runtime.Service.StatusChanged += value;
        remove => runtime.Service.StatusChanged -= value;
    }

    /// <inheritdoc />
    public event EventHandler<ClassificationPassSummary>? ClassificationPassCompleted
    {
        add => runtime.ClassificationPassCompleted += value;
        remove => runtime.ClassificationPassCompleted -= value;
    }

    /// <inheritdoc />
    public IReadOnlyList<AccountSyncStatus> CurrentStatus => runtime.Service.CurrentStatus;

    /// <inheritdoc />
    public void TriggerAccount(Guid accountId) => runtime.Service.TriggerAccount(accountId);

    /// <inheritdoc />
    public void RetryAccount(Guid accountId) => runtime.Service.RetryAccount(accountId);
}
