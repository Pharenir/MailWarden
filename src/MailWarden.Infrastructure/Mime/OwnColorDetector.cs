using AngleSharp.Css.Dom;
using AngleSharp.Dom;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// Decides whether a mail brings colors of its own. Such a mail stays on its light sheet in the dark theme: its dark text on
/// a dark page, or a logo with a transparent background, would be unreadable. Works on the elements and parsed declarations
/// of the document the sanitizer holds, never on its text, so a word in the visible text of the mail does not count. It
/// parses nothing of its own: the HTML has passed the limiter and the parser budget, the style attributes were already read
/// by the allow-list step.
/// </summary>
internal static class OwnColorDetector
{
    private const string ColorProperty = "color";

    // Also covers what a shorthand expands to: "background: red" can come back as its longhands.
    private const string BackgroundPropertyPrefix = "background";

    /// <summary>
    /// True when an element below <paramref name="body"/> is an <c>img</c> or a <c>style</c>, has the attribute <c>color</c>
    /// or <c>bgcolor</c>, or a style attribute with a color or background declaration or without any readable declaration.
    /// The element itself does not count: its attributes never reach the reading document.
    /// </summary>
    /// <param name="body">Body after the allow-lists and before the URL rules, so that the answer does not depend on the image approvals.</param>
    /// <returns>True when the mail has to stay on the light sheet.</returns>
    public static bool HoldsOwnColors(IElement body)
    {
        foreach (IElement element in body.QuerySelectorAll("*"))
        {
            if (element.LocalName is "img" or "style"
                || element.HasAttribute("color")
                || element.HasAttribute("bgcolor")
                || HasColorDeclaration(element))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasColorDeclaration(IElement element)
    {
        if (string.IsNullOrWhiteSpace(element.GetAttribute("style")))
        {
            return false;
        }

        // Text is there, but the parser read no declaration from it: unclear.
        ICssStyleDeclaration style = element.GetStyle();
        if (style.Length == 0)
        {
            return true;
        }

        foreach (ICssProperty declaration in style)
        {
            if (string.Equals(declaration.Name, ColorProperty, StringComparison.OrdinalIgnoreCase)
                || declaration.Name.StartsWith(BackgroundPropertyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
