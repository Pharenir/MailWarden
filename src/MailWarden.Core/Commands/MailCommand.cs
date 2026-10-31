using MailWarden.Core.Model;

namespace MailWarden.Core.Commands;

/// <summary>
/// A queued server change for messages of one folder.
/// </summary>
/// <param name="Id">Database id of the command.</param>
/// <param name="AccountId">Account of the source folder.</param>
/// <param name="Kind">What the command does on the server.</param>
/// <param name="FolderId">Source folder (database id).</param>
/// <param name="FolderUidValidity">
/// UIDVALIDITY of the source folder at enqueue time; the executor never acts when the server reports another value.
/// </param>
/// <param name="Uids">UIDs in the source folder, ascending and without duplicates (1..1,000 entries).</param>
/// <param name="TargetFolderId">Target folder; only for <see cref="MailCommandKind.Move"/> and <see cref="MailCommandKind.MoveToTrash"/>.</param>
/// <param name="FlagsToAdd">Flags to add; only for <see cref="MailCommandKind.SetFlags"/>, otherwise None.</param>
/// <param name="FlagsToRemove">Flags to remove; only for <see cref="MailCommandKind.SetFlags"/>, otherwise None.</param>
/// <param name="Status">Pending or permanently failed.</param>
/// <param name="Attempts">Number of failed attempts so far.</param>
/// <param name="CreatedAt">Enqueue time.</param>
/// <param name="NextAttemptAt">Earliest time of the next attempt; null = due now.</param>
/// <param name="LastError">
/// Last failure: an exception type name or a fixed English text of this app, never server text. Null before the first
/// failure.
/// </param>
public sealed record MailCommand(
    long Id,
    Guid AccountId,
    MailCommandKind Kind,
    long FolderId,
    uint FolderUidValidity,
    IReadOnlyList<uint> Uids,
    long? TargetFolderId,
    MessageFlags FlagsToAdd,
    MessageFlags FlagsToRemove,
    MailCommandStatus Status,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt,
    string? LastError);
