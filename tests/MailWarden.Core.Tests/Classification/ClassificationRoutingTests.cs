using MailWarden.Core.Model;

namespace MailWarden.Core.Tests.Classification;

/// <summary>AP-07c D1 / case 1: <see cref="ClassificationRouting.Route"/> implements the routing table of the goal.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationRoutingTests
{
    public static TheoryData<FolderRole, SpamTier, ClassificationAction> RoutingTable() => new()
    {
        { FolderRole.Inbox, SpamTier.TrustedContact, ClassificationAction.None },
        { FolderRole.Inbox, SpamTier.LikelyLegit, ClassificationAction.MoveToScreener },
        { FolderRole.Inbox, SpamTier.LikelySpam, ClassificationAction.MoveToJunk },
        { FolderRole.Inbox, SpamTier.CertainSpam, ClassificationAction.MoveToQuarantine },
        { FolderRole.Junk, SpamTier.TrustedContact, ClassificationAction.MoveToInbox },
        { FolderRole.Junk, SpamTier.LikelyLegit, ClassificationAction.None },
        { FolderRole.Junk, SpamTier.LikelySpam, ClassificationAction.None },
        { FolderRole.Junk, SpamTier.CertainSpam, ClassificationAction.None },
    };

    // Case 1
    [Theory]
    [MemberData(nameof(RoutingTable))]
    public void Route_follows_the_table(FolderRole sourceRole, SpamTier tier, ClassificationAction expected)
    {
        Assert.Equal(expected, ClassificationRouting.Route(sourceRole, tier));
    }

    // Case 1: only Inbox and Junk are classified
    [Theory]
    [InlineData(FolderRole.None)]
    [InlineData(FolderRole.Sent)]
    [InlineData(FolderRole.Drafts)]
    [InlineData(FolderRole.Trash)]
    [InlineData(FolderRole.Archive)]
    [InlineData(FolderRole.Screener)]
    [InlineData(FolderRole.Quarantine)]
    [InlineData(FolderRole.All)]
    [InlineData(FolderRole.Outbox)]
    public void Route_for_any_other_source_role_throws(FolderRole sourceRole)
    {
        foreach (SpamTier tier in Enum.GetValues<SpamTier>())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ClassificationRouting.Route(sourceRole, tier));
        }
    }

    // Case 45 / G9
    [Theory]
    [InlineData(FolderRole.Inbox)]
    [InlineData(FolderRole.Junk)]
    public void Route_with_an_undefined_tier_throws(FolderRole sourceRole)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassificationRouting.Route(sourceRole, (SpamTier)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassificationRouting.Route(sourceRole, (SpamTier)(-1)));
    }

    [Fact]
    public void Action_values_are_the_documented_numbers()
    {
        Assert.Equal(0, (int)ClassificationAction.None);
        Assert.Equal(1, (int)ClassificationAction.MoveToInbox);
        Assert.Equal(2, (int)ClassificationAction.MoveToScreener);
        Assert.Equal(3, (int)ClassificationAction.MoveToJunk);
        Assert.Equal(4, (int)ClassificationAction.MoveToQuarantine);
    }

    [Fact]
    public void Outcome_values_are_the_documented_numbers()
    {
        Assert.Equal(1, (int)ClassificationOutcome.NoAction);
        Assert.Equal(2, (int)ClassificationOutcome.Recorded);
        Assert.Equal(3, (int)ClassificationOutcome.Enqueued);
        Assert.Equal(4, (int)ClassificationOutcome.TargetMissing);
        Assert.Equal(5, (int)ClassificationOutcome.EnqueueFailed);
        Assert.Equal(6, (int)ClassificationOutcome.ReadFailed);
    }
}
