using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Ganss.Xss;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// Result of <see cref="MailHtmlSanitizer.Sanitize"/>.
/// </summary>
/// <param name="Html">The cleaned body content.</param>
/// <param name="BlockedRemoteResourceCount">Number of remote images and remote style URLs that were blocked.</param>
/// <param name="WasReduced">True when a limit for crafted input dropped markup or the inline image budget was hit.</param>
public sealed record SanitizedHtml(string Html, int BlockedRemoteResourceCount, bool WasReduced = false);

/// <summary>
/// Cleans the HTML body of a mail with an allow-list. Before parsing, <see cref="HtmlNestingLimiter"/> bounds the work
/// for crafted input. Then three steps run on one parsed document: remove the dangerous
/// elements with their content and limit the nesting; let the HtmlSanitizer package apply the allow-lists for elements,
/// attributes and CSS properties; then apply the rules of this application for link targets, image sources and style URLs.
/// </summary>
public static partial class MailHtmlSanitizer
{
    // Deeper DOM trees are flattened to their text: walking and serializing them recursively could overflow the stack,
    // which no exception handler catches. Browsers limit the depth in the same way.
    private const int MaxNestingDepth = 256;

    // One small inline image referenced many times could otherwise multiply into gigabytes. A strict limit, also for a
    // single image: 25 binary megabytes are 34,952,558 characters in Base64 with their prefix.
    private const long MaxInsertedDataUriChars = 34_000_000;

    private const string DocumentPrefix = "<!doctype html><html><body>";

    // Removed with their content; a tag that is merely unknown (e.g. o:p) or form only loses the tag, because some mailers
    // wrap the whole mail in a form. The last lines hold the elements whose content is not body text or would be shown
    // twice.
    private static readonly FrozenSet<string> RemovedElements = new[]
    {
        "script", "style", "iframe", "frame", "object", "embed", "applet", "input", "button", "select", "textarea",
        "link", "meta", "base", "svg", "math", "video", "audio", "source", "template",
        "title", "head", "noscript", "noembed", "noframes", "frameset", "option", "optgroup", "datalist", "canvas", "track",
        "param", "keygen", "portal",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AllowedTags =
    [
        "a", "abbr", "acronym", "address", "article", "aside", "b", "bdi", "bdo", "big", "blockquote", "br", "caption",
        "center", "cite", "code", "col", "colgroup", "dd", "del", "details", "dfn", "dir", "div", "dl", "dt", "em",
        "fieldset", "figcaption", "figure", "font", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "i", "img",
        "ins", "kbd", "label", "legend", "li", "main", "mark", "menu", "nav", "nobr", "ol", "p", "pre", "q", "rp", "rt",
        "ruby", "s", "samp", "section", "small", "span", "strike", "strong", "sub", "summary", "sup", "table", "tbody", "td",
        "tfoot", "th", "thead", "time", "tr", "tt", "u", "ul", "var", "wbr",
    ];

    // No on*, target, srcset, srcdoc, background, ping, formaction, and no other attribute that holds a URL.
    private static readonly string[] AllowedAttributes =
    [
        "abbr", "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "char", "charoff", "class", "clear", "color",
        "cols", "colspan", "compact", "datetime", "dir", "face", "headers", "height", "href", "hspace", "lang", "noshade",
        "nowrap", "reversed", "rows", "rowspan", "scope", "size", "span", "src", "start", "style", "summary", "title", "type",
        "valign", "value", "vspace", "width",
    ];

    private static readonly FrozenSet<string> LinkSchemes =
        new[] { "http", "https", "mailto" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> DataImageTypes =
        new[] { "image/png", "image/jpeg", "image/gif", "image/webp" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly char[] UrlEdgeCharacters = [.. Enumerable.Range(0, 33).Select(code => (char)code)];

    // Every position value except "fixed", which could cover the whole reading document.
    private static readonly FrozenSet<string> AllowedPositions =
        new[] { "static", "relative", "absolute", "sticky" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Settings stay unchanged and no event handler is attached, so the instance is shared across threads. The final step
    // decides about all URLs, so href and src are no URI attributes for the package; it only drops style URLs with a
    // scheme outside this list, which the final step would remove as well.
    private static readonly HtmlSanitizer PackageSanitizer = new(new HtmlSanitizerOptions
    {
        AllowedTags = new HashSet<string>(AllowedTags, StringComparer.OrdinalIgnoreCase),
        AllowedAttributes = new HashSet<string>(AllowedAttributes, StringComparer.OrdinalIgnoreCase),
        AllowedCssProperties = new HashSet<string>(HtmlSanitizerDefaults.AllowedCssProperties, StringComparer.OrdinalIgnoreCase),
        AllowedAtRules = new HashSet<CssRuleType>(),
        AllowedSchemes = new HashSet<string>(["http", "https", "data", "cid"], StringComparer.OrdinalIgnoreCase),
        UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        UriListAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    })
    {
        KeepChildNodes = true,
    };

    /// <summary>
    /// Cleans the HTML on any input; malformed HTML never throws. Removed entirely: script, style, iframe, frame, object,
    /// embed, applet, input, button, select, textarea, link, meta, base, svg, math, video, audio, source, template; form
    /// loses only its tag. Remote images and remote style URLs stay only when <paramref name="allowRemoteImages"/> is
    /// true; otherwise they are removed and counted. Inserted inline images use at most 34,000,000 characters per document;
    /// images over this budget are removed.
    /// </summary>
    /// <param name="html">Raw HTML of a mail body.</param>
    /// <param name="inlineImages">Content-ID → data URI, the targets of <c>cid:</c> image sources.</param>
    /// <param name="allowRemoteImages">True when the user approved remote images for this message.</param>
    /// <returns>The cleaned body content, the number of blocked remote resources and whether a limit reduced it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> or <paramref name="inlineImages"/> is null.</exception>
    public static SanitizedHtml Sanitize(string html, IReadOnlyDictionary<string, string> inlineImages, bool allowRemoteImages)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(inlineImages);

        LimitedHtml limited = HtmlNestingLimiter.Limit(html);
        using IHtmlDocument document = PackageSanitizer.HtmlParserFactory().ParseDocument(DocumentPrefix + limited.Html);
        IHtmlElement? body = document.Body;
        if (body is null)
        {
            return new SanitizedHtml(string.Empty, 0, limited.WasReduced);
        }

        bool wasFlattened = PruneElements(body);
        PackageSanitizer.SanitizeDom(document, body);
        var inlineImageBudget = new InlineImageBudget();
        int blockedCount = ApplyUrlRules(body, inlineImages, inlineImageBudget, allowRemoteImages);
        return new SanitizedHtml(
            body.ChildNodes.ToHtml(PackageSanitizer.OutputFormatter),
            blockedCount,
            limited.WasReduced || wasFlattened || inlineImageBudget.WasExhausted);
    }

    // Iterative on purpose (see MaxNestingDepth). Returns true when something was flattened to text.
    private static bool PruneElements(IElement root)
    {
        bool wasFlattened = false;
        var pending = new Stack<(IElement Element, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            (IElement element, int depth) = pending.Pop();
            if (depth >= MaxNestingDepth)
            {
                wasFlattened |= element.Children.Length > 0;
                FlattenToText(element);
                continue;
            }

            foreach (IElement child in element.Children.ToArray())
            {
                if (RemovedElements.Contains(child.LocalName))
                {
                    child.Remove();
                }
                else
                {
                    pending.Push((child, depth + 1));
                }
            }
        }

        return wasFlattened;
    }

    // Replaces the descendants of the element by their text; the content of removed elements is not text.
    private static void FlattenToText(IElement element)
    {
        var text = new StringBuilder();
        var pending = new Stack<INode>();
        PushChildrenInReverse(element, pending);
        while (pending.Count > 0)
        {
            INode node = pending.Pop();
            if (node is IText textNode)
            {
                text.Append(textNode.Data);
            }
            else if (node is IElement child && !RemovedElements.Contains(child.LocalName))
            {
                PushChildrenInReverse(child, pending);
            }
        }

        element.TextContent = text.ToString();
    }

    private static void PushChildrenInReverse(INode node, Stack<INode> pending)
    {
        INode[] children = node.ChildNodes.ToArray();
        for (int index = children.Length - 1; index >= 0; index--)
        {
            pending.Push(children[index]);
        }
    }

    // Final step on the elements the package left: decides about every URL that can still load or open something.
    private static int ApplyUrlRules(
        IElement body,
        IReadOnlyDictionary<string, string> inlineImages,
        InlineImageBudget inlineImageBudget,
        bool allowRemoteImages)
    {
        int blockedCount = 0;
        foreach (IElement element in body.QuerySelectorAll("*").ToArray())
        {
            if (element.LocalName == "img")
            {
                blockedCount += ApplyImageRule(element, inlineImages, inlineImageBudget, allowRemoteImages);
                if (element.Parent is null)
                {
                    continue;
                }
            }
            else
            {
                element.RemoveAttribute("src");
            }

            if (element.LocalName == "a")
            {
                ApplyLinkRule(element);
            }
            else
            {
                element.RemoveAttribute("href");
            }

            blockedCount += ApplyStyleRules(element, allowRemoteImages);
        }

        return blockedCount;
    }

    private static void ApplyLinkRule(IElement link)
    {
        string? href = link.GetAttribute("href");
        if (href is not null && (ReadScheme(href) is not string scheme || !LinkSchemes.Contains(scheme)))
        {
            link.RemoveAttribute("href");
        }
    }

    // Returns the number of blocked remote resources (0 or 1).
    private static int ApplyImageRule(
        IElement image,
        IReadOnlyDictionary<string, string> inlineImages,
        InlineImageBudget inlineImageBudget,
        bool allowRemoteImages)
    {
        string? source = image.GetAttribute("src");
        if (source is null)
        {
            return 0;
        }

        switch (ReadScheme(source)?.ToLowerInvariant())
        {
            case "cid":
                if (FindInlineImage(source, inlineImages) is string dataUri && inlineImageBudget.TryInsert(dataUri))
                {
                    image.SetAttribute("src", dataUri);
                }
                else
                {
                    image.Remove();
                }

                return 0;

            case "data":
                if (!IsAllowedDataImage(source))
                {
                    image.Remove();
                }

                return 0;

            case "http":
            case "https":
                if (allowRemoteImages)
                {
                    return 0;
                }

                image.RemoveAttribute("src");
                return 1;

            default:
                image.Remove();
                return 0;
        }
    }

    // RFC 2392 percent-encodes the Content-ID in the URL; some mailers do not, so the raw value is tried first.
    private static string? FindInlineImage(string source, IReadOnlyDictionary<string, string> inlineImages)
    {
        string contentId = StripUrlWhitespace(source)["cid:".Length..];
        return inlineImages.TryGetValue(contentId, out string? rawMatch)
            ? rawMatch
            : inlineImages.GetValueOrDefault(Uri.UnescapeDataString(contentId));
    }

    private static bool IsAllowedDataImage(string uri)
    {
        Match match = DataUriType().Match(StripUrlWhitespace(uri));
        return match.Success && DataImageTypes.Contains(match.Groups[1].Value);
    }

    // Removes url() declarations and "position: fixed" from the inline style; returns the number of blocked remote URLs.
    private static int ApplyStyleRules(IElement element, bool allowRemoteImages)
    {
        if (!element.HasAttribute("style"))
        {
            return 0;
        }

        int blockedCount = 0;
        ICssStyleDeclaration style = element.GetStyle();
        foreach (ICssProperty declaration in style.ToArray())
        {
            bool isAllowed = true;
            if (string.Equals(declaration.Name, "position", StringComparison.OrdinalIgnoreCase))
            {
                isAllowed = AllowedPositions.Contains(declaration.Value.Trim());
            }

            string remainingValue = CssUrl().Replace(declaration.Value, string.Empty);
            bool hasUnreadableUrl = remainingValue.Contains("url", StringComparison.OrdinalIgnoreCase)
                && remainingValue.Contains('(');
            int remoteUrlCount = 0;
            foreach (Match match in CssUrl().Matches(declaration.Value))
            {
                string? scheme = ReadScheme(match.Groups[2].Value);
                if (string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
                {
                    remoteUrlCount++;
                }
                else
                {
                    isAllowed = false;
                }
            }

            // A value mixing a remote URL with another one is removed but still counted.
            if (remoteUrlCount > 0 && !allowRemoteImages)
            {
                blockedCount += remoteUrlCount;
                isAllowed = false;
            }

            if (!isAllowed || hasUnreadableUrl)
            {
                style.RemoveProperty(declaration.Name);
            }
        }

        if (style.Length == 0)
        {
            element.RemoveAttribute("style");
        }
        else
        {
            element.SetAttribute("style", style.ToCss());
        }

        return blockedCount;
    }

    // Lower-level URL parsers ignore tabs and line breaks anywhere and control characters at the ends; so do this check.
    private static string StripUrlWhitespace(string url)
    {
        string withoutBreaks = url.Replace("\t", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        return withoutBreaks.Trim(UrlEdgeCharacters);
    }

    // The URL scheme, or null for a relative URL.
    private static string? ReadScheme(string url)
    {
        Match match = UrlScheme().Match(StripUrlWhitespace(url));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The inline images inserted into one document. Each image is checked once: the check copies the URI, which would
    /// cost gigabytes for many references to one large image.
    /// </summary>
    private sealed class InlineImageBudget
    {
        private readonly Dictionary<string, bool> checkedImages = new(ReferenceEqualityComparer.Instance);
        private long insertedChars;

        /// <summary>True when an allowed image was refused because the budget is used up.</summary>
        public bool WasExhausted { get; private set; }

        public bool TryInsert(string dataUri)
        {
            if (!checkedImages.TryGetValue(dataUri, out bool isAllowed))
            {
                isAllowed = IsAllowedDataImage(dataUri);
                checkedImages.Add(dataUri, isAllowed);
            }

            if (!isAllowed)
            {
                return false;
            }

            if (insertedChars + dataUri.Length > MaxInsertedDataUriChars)
            {
                WasExhausted = true;
                return false;
            }

            insertedChars += dataUri.Length;
            return true;
        }
    }

    [GeneratedRegex("^([a-zA-Z][a-zA-Z0-9+.-]*):", RegexOptions.CultureInvariant)]
    private static partial Regex UrlScheme();

    [GeneratedRegex("^data:([a-zA-Z0-9.+-]+/[a-zA-Z0-9.+-]+)(;[^,]*)?,", RegexOptions.CultureInvariant)]
    private static partial Regex DataUriType();

    [GeneratedRegex("""url\(\s*(["']?)(.*?)\1\s*\)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CssUrl();
}
