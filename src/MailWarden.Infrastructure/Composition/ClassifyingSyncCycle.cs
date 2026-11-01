using MailWarden.Infrastructure.Classification;
using MailWarden.Infrastructure.Logging;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// The sync step of an account cycle in the running app: one sync pass, one contact harvest over what it stored, then one
/// classification pass. The order counts: a contact collected in this cycle already counts for this cycle's classification.
/// <see cref="RunAsync"/> is the <c>syncAccount</c> function of the <see cref="Background.MailboxService"/>, which turns an
/// exception from here into its error state with backoff.
/// </summary>
/// <remarks>Log lines hold type names and counts only: exception messages can quote message or account data.</remarks>
public sealed class ClassifyingSyncCycle
{
    private const string RunSource = "ClassifyingSyncCycle.RunAsync";

    private readonly Func<Guid, IProgress<SyncProgress>?, CancellationToken, Task<SyncReport>> sync;
    private readonly Func<Guid, CancellationToken, Task<ContactHarvestReport>> harvest;
    private readonly Func<Guid, CancellationToken, Task<ClassificationPassReport>> classify;
    private readonly Action<Guid> triggerAccount;
    private readonly Action<ClassificationPassSummary> reportSummary;
    private readonly ICriticalLog log;

    /// <summary>Creates the cycle; nothing runs before <see cref="RunAsync"/>.</summary>
    /// <param name="sync">Runs one sync pass of an account.</param>
    /// <param name="harvest">Runs one contact harvest of an account.</param>
    /// <param name="classify">Runs one classification pass of an account.</param>
    /// <param name="triggerAccount">Schedules another cycle of an account soon, so that new commands do not wait for the
    /// poll interval.</param>
    /// <param name="reportSummary">Receives the summary of every successful pass.</param>
    /// <param name="log">Critical log.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public ClassifyingSyncCycle(
        Func<Guid, IProgress<SyncProgress>?, CancellationToken, Task<SyncReport>> sync,
        Func<Guid, CancellationToken, Task<ContactHarvestReport>> harvest,
        Func<Guid, CancellationToken, Task<ClassificationPassReport>> classify,
        Action<Guid> triggerAccount,
        Action<ClassificationPassSummary> reportSummary,
        ICriticalLog log)
    {
        this.sync = sync ?? throw new ArgumentNullException(nameof(sync));
        this.harvest = harvest ?? throw new ArgumentNullException(nameof(harvest));
        this.classify = classify ?? throw new ArgumentNullException(nameof(classify));
        this.triggerAccount = triggerAccount ?? throw new ArgumentNullException(nameof(triggerAccount));
        this.reportSummary = reportSummary ?? throw new ArgumentNullException(nameof(reportSummary));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Runs the sync pass and, when it succeeded, the contact harvest and then the classification pass of the account.</summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="progress">Receives the progress of the sync pass.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The report of the sync pass.</returns>
    /// <exception cref="OperationCanceledException">The cycle was cancelled; without a cancelled <paramref name="ct"/> this
    /// counts as a failure of the step that threw it.</exception>
    /// <exception cref="Exception">Whatever the sync, the harvest or the classification pass throws; a failed harvest or
    /// classification pass is logged first, and a failed harvest means no classification pass in this cycle.</exception>
    public async Task<SyncReport> RunAsync(Guid accountId, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        SyncReport syncReport = await sync(accountId, progress, ct).ConfigureAwait(false);
        await RunLoggedAsync(harvest, accountId, "Contact harvest failed", ct).ConfigureAwait(false);
        ClassificationPassReport passReport = await RunLoggedAsync(classify, accountId, "Classification pass failed", ct).ConfigureAwait(false);

        // Only commands that are really stored are worth a cycle; rejected or held mail would just cause an empty one.
        if (passReport.MovesEnqueued + passReport.RetentionDeletesEnqueued > 0)
        {
            triggerAccount(accountId);
        }

        ReportSummary(accountId, passReport);
        LogProblemCounts(passReport);
        return syncReport;
    }

    // Fulfils "error in the log" of harvest and classifier: the service only shows the type name in the account's status. Only a
    // cancellation of this cycle's token is a stop, like in the service; any other one is a failure, e.g. a timeout while
    // getting a token.
    private async Task<TReport> RunLoggedAsync<TReport>(
        Func<Guid, CancellationToken, Task<TReport>> step,
        Guid accountId,
        string failureText,
        CancellationToken ct)
    {
        try
        {
            return await step(accountId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log.Write(CriticalLogLevel.Critical, RunSource, $"{failureText}: {exception.GetType().Name}");
            throw;
        }
    }

    // The pass is done and stored at this point, so a faulty receiver must not turn it into a failed cycle.
    private void ReportSummary(Guid accountId, ClassificationPassReport passReport)
    {
        var summary = new ClassificationPassSummary(
            accountId,
            passReport.EvaluationFailed,
            passReport.ReadFailed,
            passReport.BrakeHeld,
            passReport.ActSuspendedNoContacts);
        try
        {
            reportSummary(summary);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log.Write(CriticalLogLevel.Critical, RunSource, $"Reporting the classification summary failed: {exception.GetType().Name}");
        }
    }

    private void LogProblemCounts(ClassificationPassReport passReport)
    {
        if (passReport.EvaluationFailed == 0 && passReport.BrakeHeld == 0 && passReport.ReadFailed == 0)
        {
            return;
        }

        string readFailedPart = passReport.ReadFailed > 0 ? $", read failed {passReport.ReadFailed}" : string.Empty;
        log.Write(
            CriticalLogLevel.Critical,
            RunSource,
            $"Classification pass counts: evaluation failed {passReport.EvaluationFailed}, brake held {passReport.BrakeHeld}{readFailedPart}");
    }
}
