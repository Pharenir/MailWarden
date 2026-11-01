using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using MailWarden.Infrastructure.Composition;

namespace MailWarden.Presentation;

/// <summary>
/// Best effort for a permanent delete outside Trash and Junk: without COPYUID from the server the mail stays in the Trash.
/// When the command is done and a mail with the same Message-ID lies in the Trash, the banner says so once. A Trash beyond
/// <see cref="ScannedPages"/> pages is not searched further, and a mail without a Message-ID cannot be recognised.
/// </summary>
internal sealed class PermanentDeleteWatcher(IMailboxReadModel readModel, ShellBanner banner)
{
    private const int ScannedPages = 10;
    private const int ScannedPageSize = 500;

    // The sync can still run at the first refresh after the command, so a watch looks a second time.
    private const int ChecksPerWatch = 2;

    private readonly List<Watch> watches = [];
    private bool isChecking;

    /// <summary>True when a permanent delete is being watched.</summary>
    public bool IsWatching => watches.Count > 0;

    /// <summary>Starts watching messages whose permanent delete has been stored.</summary>
    /// <param name="messages">The deleted messages from outside Trash and Junk.</param>
    public void Add(IEnumerable<MailMessage> messages)
    {
        foreach (IGrouping<Guid, MailMessage> accountMessages in messages.Where(message => message.MessageId is not null).GroupBy(message => message.AccountId))
        {
            watches.Add(new Watch(
                accountMessages.Key,
                accountMessages.Select(message => message.Id).ToHashSet(),
                accountMessages.Select(message => message.MessageId!).ToHashSet(StringComparer.Ordinal)));
        }
    }

    /// <summary>Stops watching messages whose command was discarded: they were not deleted.</summary>
    /// <param name="messageIds">Message ids of the discarded command.</param>
    public void Forget(IReadOnlyCollection<long> messageIds)
    {
        watches.RemoveAll(watch => watch.MessageIds.Overlaps(messageIds));
    }

    /// <summary>Checks the watches whose commands are done, that is no longer in the queue.</summary>
    /// <param name="listedActions">The commands in the queue now.</param>
    /// <returns>A task that completes when the check ended.</returns>
    public async Task CheckAsync(IReadOnlyList<PendingAction> listedActions)
    {
        if (isChecking)
        {
            return;
        }

        isChecking = true;
        try
        {
            foreach (Watch watch in watches.ToList())
            {
                if (listedActions.Any(action => watch.MessageIds.Overlaps(action.MessageIds)))
                {
                    continue;
                }

                if (await LiesInTrashAsync(watch))
                {
                    banner.Set(ShellBanner.BannerSource.DeleteReachedTrash, UiText.BannerDeleteReachedTrash);
                    watches.Remove(watch);
                }
                else if (++watch.CheckCount >= ChecksPerWatch)
                {
                    watches.Remove(watch);
                }
            }
        }
        finally
        {
            isChecking = false;
        }
    }

    private async Task<bool> LiesInTrashAsync(Watch watch)
    {
        MailFolder? trash = FolderLookup.FindTrash(await readModel.ListFoldersAsync(watch.AccountId));
        if (trash is null)
        {
            return false;
        }

        for (int page = 0; page < ScannedPages; page++)
        {
            IReadOnlyList<MailMessage> messages = await readModel.ListMessagesAsync(
                new MessageQuery(FolderId: trash.Id, Limit: ScannedPageSize, Offset: page * ScannedPageSize));
            if (messages.Any(message => message.MessageId is string messageId
                && watch.HeaderIds.Contains(messageId)
                && !watch.MessageIds.Contains(message.Id)))
            {
                return true;
            }

            if (messages.Count < ScannedPageSize)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>The messages of one account whose permanent delete is being watched.</summary>
    private sealed class Watch(Guid accountId, HashSet<long> messageIds, HashSet<string> headerIds)
    {
        public Guid AccountId { get; } = accountId;

        public HashSet<long> MessageIds { get; } = messageIds;

        public HashSet<string> HeaderIds { get; } = headerIds;

        public int CheckCount { get; set; }
    }
}
