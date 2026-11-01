using MailWarden.Core.Text;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// The one way from a foreign name (header of a sent mail, import file) to the display name of a contact.
/// </summary>
internal static class ContactNames
{
    private const int MaxLength = 200;

    /// <summary>
    /// Cleans the name with <see cref="SafeDisplayText"/> and limits it to one line of at most 200 characters, the "…" of a
    /// cut name included.
    /// </summary>
    /// <param name="name">Name from foreign data or null.</param>
    /// <returns>The cleaned name; empty when nothing is left.</returns>
    internal static string Clean(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        string shortened = SafeDisplayText.Shorten(name, MaxLength);
        return shortened.Length <= MaxLength ? shortened : SafeDisplayText.Shorten(name, MaxLength - 1);
    }
}
