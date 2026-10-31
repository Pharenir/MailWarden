using MailWarden.Core.Model;

namespace MailWarden.Core.Sync;

/// <summary>
/// System flags of one message on the server.
/// </summary>
/// <param name="Uid">IMAP UID within the open folder.</param>
/// <param name="Flags">System flags; keywords are not part of the model.</param>
public sealed record RemoteFlags(uint Uid, MessageFlags Flags);
