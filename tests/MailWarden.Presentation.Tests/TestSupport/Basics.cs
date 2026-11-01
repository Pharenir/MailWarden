namespace MailWarden.Presentation.Tests.TestSupport;

/// <summary>Central per-test timeout used as <c>[Fact(Timeout = TestTimeouts.PerTestMilliseconds)]</c>.</summary>
internal static class TestTimeouts
{
    /// <summary>Default per-test timeout (30 s) from the project test configuration.</summary>
    public const int PerTestMilliseconds = 30_000;
}

/// <summary>
/// Time provider with a fixed instant. The local time zone is the machine's own, so a view model that uses either
/// <c>TimeProvider.LocalTimeZone</c> or <c>TimeZoneInfo.Local</c> sees the same zone as the test expectations.
/// </summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}

/// <summary>Polling helpers for work that view models start fire-and-forget (property setters, commands).</summary>
internal static class Wait
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Polls until the condition holds; fails after 10 s with the description.</summary>
    public static async Task UntilAsync(Func<bool> condition, string description)
    {
        DateTime deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {description}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Gives the code under test a short real-time window in which something unwanted could still happen.</summary>
    public static Task NegativeWindowAsync() => Task.Delay(300);
}

/// <summary>Unique directory below the test bin folder, removed on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        RootPath = Path.Combine(AppContext.BaseDirectory, "ap04a-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
