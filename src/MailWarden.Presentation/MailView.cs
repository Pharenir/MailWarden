using MailWarden.Core.Model;
using MailWarden.Core.Storage;

namespace MailWarden.Presentation;

/// <summary>
/// A selectable message view: a role over all accounts, a role of one account, or one folder.
/// </summary>
/// <param name="Role">Folder role; null for a folder view.</param>
/// <param name="AccountId">Account; null for a view over all accounts.</param>
/// <param name="FolderId">Folder; null for a role view.</param>
public sealed record MailView(FolderRole? Role, Guid? AccountId, long? FolderId)
{
    private static readonly FolderRole[] OrderedGroupRoles =
    [
        FolderRole.Inbox,
        FolderRole.Sent,
        FolderRole.Outbox,
        FolderRole.Screener,
        FolderRole.Junk,
        FolderRole.Quarantine,
    ];

    /// <summary>Inbox, Sent, Outbox, Screener, Junk, Quarantine; in this order.</summary>
    public static IReadOnlyList<FolderRole> GroupRoles { get; } = Array.AsReadOnly(OrderedGroupRoles);

    /// <summary>A role view over all accounts.</summary>
    /// <param name="role">Folder role.</param>
    /// <returns>The view.</returns>
    public static MailView Group(FolderRole role) => new(role, null, null);

    /// <summary>A role view of one account.</summary>
    /// <param name="role">Folder role.</param>
    /// <param name="accountId">Account id.</param>
    /// <returns>The view.</returns>
    public static MailView GroupForAccount(FolderRole role, Guid accountId) => new(role, accountId, null);

    /// <summary>A view of one folder.</summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="folderId">Folder id.</param>
    /// <returns>The view.</returns>
    public static MailView Folder(Guid accountId, long folderId) => new(null, accountId, folderId);

    /// <summary>False when no folder list can change the answer of <see cref="FindEmptyTarget"/>, so that nothing has to be read.</summary>
    internal bool MayHaveEmptyTarget => FolderId is not null || Role is FolderRole.Trash or FolderRole.Junk;

    /// <summary>
    /// What "Ordner leeren" would empty for this view: exactly one folder with role Trash or Junk behind it. A group view over
    /// several accounts has more than one such folder and cannot be emptied at once.
    /// </summary>
    /// <param name="folders">The known folders; those of other accounts are ignored.</param>
    /// <returns>The folder, or null when this view cannot be emptied.</returns>
    internal MailFolder? FindEmptyTarget(IEnumerable<MailFolder> folders)
    {
        List<MailFolder> candidates = folders
            .Where(folder => AccountId is null || folder.AccountId == AccountId)
            .Where(folder => FolderId is long folderId ? folder.Id == folderId : folder.Role == Role)
            .ToList();
        return candidates is [{ Role: FolderRole.Trash or FolderRole.Junk } target] ? target : null;
    }

    /// <summary>The storage query of this view.</summary>
    /// <param name="limit">Maximum number of messages.</param>
    /// <param name="offset">Number of messages to skip.</param>
    /// <returns>The query.</returns>
    public MessageQuery ToQuery(int limit, int offset) => new(Role, AccountId, FolderId, limit, offset);
}
