using MailWarden.Core.Model;

namespace MailWarden.Core.Storage;

/// <summary>
/// Filter for list views. <paramref name="Role"/> selects across all accounts unless <paramref name="AccountId"/>
/// narrows it; <paramref name="FolderId"/> wins over <paramref name="Role"/>. No filter = all messages.
/// </summary>
/// <param name="Role">Folder role of the group view (e.g. Inbox over all accounts).</param>
/// <param name="AccountId">Restricts the result to one account.</param>
/// <param name="FolderId">Restricts the result to one folder; when set, <paramref name="Role"/> is ignored.</param>
/// <param name="Limit">Maximum number of messages, must be greater than 0.</param>
/// <param name="Offset">Number of messages to skip in newest-first order, must not be negative.</param>
/// <param name="ExcludedMessageIds">
/// Messages that do not count, by database id; null or empty excludes nothing. An id that is not stored has no effect, so the result
/// stays right after such a message is gone.
/// </param>
public sealed record MessageQuery(
    FolderRole? Role = null,
    Guid? AccountId = null,
    long? FolderId = null,
    int Limit = 100,
    int Offset = 0,
    IReadOnlyCollection<long>? ExcludedMessageIds = null);
