namespace MailWarden.Core.Classification;

/// <summary>
/// Persists the manual allow/block lists. An entry has one kind; address and domain entries are compared case-insensitively
/// for ASCII letters.
/// </summary>
public interface ISenderListStore
{
    /// <summary>Lists all entries.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All entries ordered by their text, case-insensitive for ASCII letters only (SQLite <c>NOCASE</c>).</returns>
    Task<IReadOnlyList<SenderListEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores the entry, trimmed. An entry that exists already gets the new spelling and the new kind.
    /// </summary>
    /// <param name="entry">
    /// Address: must hold '@' and a domain, see <see cref="AddressRules.IsWellFormedAddress"/>. Domain: no '@', at least one
    /// dot, see <see cref="AddressRules.IsWellFormedDomain"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the entry is stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    /// <exception cref="ArgumentException">The text is invalid, or the kind is <see cref="ListMembership.None"/>.</exception>
    Task SetAsync(SenderListEntry entry, CancellationToken ct = default);

    /// <summary>Removes an entry. Unknown entry = no-op.</summary>
    /// <param name="entry">Address or domain, trimmed before the lookup.</param>
    /// <param name="isDomain">True for a domain entry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the entry is gone.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    Task RemoveAsync(string entry, bool isDomain, CancellationToken ct = default);
}
