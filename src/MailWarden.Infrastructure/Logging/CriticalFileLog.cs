using System.Globalization;
using System.Text;

namespace MailWarden.Infrastructure.Logging;

/// <summary>
/// <see cref="ICriticalLog"/> that appends one line per entry to <c>&lt;logDirectory&gt;\mailwarden.log</c> in the format
/// <c>ISO8601 | LEVEL | source | message</c>. CR and LF in any field become a space. Safe across threads. A write failure
/// is swallowed: logging must never be the reason for a further failure.
/// </summary>
public sealed class CriticalFileLog : ICriticalLog
{
    /// <summary>Name of the log file below the log directory.</summary>
    public const string FileName = "mailwarden.log";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string logDirectory;
    private readonly TimeProvider timeProvider;
    private readonly Lock writeGate = new();

    /// <summary>Creates the log. Does not touch the file system; the directory is created on the first write.</summary>
    /// <param name="logDirectory">Directory of the log file.</param>
    /// <param name="timeProvider">Clock for the time stamps.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public CriticalFileLog(string logDirectory, TimeProvider timeProvider)
    {
        this.logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public void Write(string level, string source, string message)
    {
        try
        {
            string timestamp = timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            string line = $"{timestamp} | {ToSingleLine(level)} | {ToSingleLine(source)} | {ToSingleLine(message)}{Environment.NewLine}";
            lock (writeGate)
            {
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(Path.Combine(logDirectory, FileName), line, Utf8WithoutBom);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ignored on purpose, see class comment.
        }
    }

    private static string ToSingleLine(string? field)
    {
        return (field ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
    }
}
