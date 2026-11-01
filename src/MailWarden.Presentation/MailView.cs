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

    /// <summary>The storage query of this view.</summary>
    /// <param name="limit">Maximum number of messages.</param>
    /// <param name="offset">Number of messages to skip.</param>
    /// <returns>The query.</returns>
    public MessageQuery ToQuery(int limit, int offset) => new(Role, AccountId, FolderId, limit, offset);
}
