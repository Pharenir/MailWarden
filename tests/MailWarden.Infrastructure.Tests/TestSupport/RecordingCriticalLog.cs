using MailWarden.Infrastructure.Logging;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>Records every critical-log line; thread-safe.</summary>
internal sealed class RecordingCriticalLog : ICriticalLog
{
    private readonly object _gate = new();
    private readonly List<(string Level, string Source, string Message)> _entries = [];

    public IReadOnlyList<(string Level, string Source, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>All fields of all entries as one text, for "must never contain" checks.</summary>
    public string AllText
    {
        get
        {
            lock (_gate)
            {
                return string.Join("\n", _entries.Select(entry => $"{entry.Level}|{entry.Source}|{entry.Message}"));
            }
        }
    }

    public void Write(string level, string source, string message)
    {
        lock (_gate)
        {
            _entries.Add((level, source, message));
        }
    }
}
