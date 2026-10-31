namespace MailWarden.Core.Model;

/// <summary>
/// Shared rules for finding special folders of an account, so that the command queue and the executor always pick
/// the same folder.
/// </summary>
public static class FolderLookup
{
    /// <summary>Finds the Trash folder of an account: the first folder with role Trash in the given order.</summary>
    /// <param name="accountFolders">Folders of one account in folder-store list order.</param>
    /// <returns>The Trash folder, or null when the account has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="accountFolders"/> is null.</exception>
    public static MailFolder? FindTrash(IEnumerable<MailFolder> accountFolders)
    {
        ArgumentNullException.ThrowIfNull(accountFolders);
        return accountFolders.FirstOrDefault(folder => folder.Role == FolderRole.Trash);
    }
}
