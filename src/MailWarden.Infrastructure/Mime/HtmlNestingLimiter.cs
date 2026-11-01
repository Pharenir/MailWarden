using System.Collections.Frozen;
using System.Text;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// Rewrites HTML before it is parsed so that a crafted mail cannot stall the reading pane or overflow the stack. Time
/// measured with the parser and the sanitizer grows with the square of: the depth (100,000 nested <c>div</c> take more
/// than 20 s), the number of siblings and comments, and the number of stray <c>&lt;</c> characters. Therefore start tags
/// and comments beyond the limits are dropped and stray <c>&lt;</c> are encoded. The scan is linear and only estimates the
/// depth; text is never dropped.
/// </summary>
internal static class HtmlNestingLimiter
{
    // An end tag closing more open elements than this is ignored, which keeps the scan linear. It can only overestimate.
    private const int MaxClosingSearchDepth = 64;

    private const string EncodedLessThan = "&lt;";

    // Void elements have no content, and these close themselves implicitly (<p><p> are siblings).
    private static readonly FrozenSet<string> NotNesting = new[]
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
        "p", "li", "dd", "dt", "tr", "td", "th", "option", "optgroup", "tbody", "thead", "tfoot", "rp", "rt", "colgroup",
    }.ToFrozenSet(StringComparer.Ordinal);

    // The parser does not let an end tag close an element outside these elements (e.g. <div><object></div> stays open).
    private static readonly FrozenSet<string> ScopeBoundaries = new[]
    {
        "applet", "button", "caption", "marquee", "object", "table", "template",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Returns the HTML with stray <c>&lt;</c> encoded and every tag removed that would exceed a limit.</summary>
    /// <param name="html">Raw HTML.</param>
    /// <param name="maxOpenTags">Maximum estimated depth of open elements.</param>
    /// <param name="maxMarkupItems">Maximum number of start tags and comments in the whole HTML.</param>
    /// <returns>The rewritten HTML; the same instance when nothing had to change.</returns>
    public static string Limit(string html, int maxOpenTags, int maxMarkupItems)
    {
        var scan = new Scan(html);
        var openTags = new List<string>();
        int markupItemCount = 0;
        int tagStart = html.IndexOf('<', StringComparison.Ordinal);
        while (tagStart >= 0 && tagStart + 1 < html.Length)
        {
            int nextSearchStart = tagStart + 1;
            char next = html[tagStart + 1];
            if (char.IsAsciiLetter(next))
            {
                string tagName = ReadTagName(html, tagStart + 1);
                bool isNesting = !NotNesting.Contains(tagName);
                if (markupItemCount >= maxMarkupItems || (isNesting && openTags.Count >= maxOpenTags))
                {
                    nextSearchStart = scan.DropTag(tagStart);
                }
                else
                {
                    markupItemCount++;
                    if (isNesting)
                    {
                        openTags.Add(tagName);
                    }
                }
            }
            else if (next == '/' && tagStart + 2 < html.Length && char.IsAsciiLetter(html[tagStart + 2]))
            {
                CloseTag(openTags, ReadTagName(html, tagStart + 2));
            }
            else if (next is '/' or '!' or '?')
            {
                // Comments and everything the parser treats like one.
                if (markupItemCount >= maxMarkupItems)
                {
                    nextSearchStart = scan.DropTag(tagStart);
                }
                else
                {
                    markupItemCount++;
                }
            }
            else
            {
                scan.EncodeLessThan(tagStart);
            }

            tagStart = nextSearchStart < html.Length ? html.IndexOf('<', nextSearchStart) : -1;
        }

        return scan.ToResult();
    }

    private static void CloseTag(List<string> openTags, string tagName)
    {
        int lowestIndex = Math.Max(0, openTags.Count - MaxClosingSearchDepth);
        for (int index = openTags.Count - 1; index >= lowestIndex; index--)
        {
            if (openTags[index] == tagName)
            {
                openTags.RemoveRange(index, openTags.Count - index);
                return;
            }

            if (ScopeBoundaries.Contains(openTags[index]))
            {
                return;
            }
        }
    }

    private static string ReadTagName(string html, int nameStart)
    {
        int nameEnd = nameStart;
        while (nameEnd < html.Length && (char.IsAsciiLetterOrDigit(html[nameEnd]) || html[nameEnd] is ':' or '-'))
        {
            nameEnd++;
        }

        return html[nameStart..nameEnd].ToLowerInvariant();
    }

    /// <summary>Copies the unchanged parts of the HTML; allocates only when something changes.</summary>
    private sealed class Scan(string html)
    {
        private StringBuilder? rewritten;
        private int copiedUpTo;

        /// <summary>Drops the tag up to the next '&gt;'; a '&gt;' in an attribute value only turns the rest into text.</summary>
        /// <param name="tagStart">Position of the '&lt;'.</param>
        /// <returns>The position after the dropped tag.</returns>
        public int DropTag(int tagStart)
        {
            int tagEnd = html.IndexOf('>', tagStart);
            int resumeAt = tagEnd < 0 ? html.Length : tagEnd + 1;
            CopyUpTo(tagStart);
            copiedUpTo = resumeAt;
            return resumeAt;
        }

        public void EncodeLessThan(int position)
        {
            CopyUpTo(position);
            rewritten!.Append(EncodedLessThan);
            copiedUpTo = position + 1;
        }

        public string ToResult()
        {
            return rewritten is null ? html : rewritten.Append(html, copiedUpTo, html.Length - copiedUpTo).ToString();
        }

        private void CopyUpTo(int position)
        {
            rewritten ??= new StringBuilder(html.Length);
            rewritten.Append(html, copiedUpTo, position - copiedUpTo);
        }
    }
}
