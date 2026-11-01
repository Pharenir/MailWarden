using MailWarden.Core.Model;

namespace MailWarden.Core.Classification;

/// <summary>
/// Fixed routing table of the classification pass. Only Inbox and Junk are classified at all.
/// </summary>
public static class ClassificationRouting
{
    /// <summary>
    /// Maps source folder role and tier to the action. Inbox: TrustedContact stays, LikelyLegit → Screener, LikelySpam →
    /// Junk, CertainSpam → Quarantine. Junk: TrustedContact → Inbox (rescue), every other tier stays.
    /// </summary>
    /// <param name="sourceRole">Role of the folder the message is in.</param>
    /// <param name="tier">Decided tier.</param>
    /// <returns>The action to carry out.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The role is neither Inbox nor Junk, or the tier is not a defined
    /// value.</exception>
    public static ClassificationAction Route(FolderRole sourceRole, SpamTier tier)
    {
        return sourceRole switch
        {
            FolderRole.Inbox => tier switch
            {
                SpamTier.TrustedContact => ClassificationAction.None,
                SpamTier.LikelyLegit => ClassificationAction.MoveToScreener,
                SpamTier.LikelySpam => ClassificationAction.MoveToJunk,
                SpamTier.CertainSpam => ClassificationAction.MoveToQuarantine,
                _ => throw UnknownTier(tier),
            },
            FolderRole.Junk => tier switch
            {
                SpamTier.TrustedContact => ClassificationAction.MoveToInbox,
                SpamTier.LikelyLegit or SpamTier.LikelySpam or SpamTier.CertainSpam => ClassificationAction.None,
                _ => throw UnknownTier(tier),
            },
            _ => throw UnclassifiedRole(sourceRole),
        };
    }

    /// <summary>
    /// Action for a readable message without a tier, because it has no single usable From address or could not be
    /// evaluated. The sender causes this, so it must not stay unfiltered, and must not be a way to choose a better target
    /// than the classifier would: from the Inbox it goes to Junk; in Junk it stays.
    /// </summary>
    /// <param name="sourceRole">Role of the folder the message is in.</param>
    /// <returns>The action to carry out.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The role is neither Inbox nor Junk.</exception>
    public static ClassificationAction RouteWithoutSender(FolderRole sourceRole)
    {
        return sourceRole switch
        {
            FolderRole.Inbox => ClassificationAction.MoveToJunk,
            FolderRole.Junk => ClassificationAction.None,
            _ => throw UnclassifiedRole(sourceRole),
        };
    }

    private static ArgumentOutOfRangeException UnknownTier(SpamTier tier) =>
        new(nameof(tier), tier, "Unknown spam tier.");

    private static ArgumentOutOfRangeException UnclassifiedRole(FolderRole sourceRole) =>
        new(nameof(sourceRole), sourceRole, "Only Inbox and Junk are classified.");
}
