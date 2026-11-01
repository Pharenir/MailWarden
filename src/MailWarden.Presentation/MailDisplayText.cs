using MailWarden.Core.Text;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation;

/// <summary>
/// The display rules for mail data shared by message list and reading pane. Header lines from a sender have no length
/// limit by themselves and could hide the real sender or push the message out of the window.
/// </summary>
internal static class MailDisplayText
{
    /// <summary>Maximum length of a sender.</summary>
    public const int MaxSenderCharacters = 100;

    /// <summary>Maximum length of a subject.</summary>
    public const int MaxSubjectCharacters = 500;

    /// <summary>Maximum length of the To or Cc line.</summary>
    public const int MaxRecipientCharacters = 2_000;

    /// <summary>Maximum length of a preview.</summary>
    public const int MaxPreviewCharacters = 200;

    /// <summary>Maximum length of a link target under the mouse.</summary>
    public const int MaxLinkCharacters = 500;

    /// <summary>Attachment names listed before "und N weitere".</summary>
    public const int MaxAttachmentNames = 50;

    // No file system allows longer names, so only crafted names are cut.
    private const int MaxAttachmentNameCharacters = 260;

    // Beside a name the address gets at most this much.
    private const int MaxAddressCharactersWithName = 60;

    private const string Ellipsis = "…";

    /// <summary>The shortened subject; a fixed text when it is empty.</summary>
    /// <param name="subject">Raw subject.</param>
    /// <returns>The text to show.</returns>
    public static string Subject(string subject)
    {
        string shortened = SafeDisplayText.Shorten(subject, MaxSubjectCharacters);
        return shortened.Length == 0 ? UiText.NoSubject : shortened;
    }

    /// <summary>
    /// The sender line: only the first mailbox as "Name &lt;address&gt;", then "+N weitere". It works on the parsed mailboxes,
    /// because name and order are chosen by the sender and could hide the real address. The name is cut at the end, the
    /// address in the middle, so that its domain stays visible.
    /// </summary>
    /// <param name="senders">Parsed mailboxes of the From header.</param>
    /// <returns>
    /// The first mailbox, with a name of at most <see cref="MaxSenderCharacters"/> characters plus "…" and an address of at most
    /// 60 characters beside it, and the number of others.
    /// </returns>
    public static string SenderLine(IReadOnlyList<MailSender> senders)
    {
        if (senders.Count == 0)
        {
            return string.Empty;
        }

        string line = DescribeSender(senders[0]);
        return senders.Count == 1 ? line : $"{line} {UiText.MoreSenders(senders.Count - 1)}";
    }

    /// <summary>Cuts an address in the middle: everything from the last '@' on stays, as far as it fits.</summary>
    /// <param name="address">Raw address.</param>
    /// <param name="maxCharacters">Maximum length of the result, including the "…", at least 2.</param>
    /// <returns>The address, e.g. <c>aaa…@evil.test</c>.</returns>
    public static string ShortenAddress(string address, int maxCharacters)
    {
        string collapsed = SafeDisplayText.Shorten(address, int.MaxValue);
        if (collapsed.Length <= maxCharacters)
        {
            return collapsed;
        }

        int domainStart = collapsed.LastIndexOf('@');
        int roomForText = maxCharacters - Ellipsis.Length;
        int tailLength = Math.Min(domainStart < 0 ? roomForText / 2 : collapsed.Length - domainStart, roomForText);
        int headLength = roomForText - tailLength;

        // A cut must not split a surrogate pair.
        if (headLength > 0 && char.IsHighSurrogate(collapsed[headLength - 1]))
        {
            headLength--;
        }

        int tailStart = collapsed.Length - tailLength;
        if (char.IsLowSurrogate(collapsed[tailStart]))
        {
            tailStart++;
        }

        return string.Concat(collapsed.AsSpan(0, headLength), Ellipsis, collapsed.AsSpan(tailStart));
    }

    private static string DescribeSender(MailSender sender)
    {
        if (SafeDisplayText.Shorten(sender.Name, 1).Length == 0)
        {
            return ShortenAddress(sender.Address, MaxSenderCharacters);
        }

        string name = SafeDisplayText.Shorten(sender.Name, MaxSenderCharacters);
        string address = ShortenAddress(sender.Address, MaxAddressCharactersWithName);
        return address.Length == 0 ? name : $"{name} <{address}>";
    }

    /// <summary>The recipients as one line.</summary>
    /// <param name="recipients">Raw recipients.</param>
    /// <returns>The shortened line.</returns>
    public static string Recipients(IEnumerable<string> recipients)
    {
        return SafeDisplayText.Shorten(string.Join(", ", recipients), MaxRecipientCharacters);
    }

    /// <summary>At most <see cref="MaxAttachmentNames"/> names, then one entry with the number of the others.</summary>
    /// <param name="fileNames">Raw file names.</param>
    /// <returns>The entries to show.</returns>
    public static List<string> AttachmentNames(IReadOnlyCollection<string> fileNames)
    {
        List<string> names = fileNames
            .Take(MaxAttachmentNames)
            .Select(name => SafeDisplayText.Shorten(name, MaxAttachmentNameCharacters))
            .Select(name => name.Length == 0 ? UiText.UnnamedAttachment : name)
            .ToList();
        if (fileNames.Count > MaxAttachmentNames)
        {
            names.Add(UiText.MoreAttachments(fileNames.Count - MaxAttachmentNames));
        }

        return names;
    }
}
