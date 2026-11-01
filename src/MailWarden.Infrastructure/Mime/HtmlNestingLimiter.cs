using System.Collections.Frozen;
using System.Text;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// Rewrites HTML before it is parsed so that a crafted mail cannot stall the reading pane or overflow the stack. Time
/// measured with the parser and the sanitizer grows with the square of: the depth (100,000 nested <c>div</c> take more
/// than 20 s), the number of siblings, comments and attributes of a tag, the declarations of a style (100,000 take more
/// than 40 s), and the number of pieces a text is split into by end tags or stray <c>&lt;</c> characters. Therefore tags
/// beyond the limits are dropped, oversized style values are dropped and stray <c>&lt;</c> are encoded. The scan is linear
/// and follows the tag syntax of HTML, but it only estimates the depth. Text outside of tags is never dropped.
/// </summary>
/// <remarks>
/// The scan does not know raw text such as the content of <c>script</c>, because the parser treats that differently in
/// SVG. A quote there can make the scan read real tags as an attribute value, so every <c>&lt;x</c> inside a kept quoted
/// value counts against <see cref="MaxTagsInAttributeValues"/>; a tag dropped for it is not parsed at all.
/// </remarks>
internal static class HtmlNestingLimiter
{
    /// <summary>Estimated depth at which start tags are dropped. Far above real mails.</summary>
    internal const int MaxOpenTags = 1024;

    /// <summary>Start tags and comments kept; the text of the rest stays visible without its markup.</summary>
    internal const int MaxMarkupItems = 20_000;

    /// <summary>End tags kept.</summary>
    internal const int MaxEndTags = MaxMarkupItems;

    /// <summary>A tag with more attributes is dropped.</summary>
    internal const int MaxAttributesPerTag = 256;

    /// <summary>A longer style value is dropped; the tag stays.</summary>
    internal const int MaxStyleValueChars = 64 * 1024;

    /// <summary>A style value with more declarations is dropped; the tag stays. The parser is quadratic in them.</summary>
    internal const int MaxStyleDeclarations = 256;

    /// <summary>Characters of all kept tags together, without <c>data:</c> values of <c>src</c>.</summary>
    internal const int MaxTagTextChars = 2_000_000;

    /// <summary>Tag-like text inside kept quoted attribute values, see the remarks of the class.</summary>
    internal const int MaxTagsInAttributeValues = MaxOpenTags;

    // An end tag closing more open elements than this is ignored, which keeps the scan linear. It can only overestimate.
    private const int MaxClosingSearchDepth = 64;

    private const string EncodedLessThan = "&lt;";
    private const string CommentStart = "<!--";

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

    /// <summary>Returns the HTML with stray <c>&lt;</c> encoded and everything removed that would exceed a limit.</summary>
    /// <param name="html">Raw HTML.</param>
    /// <returns>The rewritten HTML; the same instance when nothing had to change.</returns>
    public static string Limit(string html)
    {
        var rewriter = new Rewriter(html);
        var budgets = new Budgets();
        var openTags = new List<string>();
        int position = html.IndexOf('<', StringComparison.Ordinal);
        while (position >= 0 && position + 1 < html.Length)
        {
            int resumeAt = position + 1;
            char next = html[position + 1];
            if (char.IsAsciiLetter(next))
            {
                resumeAt = HandleStartTag(html, position, rewriter, budgets, openTags);
            }
            else if (next == '/' && position + 2 < html.Length && char.IsAsciiLetter(html[position + 2]))
            {
                resumeAt = HandleEndTag(html, position, rewriter, budgets, openTags);
            }
            else if (next is '/' or '!' or '?')
            {
                resumeAt = HandleComment(html, position, rewriter, budgets);
            }
            else
            {
                rewriter.EncodeLessThan(position);
            }

            position = resumeAt < html.Length ? html.IndexOf('<', resumeAt) : -1;
        }

        return rewriter.ToResult();
    }

    private static int HandleStartTag(string html, int tagStart, Rewriter rewriter, Budgets budgets, List<string> openTags)
    {
        int nameEnd = FindTagNameEnd(html, tagStart + 1);
        string tagName = html[(tagStart + 1)..nameEnd].ToLowerInvariant();
        TagScan tag = ScanAttributes(html, tagStart, nameEnd);
        bool isNesting = !NotNesting.Contains(tagName);
        bool exceedsLimit = budgets.MarkupItems >= MaxMarkupItems
            || (isNesting && openTags.Count >= MaxOpenTags)
            || tag.AttributeCount > MaxAttributesPerTag
            || budgets.TagTextChars + tag.CountedChars > MaxTagTextChars
            || budgets.TagsInAttributeValues + tag.TagsInValues > MaxTagsInAttributeValues;
        if (exceedsLimit)
        {
            rewriter.Drop(tagStart, tag.End);
            return tag.End;
        }

        budgets.MarkupItems++;
        budgets.TagTextChars += tag.CountedChars;
        budgets.TagsInAttributeValues += tag.TagsInValues;
        if (isNesting)
        {
            openTags.Add(tagName);
        }

        foreach ((int start, int end) in tag.OversizedStyles ?? [])
        {
            rewriter.Drop(start, end);
        }

        return tag.End;
    }

    // Attributes of an end tag mean nothing, but the parser still reads them, so they are removed. A long list of tag
    // pairs ends here with bare text: once the start tags are over budget, their end tags soon are as well.
    private static int HandleEndTag(string html, int tagStart, Rewriter rewriter, Budgets budgets, List<string> openTags)
    {
        int nameEnd = FindTagNameEnd(html, tagStart + 2);
        TagScan tag = ScanAttributes(html, tagStart, nameEnd);
        if (budgets.EndTags >= MaxEndTags)
        {
            rewriter.Drop(tagStart, tag.End);
            return tag.End;
        }

        budgets.EndTags++;
        CloseTag(openTags, html[(tagStart + 2)..nameEnd].ToLowerInvariant());
        if (tag.AttributeCount > 0)
        {
            rewriter.Drop(tagStart, tag.End);
            CopyBareEndTag(html, tagStart, nameEnd, rewriter);
        }

        return tag.End;
    }

    private static void CopyBareEndTag(string html, int tagStart, int nameEnd, Rewriter rewriter)
    {
        rewriter.Insert(html[tagStart..nameEnd] + ">");
    }

    // Comments and everything the parser treats like one: up to "-->" or "--!>", other forms up to the next '>'.
    private static int HandleComment(string html, int start, Rewriter rewriter, Budgets budgets)
    {
        int end = FindCommentEnd(html, start);
        if (budgets.MarkupItems >= MaxMarkupItems)
        {
            rewriter.Drop(start, end);
        }
        else
        {
            budgets.MarkupItems++;
        }

        return end;
    }

    private static int FindCommentEnd(string html, int start)
    {
        if (string.CompareOrdinal(html, start, CommentStart, 0, CommentStart.Length) != 0)
        {
            int closing = html.IndexOf('>', start);
            return closing < 0 ? html.Length : closing + 1;
        }

        // Searched from behind "<!" because "<!-->" and "<!--->" are complete comments.
        int searchStart = start + 2;
        int normalEnd = html.IndexOf("-->", searchStart, StringComparison.Ordinal);
        int searchLimit = normalEnd < 0 ? html.Length : normalEnd;
        int bangEnd = html.IndexOf("--!>", searchStart, searchLimit - searchStart, StringComparison.Ordinal);
        if (bangEnd >= 0)
        {
            return bangEnd + 4;
        }

        return normalEnd < 0 ? html.Length : normalEnd + 3;
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

    // Per the tag name state of HTML: the name ends at whitespace, '/' or '>'.
    private static int FindTagNameEnd(string html, int nameStart)
    {
        int nameEnd = nameStart;
        while (nameEnd < html.Length && !IsHtmlWhitespace(html[nameEnd]) && html[nameEnd] is not ('/' or '>'))
        {
            nameEnd++;
        }

        return nameEnd;
    }

    private static bool IsHtmlWhitespace(char character) => character is ' ' or '\t' or '\n' or '\r' or '\f';

    private static int SkipWhitespace(string html, int position)
    {
        while (position < html.Length && IsHtmlWhitespace(html[position]))
        {
            position++;
        }

        return position;
    }

    // Reads the attributes after the tag name the way the HTML tokenizer does; a tag without an end reaches to the end of
    // the text, and the parser then ignores it as well.
    private static TagScan ScanAttributes(string html, int tagStart, int nameEnd)
    {
        var tag = new TagScan();
        int excludedChars = 0;
        int position = nameEnd;
        while (true)
        {
            while (position < html.Length && (IsHtmlWhitespace(html[position]) || html[position] == '/'))
            {
                position++;
            }

            if (position >= html.Length)
            {
                tag.End = html.Length;
                break;
            }

            if (html[position] == '>')
            {
                tag.End = position + 1;
                break;
            }

            int attributeStart = position;

            // A leading '=' belongs to the name.
            position++;
            while (position < html.Length && !IsHtmlWhitespace(html[position]) && html[position] is not ('/' or '>' or '='))
            {
                position++;
            }

            int attributeNameEnd = position;
            tag.AttributeCount++;
            position = SkipWhitespace(html, position);
            if (position >= html.Length || html[position] != '=')
            {
                continue;
            }

            position = SkipWhitespace(html, position + 1);
            (int valueStart, int valueEnd, position, bool isQuoted) = ReadAttributeValue(html, position);
            ReadOnlySpan<char> attributeName = html.AsSpan(attributeStart, attributeNameEnd - attributeStart);
            ReadOnlySpan<char> value = html.AsSpan(valueStart, valueEnd - valueStart);
            if (attributeName.Equals("src", StringComparison.OrdinalIgnoreCase) && StartsWithDataScheme(value))
            {
                excludedChars += value.Length;
                continue;
            }

            if (attributeName.Equals("style", StringComparison.OrdinalIgnoreCase) && IsOversizedStyle(value))
            {
                tag.OversizedStyles ??= [];
                tag.OversizedStyles.Add((attributeStart, position));
                excludedChars += position - attributeStart;
                continue;
            }

            if (isQuoted)
            {
                tag.TagsInValues += CountTagLikeText(value);
            }
        }

        tag.CountedChars = tag.End - tagStart - excludedChars;
        return tag;
    }

    // Returns the range of the value and the position behind it. A missing value before '>' is empty.
    private static (int ValueStart, int ValueEnd, int Next, bool IsQuoted) ReadAttributeValue(string html, int position)
    {
        if (position >= html.Length)
        {
            return (html.Length, html.Length, html.Length, false);
        }

        char first = html[position];
        if (first is '"' or '\'')
        {
            int closing = html.IndexOf(first, position + 1);
            return closing < 0
                ? (position + 1, html.Length, html.Length, true)
                : (position + 1, closing, closing + 1, true);
        }

        int end = position;
        while (end < html.Length && !IsHtmlWhitespace(html[end]) && html[end] != '>')
        {
            end++;
        }

        return (position, end, end, false);
    }

    private static bool StartsWithDataScheme(ReadOnlySpan<char> value)
    {
        return value.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOversizedStyle(ReadOnlySpan<char> value)
    {
        return value.Length > MaxStyleValueChars || value.Count(';') > MaxStyleDeclarations;
    }

    private static int CountTagLikeText(ReadOnlySpan<char> value)
    {
        int count = 0;
        int index = value.IndexOf('<');
        while (index >= 0 && index + 1 < value.Length)
        {
            if (char.IsAsciiLetter(value[index + 1]))
            {
                count++;
            }

            int next = value[(index + 1)..].IndexOf('<');
            index = next < 0 ? -1 : index + 1 + next;
        }

        return count;
    }

    /// <summary>Result of reading one tag.</summary>
    private sealed class TagScan
    {
        /// <summary>Position behind the tag.</summary>
        public int End { get; set; }

        public int AttributeCount { get; set; }

        /// <summary>Length of the tag without <c>data:</c> values of <c>src</c> and without oversized styles.</summary>
        public int CountedChars { get; set; }

        /// <summary>Tag-like text inside quoted values.</summary>
        public int TagsInValues { get; set; }

        /// <summary>Attribute ranges (name and value) to drop; null when there is none.</summary>
        public List<(int Start, int End)>? OversizedStyles { get; set; }
    }

    /// <summary>What the kept markup has used so far.</summary>
    private sealed class Budgets
    {
        public int MarkupItems { get; set; }

        public int EndTags { get; set; }

        public int TagTextChars { get; set; }

        public int TagsInAttributeValues { get; set; }
    }

    /// <summary>Copies the unchanged parts of the HTML; allocates only when something changes.</summary>
    private sealed class Rewriter(string html)
    {
        private StringBuilder? rewritten;
        private int copiedUpTo;

        /// <summary>Removes a range behind everything copied so far.</summary>
        /// <param name="start">First position to remove.</param>
        /// <param name="end">Position behind the range.</param>
        public void Drop(int start, int end)
        {
            CopyUpTo(start);
            copiedUpTo = end;
        }

        /// <summary>Adds text at the current position; call directly after <see cref="Drop"/>.</summary>
        /// <param name="text">Text to add.</param>
        public void Insert(string text)
        {
            rewritten!.Append(text);
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
