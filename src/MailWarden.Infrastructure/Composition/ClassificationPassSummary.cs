namespace MailWarden.Infrastructure.Composition;

/// <summary>
/// Result of one classification pass for the user interface; counts only, never message data.
/// </summary>
/// <param name="AccountId">Account of the pass.</param>
/// <param name="EvaluationFailed">Messages whose evaluation failed in this pass.</param>
/// <param name="ReadFailed">Messages that could not be read in this pass.</param>
/// <param name="BrakeHeld">Messages the brake held in this pass.</param>
/// <param name="ActSuspendedNoContacts">True when the policy says Act but the address book is empty.</param>
public sealed record ClassificationPassSummary(
    Guid AccountId,
    int EvaluationFailed,
    int ReadFailed,
    int BrakeHeld,
    bool ActSuspendedNoContacts);
