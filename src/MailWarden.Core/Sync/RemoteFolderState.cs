namespace MailWarden.Core.Sync;

/// <summary>
/// State of a server folder right after it was opened.
/// </summary>
/// <param name="UidValidity">IMAP UIDVALIDITY of the folder.</param>
/// <param name="UidNext">IMAP UIDNEXT (0 when the server did not report it).</param>
/// <param name="HighestModSeq">CONDSTORE HIGHESTMODSEQ; 0 = the server has no CONDSTORE for this folder.</param>
/// <param name="MessageCount">Number of messages in the folder.</param>
public sealed record RemoteFolderState(uint UidValidity, uint UidNext, ulong HighestModSeq, int MessageCount);
