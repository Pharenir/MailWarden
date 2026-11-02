namespace MailWarden.Presentation;

/// <summary>
/// The problems the banner currently reports. Each source sets or clears its own text, so a success in one place does not
/// hide a problem in another.
/// </summary>
internal sealed class ShellBanner
{
    private readonly SortedDictionary<BannerSource, string> texts = [];

    /// <summary>Raised when <see cref="Text"/> may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Who reports a banner text; the order is the order of the texts.</summary>
    public enum BannerSource
    {
        /// <summary>No accounts exist.</summary>
        NoAccounts,

        /// <summary>The settings could not be loaded.</summary>
        SettingsLoad,

        /// <summary>The settings could not be saved.</summary>
        SettingsSave,

        /// <summary>The navigation could not be loaded.</summary>
        Navigation,

        /// <summary>The message list could not be loaded.</summary>
        MessageList,

        /// <summary>Since the app started, classification passes had mail that could not be evaluated or that the brake held.</summary>
        Classification,

        /// <summary>The filter waits for the address book.</summary>
        ClassificationWaiting,

        /// <summary>The queued actions could not be read.</summary>
        ActionsRead,

        /// <summary>Queued actions have failed; the text names their number.</summary>
        Actions,

        /// <summary>The last action could not be stored in the queue.</summary>
        ActionRejected,

        /// <summary>A permanent delete has probably only reached the Trash.</summary>
        DeleteReachedTrash,

        /// <summary>A save failed after another mail was selected, so the reading pane has no place for it any more.</summary>
        AttachmentSave,
    }

    /// <summary>All current texts joined, or null when nothing is to report.</summary>
    public string? Text => texts.Count == 0 ? null : string.Join(" ", texts.Values);

    /// <summary>Sets the text of a source.</summary>
    /// <param name="source">Reporting source.</param>
    /// <param name="text">Fixed text.</param>
    public void Set(BannerSource source, string text)
    {
        texts[source] = text;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears the text of a source.</summary>
    /// <param name="source">Reporting source.</param>
    public void Clear(BannerSource source)
    {
        if (texts.Remove(source))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
