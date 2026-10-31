using MailWarden.Infrastructure.Background;

namespace MailWarden.Infrastructure.Tests.Background;

/// <summary>AP-03d case 18: option validation and defaults (D3).</summary>
[Trait("Category", "feature:sync")]
public sealed class MailboxServiceOptionsTests
{
    private static MailboxServiceOptions Build(int pollSeconds, int idleRenewSeconds, int debounceSeconds) =>
        new(TimeSpan.FromSeconds(pollSeconds), TimeSpan.FromSeconds(idleRenewSeconds), TimeSpan.FromSeconds(debounceSeconds));

    // Case 18
    [Fact]
    public void Default_values_are_15_minutes_9_minutes_and_2_seconds()
    {
        MailboxServiceOptions defaults = MailboxServiceOptions.Default;

        Assert.Equal(TimeSpan.FromMinutes(15), defaults.PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(9), defaults.IdleRenewInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.TriggerDebounce);
    }

    // Case 18
    [Theory]
    [InlineData(59, 540, 2)]
    [InlineData(0, 540, 2)]
    [InlineData(-60, 540, 2)]
    [InlineData(900, 59, 2)]
    [InlineData(900, 0, 2)]
    [InlineData(900, 1681, 2)]
    [InlineData(900, 540, -1)]
    [InlineData(900, 540, 61)]
    public void Out_of_range_values_throw_ArgumentOutOfRangeException(int pollSeconds, int idleRenewSeconds, int debounceSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(pollSeconds, idleRenewSeconds, debounceSeconds));
    }

    // Case 18
    [Theory]
    [InlineData(60, 60, 0)]
    [InlineData(900, 1680, 60)]
    [InlineData(86_400, 540, 2)]
    public void Boundary_values_are_accepted(int pollSeconds, int idleRenewSeconds, int debounceSeconds)
    {
        MailboxServiceOptions options = Build(pollSeconds, idleRenewSeconds, debounceSeconds);

        Assert.Equal(TimeSpan.FromSeconds(pollSeconds), options.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(idleRenewSeconds), options.IdleRenewInterval);
        Assert.Equal(TimeSpan.FromSeconds(debounceSeconds), options.TriggerDebounce);
    }
}
