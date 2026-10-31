namespace MailWarden.Core.Classification;

/// <summary>
/// One spam signal. Every finding carries points and a reason; there are no silent points.
/// <see cref="SpamClassifier.Classify"/> enforces this: a returned <see cref="SignalResult"/> must have
/// <see cref="SignalResult.SignalId"/> equal to <see cref="Id"/> (ordinal) and a non-empty, non-whitespace
/// <see cref="SignalResult.Reason"/>, otherwise <c>Classify</c> throws <see cref="InvalidOperationException"/>
/// naming the signal id.
/// </summary>
public interface ISpamSignal
{
    /// <summary>Stable identifier (e.g. "random-domain"); unique within one classifier.</summary>
    string Id { get; }

    /// <summary>Evaluates the signal for one message. Must not throw for any well-formed input record.</summary>
    /// <param name="input">Extracted message data.</param>
    /// <param name="context">Values precomputed once per message.</param>
    /// <returns>The finding, or null when the signal does not apply / has no finding.</returns>
    SignalResult? Evaluate(ClassificationInput input, ClassificationContext context);
}
