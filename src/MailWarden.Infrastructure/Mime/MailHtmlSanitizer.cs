using System.Collections.Frozen;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens;
using AngleSharp.Text;
using Ganss.Xss;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// Result of <see cref="MailHtmlSanitizer.Sanitize"/>.
/// </summary>
/// <param name="Html">The cleaned body content.</param>
/// <param name="BlockedRemoteResourceCount">Number of remote images and remote style URLs that were blocked.</param>
/// <param name="WasReduced">True when a limit for crafted input dropped markup or the inline image budget was hit.</param>
/// <param name="HasOwnColors">
/// True when the mail brings colors or images of its own, also when a blocked image or style URL is the reason, and when the
/// HTML could not be parsed within its budget. Only the plain answer leaves the sanitizer, and it changes nothing in
/// <paramref name="Html"/>.
/// </param>
public sealed record SanitizedHtml(string Html, int BlockedRemoteResourceCount, bool WasReduced = false, bool HasOwnColors = true);

/// <summary>
/// Cleans the HTML body of a mail with an allow-list. Before parsing, <see cref="HtmlNestingLimiter"/> bounds the work
/// for crafted input, and the parser itself runs under a budget: nothing has to be predicted there, what it costs is
/// measured. Then three steps run on one parsed document: remove the dangerous
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

    // Budget of the parser, checked at every token on the thread that parses. Copies of formatting elements are made by the
    // parser without any token or callback of their own (a 172 KB mail reached over 5 GB), but always during a token.
    // Largest of 2,664 real mails: 313,586 characters and 882 ms for the whole build.
    private const long MaxParserAllocationBytes = 256L * 1024 * 1024;

    // Estimate of the output: the parser shares attribute values between copies of an element, the serializer writes each of
    // them out, so a 1 MB mail could become over a billion characters. This holds the images of InlineImageBudget.
    private const long MaxOutputChars = 64_000_000;

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

    private static readonly TimeSpan MaxParserTime = TimeSpan.FromSeconds(3);

    // With CSS support: the package removes all styles of a document parsed without it.
    private static readonly IConfiguration ParserConfiguration = Configuration.Default.WithCss(new CssParserOptions
    {
        IsIncludingUnknownDeclarations = true,
        IsIncludingUnknownRules = true,
        IsToleratingInvalidSelectors = true,
    });

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
    /// true; otherwise they are removed and counted. With <paramref name="allowEmbeddedImages"/> false the same holds for
    /// <c>cid:</c> and <c>data:</c> images, in <c>img src</c> and CSS <c>url()</c>, because a broken image decoder is the one
    /// code path that an opened mail could reach without a click. Inserted inline images use at most 34,000,000 characters per document;
    /// images over this budget are removed. When parsing exceeds 256 MB of allocation or 3 s, or the output would exceed
    /// 64,000,000 characters, the result is the encoded text of the HTML without any markup. It also tells whether the mail
    /// brings colors of its own, see <see cref="OwnColorDetector.HoldsOwnColors"/>; the answer is the same for all image
    /// approvals, and true for that encoded text, because the mail could not be judged.
    /// </summary>
    /// <param name="html">Raw HTML of a mail body.</param>
    /// <param name="inlineImages">Content-ID → data URI, the targets of <c>cid:</c> image sources.</param>
    /// <param name="allowRemoteImages">True when the user approved remote images for this message.</param>
    /// <param name="allowEmbeddedImages">False for a mail in Junk or Quarantine that the user has not approved.</param>
    /// <returns>The cleaned body content, the number of blocked resources and whether a limit reduced it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> or <paramref name="inlineImages"/> is null.</exception>
    public static SanitizedHtml Sanitize(
        string html,
        IReadOnlyDictionary<string, string> inlineImages,
        bool allowRemoteImages,
        bool allowEmbeddedImages = true)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(inlineImages);

        LimitedHtml limited = HtmlNestingLimiter.Limit(html);
        using IHtmlDocument? document = ParseWithinBudget(DocumentPrefix + limited.Html);
        if (document is null)
        {
            return ToTextOnly(limited);
        }

        IHtmlElement? body = document.Body;
        if (body is null)
        {
            return new SanitizedHtml(string.Empty, 0, limited.WasReduced);
        }

        bool wasFlattened = PruneElements(body);
        PackageSanitizer.SanitizeDom(document, body);

        // Before the URL rules: the page must not change from dark to light when the user approves the images.
        bool hasOwnColors = OwnColorDetector.HoldsOwnColors(body);
        var inlineImageBudget = new InlineImageBudget();
        var imageRules = new ImageRules(inlineImages, inlineImageBudget, allowRemoteImages, allowEmbeddedImages);
        int blockedCount = ApplyUrlRules(body, imageRules);
        if (ExceedsOutputBudget(body))
        {
            return ToTextOnly(limited);
        }

        return new SanitizedHtml(
            body.ChildNodes.ToHtml(PackageSanitizer.OutputFormatter),
            blockedCount,
            limited.WasReduced || wasFlattened || inlineImageBudget.WasExhausted,
            hasOwnColors);
    }

    private static SanitizedHtml ToTextOnly(LimitedHtml limited)
    {
        return new SanitizedHtml(WebUtility.HtmlEncode(MimeBodyText.ExtractFromHtml(limited.Html)), 0, WasReduced: true, HasOwnColors: true);
    }

    // Sum of the attribute values of all elements and the text, stopping at the budget. Iterative like PruneElements.
    private static bool ExceedsOutputBudget(IElement body)
    {
        long estimatedChars = 0;
        var pending = new Stack<INode>();
        pending.Push(body);
        while (pending.Count > 0)
        {
            INode node = pending.Pop();
            if (node is IText text)
            {
                estimatedChars += text.Data.Length;
            }
            else if (node is IElement element)
            {
                foreach (IAttr attribute in element.Attributes)
                {
                    estimatedChars += attribute.Value.Length;
                }
            }

            if (estimatedChars > MaxOutputChars)
            {
                return true;
            }

            foreach (INode child in node.ChildNodes)
            {
                pending.Push(child);
            }
        }

        return false;
    }

    // Returns null when the budget ran out. ParseDocument works on the calling thread and so does the callback, so the
    // counter of this thread is the right one, wherever the build runs; it is only compared with its own start value.
    private static IHtmlDocument? ParseWithinBudget(string html)
    {
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long timeStart = Stopwatch.GetTimestamp();
        void CheckBudget(HtmlToken token, TextRange range)
        {
            if (GC.GetAllocatedBytesForCurrentThread() - allocationStart > MaxParserAllocationBytes
                || Stopwatch.GetElapsedTime(timeStart) > MaxParserTime)
            {
                throw new ParserBudgetExceededException();
            }
        }

        // IsScripting as in the parser factory of the package; everything else keeps its default there as well.
        var options = new HtmlParserOptions { IsScripting = true, OnToken = CheckBudget };
        try
        {
            return new HtmlParser(options, BrowsingContext.New(ParserConfiguration)).ParseDocument(html);
        }
        catch (ParserBudgetExceededException)
        {
            return null;
        }
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
    private static int ApplyUrlRules(IElement body, ImageRules rules)
    {
        int blockedCount = 0;
        foreach (IElement element in body.QuerySelectorAll("*").ToArray())
        {
            if (element.LocalName == "img")
            {
                blockedCount += ApplyImageRule(element, rules);
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

            blockedCount += ApplyStyleRules(element, rules);
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

    // Returns the number of blocked resources (0 or 1). A blocked image stays as an element without source, with its alt text.
    private static int ApplyImageRule(IElement image, ImageRules rules)
    {
        string? source = image.GetAttribute("src");
        if (source is null)
        {
            return 0;
        }

        switch (ReadScheme(source)?.ToLowerInvariant())
        {
            case "cid":
                if (FindInlineImage(source, rules.InlineImages) is not string dataUri)
                {
                    image.Remove();
                    return 0;
                }

                if (!rules.AllowEmbeddedImages)
                {
                    image.RemoveAttribute("src");
                    return 1;
                }

                if (rules.Budget.TryInsert(dataUri))
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
                    return 0;
                }

                if (!rules.AllowEmbeddedImages)
                {
                    image.RemoveAttribute("src");
                    return 1;
                }

                return 0;

            case "http":
            case "https":
                if (rules.AllowRemoteImages)
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

    // Removes url() declarations and "position: fixed" from the inline style; returns the number of blocked URLs. Style URLs
    // other than remote ones are always removed; while embedded images are blocked, those of cid: and data: count, so that
    // the user sees that something is withheld.
    private static int ApplyStyleRules(IElement element, ImageRules rules)
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
                    bool isEmbedded = string.Equals(scheme, "data", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(scheme, "cid", StringComparison.OrdinalIgnoreCase);
                    blockedCount += isEmbedded && !rules.AllowEmbeddedImages ? 1 : 0;
                }
            }

            // A value mixing a remote URL with another one is removed but still counted.
            if (remoteUrlCount > 0 && !rules.AllowRemoteImages)
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

    /// <summary>What the final step may keep of the images of one document.</summary>
    /// <param name="InlineImages">Content-ID → data URI.</param>
    /// <param name="Budget">Budget for inserted inline images.</param>
    /// <param name="AllowRemoteImages">True when remote images stay.</param>
    /// <param name="AllowEmbeddedImages">True when <c>cid:</c> and <c>data:</c> images stay.</param>
    private sealed record ImageRules(
        IReadOnlyDictionary<string, string> InlineImages,
        InlineImageBudget Budget,
        bool AllowRemoteImages,
        bool AllowEmbeddedImages);

    /// <summary>Ends the parser from inside its token callback.</summary>
    private sealed class ParserBudgetExceededException : Exception
    {
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
