using MailWarden.Core.Classification;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// What <see cref="ClassificationInputReader.ReadMessageAsync"/> found in an EML.
/// </summary>
/// <param name="Parsed">False when the bytes are no message; the other members are null then.</param>
/// <param name="TopReceived">Raw value of the top-most Received header (unfolded, never decoded), or null when there is
/// none. Part of the message key.</param>
/// <param name="Input">Classifier input, or null when the message has no single usable From address.</param>
public sealed record ClassificationMessage(bool Parsed, string? TopReceived, ClassificationInput? Input);
