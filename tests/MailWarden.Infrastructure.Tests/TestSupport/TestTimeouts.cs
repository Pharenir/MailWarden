namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>Central per-test timeout used as <c>[Fact(Timeout = TestTimeouts.PerTestMilliseconds)]</c>.</summary>
internal static class TestTimeouts
{
    /// <summary>Default per-test timeout (30 s) from the project test configuration.</summary>
    public const int PerTestMilliseconds = 30_000;
}
