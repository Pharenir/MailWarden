namespace MailWarden.Core.Classification;

/// <summary>
/// One finding of a spam signal. Points may be negative (evidence for legitimacy).
/// </summary>
/// <param name="SignalId">Id of the signal that produced the finding.</param>
/// <param name="Points">Contribution to the spam score.</param>
/// <param name="Reason">One English sentence naming the concrete values that were judged
/// (e.g. "Sender domain label 'qzxkvb73' looks random (score 0.91)").</param>
public sealed record SignalResult(string SignalId, int Points, string Reason);
