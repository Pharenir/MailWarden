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
        if (!Enum.IsDefined(tier))
        {
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown spam tier.");
        }

        return sourceRole switch
        {
            FolderRole.Inbox => tier switch
            {
                SpamTier.TrustedContact => ClassificationAction.None,
                SpamTier.LikelyLegit => ClassificationAction.MoveToScreener,
                SpamTier.LikelySpam => ClassificationAction.MoveToJunk,
                _ => ClassificationAction.MoveToQuarantine,
            },
            FolderRole.Junk => tier == SpamTier.TrustedContact
                ? ClassificationAction.MoveToInbox
                : ClassificationAction.None,
            _ => throw new ArgumentOutOfRangeException(
                nameof(sourceRole),
                sourceRole,
                "Only Inbox and Junk are classified."),
        };
    }
}
