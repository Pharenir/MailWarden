namespace MailWarden.Infrastructure.Logging;

/// <summary>
/// Log for critical failures only. Callers pass exception type names and fixed texts: never
/// <see cref="Exception.Message"/>, <see cref="object.ToString"/> of an exception, or mail or account data.
/// </summary>
public interface ICriticalLog
{
    /// <summary>Writes one entry; never throws.</summary>
    /// <param name="level">Level, e.g. <see cref="CriticalLogLevel.Critical"/>.</param>
    /// <param name="source">Origin as <c>module.function</c>.</param>
    /// <param name="message">Fixed text, optionally with an exception type name.</param>
    void Write(string level, string source, string message);
}
