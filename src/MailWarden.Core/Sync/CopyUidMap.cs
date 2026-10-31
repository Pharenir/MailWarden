using System.Collections.ObjectModel;

namespace MailWarden.Core.Sync;

/// <summary>
/// COPYUID result of a move (RFC 4315): the new UID in the target folder per source UID, and the UIDVALIDITY of the
/// target folder the new UIDs belong to. An empty map means "no usable COPYUID" (none sent, or inconsistent).
/// </summary>
/// <param name="DestinationUidValidity">UIDVALIDITY of the target folder from the COPYUID response; 0 when empty.</param>
/// <param name="Uids">Source UID → new UID in the target folder.</param>
public sealed record CopyUidMap(uint DestinationUidValidity, IReadOnlyDictionary<uint, uint> Uids)
{
    /// <summary>No usable COPYUID: nothing is known about the new UIDs.</summary>
    public static CopyUidMap Empty { get; } = new(0, ReadOnlyDictionary<uint, uint>.Empty);
}
