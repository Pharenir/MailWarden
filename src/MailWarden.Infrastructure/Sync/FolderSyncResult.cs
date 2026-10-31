using MailWarden.Core.Model;

namespace MailWarden.Infrastructure.Sync;

/// <summary>
/// Outcome of syncing one folder. Counts describe what was done before a failure as well.
/// </summary>
/// <param name="FolderFullName">Full folder name on the server.</param>
/// <param name="Role">Folder role.</param>
/// <param name="Added">Messages fetched and stored.</param>
/// <param name="FlagsUpdated">Stored messages whose flags changed.</param>
/// <param name="Deleted">Stored messages removed because they are gone on the server.</param>
/// <param name="UidValidityReset">True when the local copy was discarded because UIDVALIDITY changed.</param>
/// <param name="Error">
/// Null when everything succeeded. "&lt;ExceptionType&gt;: &lt;message&gt;" = the folder failed and keeps its previous
/// sync state. "&lt;n&gt; message(s) failed (first: &lt;Type.Name&gt;)" = the folder completed but single messages
/// failed; they are still missing locally, so the next pass retries them.
/// </param>
public sealed record FolderSyncResult(
    string FolderFullName,
    FolderRole Role,
    int Added,
    int FlagsUpdated,
    int Deleted,
    bool UidValidityReset,
    string? Error);
