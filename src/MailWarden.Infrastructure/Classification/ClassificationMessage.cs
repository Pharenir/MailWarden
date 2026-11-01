using MailWarden.Core.Classification;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// What <see cref="ClassificationInputReader.ReadMessageAsync"/> found in an EML.
/// </summary>
/// <param name="Parsed">False when the bytes are no message; the other members are null then.</param>
/// <param name="TopReceived">Raw value of the top-most Received header (unfolded, never decoded), or null when there is
/// none. Part of the message key.</param>
/// <param name="Input">Classifier input, or null when the message has no single usable From address or building the
/// input failed (see <see cref="InputFailed"/>).</param>
public sealed record ClassificationMessage(bool Parsed, string? TopReceived, ClassificationInput? Input)
{
    /// <summary>True when the message parsed but building <see cref="Input"/> threw, as opposed to having no usable
    /// sender. The two must stay apart: the first moves nothing, the second goes to Junk.</summary>
    public bool InputFailed { get; init; }
}
