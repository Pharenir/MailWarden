using MailWarden.Core.Accounts;

namespace MailWarden.Core.Sync;

/// <summary>
/// One folder as listed by the mail server.
/// </summary>
/// <param name="FullName">Full folder name as reported by the server, including hierarchy delimiters.</param>
/// <param name="Separator">Hierarchy delimiter of the server.</param>
/// <param name="Flags">Server folder flags (LIST / SPECIAL-USE attributes).</param>
public sealed record RemoteFolder(string FullName, char Separator, ServerFolderFlags Flags);
