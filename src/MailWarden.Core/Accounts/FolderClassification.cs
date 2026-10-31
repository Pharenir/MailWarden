using MailWarden.Core.Model;

namespace MailWarden.Core.Accounts;

/// <summary>
/// Result of mapping a server folder to MailWarden.
/// </summary>
/// <param name="Role">Folder role used by group views.</param>
/// <param name="Hidden">True when the folder is not shown in the folder tree (containers, virtual or irrelevant folders).</param>
public sealed record FolderClassification(FolderRole Role, bool Hidden);
