using System.Text.RegularExpressions;
using MailWarden.Infrastructure.Logging;

namespace MailWarden.Infrastructure.Tests.Logging;

/// <summary>AP-04a case 28 (D7): <see cref="CriticalFileLog"/>.</summary>
[Trait("Category", "feature:ui")]
public sealed class CriticalFileLogTests : IDisposable
{
    private static readonly Regex LinePattern = new(
        @"^2026-10-31T12:00:00(\.\d+)?(Z|[+-]\d{2}:\d{2}) \| (?<level>[^|]*) \| (?<source>[^|]*) \| (?<message>.*)$",
        RegexOptions.CultureInvariant);

    private readonly string _baseDirectory = Path.Combine(AppContext.BaseDirectory, "ap04a-log", Guid.NewGuid().ToString("N"));

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was created.
        }
        catch (IOException)
        {
            // Best effort: a failing test may leave a handle open.
        }
    }

    private string LogFile(string directory) => Path.Combine(directory, "mailwarden.log");

    private static string[] ReadLines(string file) =>
        File.ReadAllText(file).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

    // Case 28
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Write_appends_one_line_in_the_documented_format()
    {
        await Task.Yield();
        string directory = Path.Combine(_baseDirectory, "format");
        Directory.CreateDirectory(directory);
        var log = new CriticalFileLog(directory, _clock);

        log.Write("ERROR", "shell.initialize", "InvalidOperationException");
        log.Write("WARN", "shell.save", "UiSettingsException");

        string[] lines = ReadLines(LogFile(directory));
        Assert.Equal(2, lines.Length);
        Match first = LinePattern.Match(lines[0]);
        Assert.True(first.Success, lines[0]);
        Assert.Equal("ERROR", first.Groups["level"].Value);
        Assert.Equal("shell.initialize", first.Groups["source"].Value);
        Assert.Equal("InvalidOperationException", first.Groups["message"].Value);
        Match second = LinePattern.Match(lines[1]);
        Assert.True(second.Success, lines[1]);
        Assert.Equal("WARN", second.Groups["level"].Value);
    }

    // Case 28
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_second_instance_appends_to_the_same_file()
    {
        await Task.Yield();
        string directory = Path.Combine(_baseDirectory, "append");
        Directory.CreateDirectory(directory);

        new CriticalFileLog(directory, _clock).Write("ERROR", "a.b", "first");
        new CriticalFileLog(directory, _clock).Write("ERROR", "a.b", "second");

        string[] lines = ReadLines(LogFile(directory));
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("first", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("second", lines[1], StringComparison.Ordinal);
    }

    // Case 28
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cr_and_lf_in_any_field_become_spaces_and_the_entry_stays_one_line()
    {
        await Task.Yield();
        string directory = Path.Combine(_baseDirectory, "newlines");
        Directory.CreateDirectory(directory);
        var log = new CriticalFileLog(directory, _clock);

        log.Write("ER\r\nROR", "mod\nule.func\rtion", "first\r\nsecond\nthird\rfourth");

        string[] lines = ReadLines(LogFile(directory));
        string line = Assert.Single(lines);
        Match match = LinePattern.Match(line);
        Assert.True(match.Success, line);
        Assert.Matches(@"^ER\s{1,2}ROR$", match.Groups["level"].Value);
        Assert.Matches(@"^mod\s?ule\.func\s?tion$", match.Groups["source"].Value);
        Assert.Matches(@"^first\s{1,2}second\s?third\s?fourth$", match.Groups["message"].Value);
        Assert.Single(Regex.Matches(File.ReadAllText(LogFile(directory)), "\n"));
    }

    // Case 28
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Two_threads_writing_in_parallel_produce_only_complete_lines()
    {
        string directory = Path.Combine(_baseDirectory, "threads");
        Directory.CreateDirectory(directory);
        var log = new CriticalFileLog(directory, _clock);
        const int perThread = 200;

        Task Writer(string source) => Task.Run(() =>
        {
            for (int index = 0; index < perThread; index++)
            {
                log.Write("ERROR", source, $"entry_{index:D4}_" + new string('x', 100));
            }
        });

        await Task.WhenAll(Writer("thread.one"), Writer("thread.two"));

        string[] lines = ReadLines(LogFile(directory));
        Assert.Equal(2 * perThread, lines.Length);
        Assert.All(lines, line => Assert.True(LinePattern.IsMatch(line), line));
        Assert.Equal(perThread, lines.Count(line => line.Contains(" | thread.one | ", StringComparison.Ordinal)));
        Assert.Equal(perThread, lines.Count(line => line.Contains(" | thread.two | ", StringComparison.Ordinal)));
    }

    // Case 28
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Missing_directory_does_not_throw()
    {
        await Task.Yield();
        string directory = Path.Combine(_baseDirectory, "not", "created", "yet");
        var log = new CriticalFileLog(directory, _clock);

        Exception? failure = Record.Exception(() => log.Write("ERROR", "a.b", "message"));

        Assert.Null(failure);
    }

    // Case 28: a regular file where the directory should be makes the log folder unusable.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unusable_directory_does_not_throw()
    {
        await Task.Yield();
        Directory.CreateDirectory(_baseDirectory);
        string blocker = Path.Combine(_baseDirectory, "blocker");
        await File.WriteAllTextAsync(blocker, "not a directory");
        var log = new CriticalFileLog(blocker, _clock);

        Exception? failure = Record.Exception(() => log.Write("ERROR", "a.b", "message"));

        Assert.Null(failure);
    }

    // Case 28: a write failure (file locked exclusively by someone else) is swallowed.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Locked_log_file_does_not_throw()
    {
        await Task.Yield();
        string directory = Path.Combine(_baseDirectory, "locked");
        Directory.CreateDirectory(directory);
        var log = new CriticalFileLog(directory, _clock);

        await using (new FileStream(LogFile(directory), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Exception? failure = Record.Exception(() => log.Write("ERROR", "a.b", "while_locked"));
            Assert.Null(failure);
        }
    }
}
