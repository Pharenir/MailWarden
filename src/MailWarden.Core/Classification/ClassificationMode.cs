namespace MailWarden.Core.Classification;

/// <summary>
/// What a classification pass does with its decisions.
/// </summary>
public enum ClassificationMode
{
    /// <summary>Decisions are stored and nothing moves (default of a new install).</summary>
    RecordOnly = 1,

    /// <summary>Decisions are stored and messages are moved through the command queue.</summary>
    Act = 2,
}
