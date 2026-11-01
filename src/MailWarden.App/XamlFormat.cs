using MailWarden.Presentation;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Windows.UI.Text;

namespace MailWarden.App;

/// <summary>
/// Small value conversions for function bindings in XAML.
/// </summary>
public static class XamlFormat
{
    /// <summary>Bold for unread rows.</summary>
    /// <param name="isUnread">True when the row is unread.</param>
    /// <returns>The font weight.</returns>
    public static FontWeight RowWeight(bool isUnread) => isUnread ? FontWeights.Bold : FontWeights.Normal;

    /// <summary>Visible when the text is not empty.</summary>
    /// <param name="text">Text or null.</param>
    /// <returns>The visibility.</returns>
    public static Visibility VisibleWhenText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Text of an unread counter; empty for 0.</summary>
    /// <param name="count">Number of unread messages.</param>
    /// <returns>The text.</returns>
    public static string UnreadText(int count) => count == 0 ? string.Empty : count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Header line with the sender.</summary>
    /// <param name="value">Cleaned sender.</param>
    /// <returns>The line.</returns>
    public static string FromLine(string value) => $"{UiText.FromLabel} {value}";

    /// <summary>Header line with the recipients.</summary>
    /// <param name="value">Cleaned recipients.</param>
    /// <returns>The line.</returns>
    public static string ToLine(string value) => $"{UiText.ToLabel} {value}";

    /// <summary>Header line with the copy recipients.</summary>
    /// <param name="value">Cleaned recipients.</param>
    /// <returns>The line.</returns>
    public static string CcLine(string value) => $"{UiText.CcLabel} {value}";

    /// <summary>Header line with the date.</summary>
    /// <param name="value">Date text.</param>
    /// <returns>The line.</returns>
    public static string DateLine(string value) => $"{UiText.DateLabel} {value}";

    /// <summary>Header line with the attachment names.</summary>
    /// <param name="names">Attachment names.</param>
    /// <returns>The line.</returns>
    public static string AttachmentsLine(IReadOnlyList<string> names) => $"{UiText.AttachmentsLabel} {string.Join(", ", names)}";

    /// <summary>Visible when there is at least one name.</summary>
    /// <param name="names">Attachment names.</param>
    /// <returns>The visibility.</returns>
    public static Visibility VisibleWhenAny(IReadOnlyList<string> names) => names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Text of the button that loads remote images.</summary>
    /// <param name="blockedCount">Number of blocked resources.</param>
    /// <returns>The button text.</returns>
    public static string LoadImagesText(int blockedCount) => UiText.LoadRemoteImages(blockedCount);
}
