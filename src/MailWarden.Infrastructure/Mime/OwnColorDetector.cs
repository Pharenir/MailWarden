using System.Collections.Frozen;
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

    // Also covers the longhands of border-image.
    private const string BorderImagePropertyPrefix = "border-image";

    // They repaint the text or what lies under it without naming a color: filter:brightness(0), all:initial with its black
    // text, or a white box-shadow are readable on the light sheet and not on the dark page. border, outline and text-shadow stay
    // out: they would turn ordinary mails light, and a crafted overlay could already hide text on the light page. The package
    // removes backdrop-filter today, so it is only a safeguard should its allow-list change.
    private static readonly FrozenSet<string> TextRepaintingProperties = new[]
    {
        "all", "filter", "backdrop-filter", "mix-blend-mode", "box-shadow",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // A remote image without <img>: after approval a logo with a transparent ground would land on the dark page.
    private static readonly FrozenSet<string> ImageProperties = new[]
    {
        "content", "list-style-image",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // The list-style shorthand reports list-style-image=initial without any image, and list-style:none is common in ordinary mail.
    private static readonly FrozenSet<string> NoImageValues = new[]
    {
        "none", "normal", "initial",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when an element below <paramref name="body"/> is an <c>img</c> or a <c>style</c>, has the attribute <c>color</c>
    /// or <c>bgcolor</c>, or a style attribute with a color or background declaration, a declaration that repaints the text or holds an image,
    /// or without any readable declaration. The element itself does not count: its attributes never reach the reading document.
    /// </summary>
    /// <param name="body">Body after the allow-lists and before the URL rules, so that the answer does not depend on the image approvals.</param>
    /// <returns>True when the mail has to stay on the light sheet.</returns>
    public static bool HoldsOwnColors(IElement body)
    {
        foreach (IElement element in body.QuerySelectorAll("*"))
        {
            // The limiter and the sanitizer remove every style element today; if one ever stayed, it errs towards light.
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

        // Text is there, but the parser read no declaration from it: unclear, so light. Does not occur today, the allow-list
        // step removes such a value before this check.
        ICssStyleDeclaration style = element.GetStyle();
        if (style.Length == 0)
        {
            return true;
        }

        foreach (ICssProperty declaration in style)
        {
            if (string.Equals(declaration.Name, ColorProperty, StringComparison.OrdinalIgnoreCase)
                || declaration.Name.StartsWith(BackgroundPropertyPrefix, StringComparison.OrdinalIgnoreCase)
                || declaration.Name.StartsWith(BorderImagePropertyPrefix, StringComparison.OrdinalIgnoreCase)
                || TextRepaintingProperties.Contains(declaration.Name)
                || (ImageProperties.Contains(declaration.Name) && !NoImageValues.Contains(declaration.Value.Trim())))
            {
                return true;
            }
        }

        return false;
    }
}
