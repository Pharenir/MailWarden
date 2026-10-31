using MailWarden.Core.Model;

namespace MailWarden.Core.Sync;

/// <summary>
/// One complete message fetched from the server.
/// </summary>
/// <param name="Uid">IMAP UID within the open folder.</param>
/// <param name="Flags">System flags at fetch time.</param>
/// <param name="InternalDate">IMAP INTERNALDATE (delivery time on the server).</param>
/// <param name="Size">Size of the raw message in bytes (RFC822.SIZE).</param>
/// <param name="Content">The complete raw message (RFC 5322); owned and disposed by the caller.</param>
public sealed record RemoteMessage(uint Uid, MessageFlags Flags, DateTimeOffset InternalDate, long Size, Stream Content);
