using MailWarden.Core.Text;

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

    /// <summary>Attachment names listed before "und N weitere".</summary>
    public const int MaxAttachmentNames = 50;

    // No file system allows longer names, so only crafted names are cut.
    private const int MaxAttachmentNameCharacters = 260;

    // Below this length of the address the name is the part that gets cut.
    private const int MaxProtectedAddressCharacters = MaxSenderCharacters - 10;

    private const string AddressSeparator = " <";

    /// <summary>The shortened subject; a fixed text when it is empty.</summary>
    /// <param name="subject">Raw subject.</param>
    /// <returns>The text to show.</returns>
    public static string Subject(string subject)
    {
        string shortened = SafeDisplayText.Shorten(subject, MaxSubjectCharacters);
        return shortened.Length == 0 ? UiText.NoSubject : shortened;
    }

    /// <summary>
    /// A sender line such as "Name &lt;address&gt;". The name is cut first, so that a long name cannot hide the address.
    /// </summary>
    /// <param name="line">Raw sender line.</param>
    /// <returns>The text to show, at most <see cref="MaxSenderCharacters"/> characters plus "…".</returns>
    public static string SenderLine(string line)
    {
        string collapsed = SafeDisplayText.Shorten(line, int.MaxValue);
        if (collapsed.Length <= MaxSenderCharacters)
        {
            return collapsed;
        }

        int separator = collapsed.LastIndexOf(AddressSeparator, StringComparison.Ordinal);
        int addressLength = collapsed.Length - separator;
        return separator > 0 && collapsed.EndsWith('>') && addressLength <= MaxProtectedAddressCharacters
            ? SafeDisplayText.Shorten(collapsed[..separator], MaxSenderCharacters - addressLength) + collapsed[separator..]
            : SafeDisplayText.Shorten(collapsed, MaxSenderCharacters);
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
