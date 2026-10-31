namespace MailWarden.Core.Classification;

/// <summary>
/// One spam signal. Every finding carries points and a reason; there are no silent points.
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
