namespace MailWarden.Infrastructure.Commands;

/// <summary>
/// Result of one executor run over the due commands of an account.
/// </summary>
/// <param name="Completed">Commands the server confirmed (removed from the queue), including those whose local effect failed.</param>
/// <param name="Retrying">Commands that failed and stay Pending for a later attempt.</param>
/// <param name="Failed">Commands that failed permanently (Status Failed).</param>
public sealed record CommandRunReport(int Completed, int Retrying, int Failed);
