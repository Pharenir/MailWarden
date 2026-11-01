using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Classification;
using MailWarden.Infrastructure.Commands;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// The steps of an account cycle that talk to a server, replaceable for
/// <see cref="MailWardenRuntime.OpenWithCycleStepsAsync"/>, so that the composition can be checked without a server. The
/// app itself never uses this: it calls <see cref="MailWardenRuntime.OpenAsync"/>.
/// </summary>
/// <param name="Sync">Replaces the sync pass.</param>
/// <param name="IdleWatchers">Replaces the inbox watchers.</param>
/// <param name="Classify">Replaces the classification pass; null keeps the real one, on the real database with the policy
/// and settings of the data root. It only needs a server in Act when an app folder is missing locally.</param>
/// <param name="ExecuteCommands">Replaces the command run; null keeps the real one, which connects only when a command is
/// due.</param>
public sealed record AccountCycleSteps(
    Func<Guid, IProgress<SyncProgress>?, CancellationToken, Task<SyncReport>> Sync,
    IMailIdleWatcherFactory IdleWatchers,
    Func<Guid, CancellationToken, Task<ClassificationPassReport>>? Classify = null,
    Func<Guid, CancellationToken, Task<CommandRunReport>>? ExecuteCommands = null);
