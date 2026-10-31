using MailWarden.Core.Model;

namespace MailWarden.Core.Accounts;

/// <summary>
/// Maps a server folder (full name, hierarchy delimiter, flags) to a <see cref="FolderClassification"/>. Roles come
/// from server flags first; names are only used for INBOX, the app-owned folders and the documented Microsoft
/// folders without flags. The server "Outbox" is never <see cref="FolderRole.Outbox"/> (that role is the local send
/// queue).
/// </summary>
public static class FolderRoleResolver
{
    private const string InboxName = "INBOX";
    private const string ScreenerName = "Screener";
    private const string QuarantineName = "Quarantine";
    private const string MicrosoftArchiveName = "Archive";
    private const string MicrosoftSyncIssuesName = "Synchronisierungsprobleme";

    // Outlook.com top-level folders without mail relevance (AP-00 findings); compared case-insensitively.
    private static readonly HashSet<string> MicrosoftHiddenTopLevelNames =
        new(StringComparer.OrdinalIgnoreCase) { "Notes", "Outbox", "Scheduled", MicrosoftSyncIssuesName };

    // Special-use flags in precedence order; the first match decides the role.
    private static readonly (ServerFolderFlags Flag, FolderClassification Classification)[] RoleFlags =
    [
        (ServerFolderFlags.Inbox, new FolderClassification(FolderRole.Inbox, Hidden: false)),
        (ServerFolderFlags.Sent, new FolderClassification(FolderRole.Sent, Hidden: false)),
        (ServerFolderFlags.Drafts, new FolderClassification(FolderRole.Drafts, Hidden: false)),
        (ServerFolderFlags.Junk, new FolderClassification(FolderRole.Junk, Hidden: false)),
        (ServerFolderFlags.Trash, new FolderClassification(FolderRole.Trash, Hidden: false)),
        (ServerFolderFlags.Archive, new FolderClassification(FolderRole.Archive, Hidden: false)),
        (ServerFolderFlags.All, new FolderClassification(FolderRole.All, Hidden: true)),
    ];

    private static readonly FolderClassification HiddenWithoutRole = new(FolderRole.None, Hidden: true);
    private static readonly FolderClassification VisibleWithoutRole = new(FolderRole.None, Hidden: false);

    /// <summary>
    /// Classifies a server folder. Rules in this order:
    /// 1. <see cref="ServerFolderFlags.NoSelect"/> or <see cref="ServerFolderFlags.NonExistent"/> → (None, hidden).
    /// 2. Special-use flags Inbox, Sent, Drafts, Junk, Trash, Archive → same role; All → (All, hidden);
    ///    Flagged or Important without one of these → (None, hidden).
    /// 3. Name "INBOX" (case-insensitive) → Inbox.
    /// 4. Top-level "Screener" / "Quarantine" (exact) → Screener / Quarantine.
    /// 5. Microsoft only (names case-insensitive): top-level "Archive" → Archive; top-level "Notes", "Outbox",
    ///    "Scheduled", "Synchronisierungsprobleme" and the children of the latter → (None, hidden).
    /// 6. Everything else → (None, visible).
    /// </summary>
    /// <param name="provider">Provider of the account the folder belongs to.</param>
    /// <param name="fullName">Full folder name as reported by the server.</param>
    /// <param name="separator">Hierarchy delimiter of the server ('/' for Outlook.com and Gmail).</param>
    /// <param name="flags">Server flags of the folder.</param>
    /// <returns>Role and visibility of the folder.</returns>
    /// <exception cref="ArgumentException">The full name is null or empty.</exception>
    public static FolderClassification Resolve(
        MailProvider provider,
        string fullName,
        char separator,
        ServerFolderFlags flags)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullName);

        // Rule 1: containers that cannot hold messages.
        if ((flags & (ServerFolderFlags.NoSelect | ServerFolderFlags.NonExistent)) != 0)
        {
            return HiddenWithoutRole;
        }

        // Rule 2: special-use flags, then Gmail virtual folders.
        foreach ((ServerFolderFlags flag, FolderClassification classification) in RoleFlags)
        {
            if (flags.HasFlag(flag))
            {
                return classification;
            }
        }

        if ((flags & (ServerFolderFlags.Flagged | ServerFolderFlags.Important)) != 0)
        {
            return HiddenWithoutRole;
        }

        // Rule 3: INBOX is case-insensitive by IMAP definition.
        if (string.Equals(fullName, InboxName, StringComparison.OrdinalIgnoreCase))
        {
            return new FolderClassification(FolderRole.Inbox, Hidden: false);
        }

        bool isTopLevel = fullName.IndexOf(separator) < 0;

        // Rule 4: folders the app creates itself (both providers).
        if (isTopLevel && fullName == ScreenerName)
        {
            return new FolderClassification(FolderRole.Screener, Hidden: false);
        }

        if (isTopLevel && fullName == QuarantineName)
        {
            return new FolderClassification(FolderRole.Quarantine, Hidden: false);
        }

        // Rule 5: Outlook.com folders that carry no flag (names case-insensitive, OUTBOX = Outbox).
        if (provider == MailProvider.Microsoft)
        {
            if (isTopLevel && string.Equals(fullName, MicrosoftArchiveName, StringComparison.OrdinalIgnoreCase))
            {
                return new FolderClassification(FolderRole.Archive, Hidden: false);
            }

            if (isTopLevel && MicrosoftHiddenTopLevelNames.Contains(fullName))
            {
                return HiddenWithoutRole;
            }

            if (fullName.StartsWith(MicrosoftSyncIssuesName + separator, StringComparison.OrdinalIgnoreCase))
            {
                return HiddenWithoutRole;
            }
        }

        // Rule 6: ordinary user folder; never a role from any other name.
        return VisibleWithoutRole;
    }
}
