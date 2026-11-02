using MailWarden.Core.Model;
using MailWarden.Infrastructure.Mime;

namespace MailWarden.Presentation;

/// <summary>
/// One attachment of the shown mail in the reading pane. It keeps what it was made from, so an action started on it stays with
/// its mail whatever the pane shows by then.
/// </summary>
public sealed class AttachmentRow
{
    /// <summary>Creates the row.</summary>
    /// <param name="message">The mail of the attachment.</param>
    /// <param name="folderRole">Role of the folder of the mail; null when it is unknown.</param>
    /// <param name="attachment">The entry of the content.</param>
    /// <param name="index">Its position in <see cref="MailContent.Attachments"/>.</param>
    internal AttachmentRow(MailMessage message, FolderRole? folderRole, MailAttachmentInfo attachment, int index)
    {
        Message = message;
        FolderRole = folderRole;
        Attachment = attachment;
        Index = index;
        Name = MailDisplayText.AttachmentName(attachment.FileName);
        SizeText = MailDisplayText.AttachmentSize(attachment.Size);
        SafeFileName = attachment.IsAttachedMessage
            ? AttachmentFileName.ToSafeForMessage(attachment.FileName, index)
            : AttachmentFileName.ToSafe(attachment.FileName, index);
        ExtensionText = Path.GetExtension(SafeFileName).TrimStart('.').ToUpperInvariant();
        Risk = AttachmentPolicy.Classify(SafeFileName);
        HasContent = attachment.HasContent;
    }

    /// <summary>Position in <see cref="MailContent.Attachments"/>; addresses the part.</summary>
    public int Index { get; }

    /// <summary>Name for display, cleaned and shortened; a fixed text when the part has none.</summary>
    public string Name { get; }

    /// <summary>Size for display; empty when the size is unknown.</summary>
    public string SizeText { get; }

    /// <summary>
    /// The last extension of <see cref="SafeFileName"/> in capitals without the dot, the one Windows goes by; empty without one. The
    /// row always shows it, because a name that is cut can hide it.
    /// </summary>
    public string ExtensionText { get; }

    /// <summary>Risk level of <see cref="SafeFileName"/>.</summary>
    public AttachmentRisk Risk { get; }

    /// <summary>True when the row offers Öffnen: it has content and its level allows it.</summary>
    public bool CanOpen => HasContent && Risk != AttachmentRisk.SaveOnly;

    /// <summary>False when there is nothing to copy: the row offers no action at all.</summary>
    public bool HasContent { get; }

    /// <summary>The file name for the disk, also for the proposal of the save dialog.</summary>
    public string SafeFileName { get; }

    /// <summary>The mail of the attachment.</summary>
    internal MailMessage Message { get; }

    /// <summary>The entry this row was made from; the part must still match it when it is copied.</summary>
    internal MailAttachmentInfo Attachment { get; }

    /// <summary>Role of the folder of the mail when it was shown; null when it is unknown.</summary>
    internal FolderRole? FolderRole { get; }
}
