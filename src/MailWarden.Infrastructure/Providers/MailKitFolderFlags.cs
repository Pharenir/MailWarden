using MailKit;
using MailWarden.Core.Accounts;

namespace MailWarden.Infrastructure.Providers;

/// <summary>
/// Maps MailKit <see cref="FolderAttributes"/> to the provider-neutral <see cref="ServerFolderFlags"/>. Attributes
/// without a counterpart (HasChildren, Subscribed, Marked, …) are dropped.
/// </summary>
public static class MailKitFolderFlags
{
    private static readonly (FolderAttributes Attribute, ServerFolderFlags Flag)[] Mapping =
    [
        (FolderAttributes.Inbox, ServerFolderFlags.Inbox),
        (FolderAttributes.Sent, ServerFolderFlags.Sent),
        (FolderAttributes.Drafts, ServerFolderFlags.Drafts),
        (FolderAttributes.Junk, ServerFolderFlags.Junk),
        (FolderAttributes.Trash, ServerFolderFlags.Trash),
        (FolderAttributes.Archive, ServerFolderFlags.Archive),
        (FolderAttributes.All, ServerFolderFlags.All),
        (FolderAttributes.Flagged, ServerFolderFlags.Flagged),
        (FolderAttributes.Important, ServerFolderFlags.Important),
        (FolderAttributes.NoSelect, ServerFolderFlags.NoSelect),
        (FolderAttributes.NonExistent, ServerFolderFlags.NonExistent),
    ];

    /// <summary>Converts MailKit folder attributes.</summary>
    /// <param name="attributes">Attributes as reported by MailKit.</param>
    /// <returns>The corresponding server folder flags.</returns>
    public static ServerFolderFlags From(FolderAttributes attributes)
    {
        ServerFolderFlags flags = ServerFolderFlags.None;
        foreach ((FolderAttributes attribute, ServerFolderFlags flag) in Mapping)
        {
            if ((attributes & attribute) != 0)
            {
                flags |= flag;
            }
        }

        return flags;
    }
}
