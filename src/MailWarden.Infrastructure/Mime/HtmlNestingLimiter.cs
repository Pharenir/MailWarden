using System.Collections.Frozen;
using System.Text;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// Result of <see cref="HtmlNestingLimiter.Limit"/>.
/// </summary>
/// <param name="Html">The rewritten HTML.</param>
/// <param name="WasReduced">
/// True when markup was dropped because of a limit. Removing raw-text elements does not count: they would be removed later
/// anyway, and nearly every newsletter has a <c>style</c> element.
/// </param>
internal readonly record struct LimitedHtml(string Html, bool WasReduced);

/// <summary>
/// Rewrites HTML before it is parsed so that a crafted mail cannot stall the reading pane, exhaust the memory or overflow
/// the stack. The cost of parser and sanitizer grows with the square of: the depth (100,000 nested <c>div</c> take more than
/// 20 s), the number of siblings, comments and attributes of a tag, the declarations of a style (100,000 take more than
/// 40 s), and the number of pieces a text is split into by end tags or stray <c>&lt;</c> characters. Open formatting elements
/// are copied into every following block (a 154 KB mail reached over 5 GB). The scan is linear; text outside of tags and
/// raw-text elements is never dropped.
/// </summary>
/// <remarks>
/// The scan can only stand in for the parser where both read the text the same way. Raw text is where they would differ:
/// a comment start or a quote inside <c>script</c> means nothing to the parser but would hide the tags behind it from the
/// scan. Therefore raw-text elements are removed here, so the parser never enters a raw-text state, and the rest is plain
/// HTML that the scan follows like the tokenizer. What cannot be foreseen here is covered by the time limit of the build
/// in the reading pane.
/// </remarks>
internal static class HtmlNestingLimiter
{
    /// <summary>Estimated depth at which start tags are dropped. Far above real mails.</summary>
    internal const int MaxOpenTags = 1024;

    /// <summary>Start tags and comments kept; the text of the rest stays visible without its markup.</summary>
    internal const int MaxMarkupItems = 20_000;

    /// <summary>End tags kept.</summary>
    internal const int MaxEndTags = MaxMarkupItems;

    /// <summary>Open formatting elements; the parser copies them into every following block.</summary>
    internal const int MaxOpenFormattingElements = 64;

    /// <summary>
    /// Estimate of the formatting elements the parser would copy: the open ones for each start tag. 64 open elements alone
    /// would still allow 18,000 blocks with a copy of each, over a million elements and 1 GB from a 154 KB mail.
    /// </summary>
    internal const int MaxCopiedFormattingElements = 100_000;

    /// <summary>A tag with more attributes is dropped.</summary>
    internal const int MaxAttributesPerTag = 256;

    /// <summary>A longer style value is dropped; the tag stays.</summary>
    internal const int MaxStyleValueChars = 64 * 1024;

    /// <summary>A style value with more declarations is dropped; the tag stays. The parser is quadratic in them.</summary>
    internal const int MaxStyleDeclarations = 256;

    /// <summary>Characters of all kept tags together, without <c>data:</c> values of <c>src</c>.</summary>
    internal const int MaxTagTextChars = 2_000_000;

    /// <summary>Every <c>&lt;</c> in the attribute values of kept tags together; a tag beyond this is dropped.</summary>
    internal const int MaxLessThanInAttributeValues = MaxOpenTags;

    /// <summary>A comment with more <c>&lt;</c> inside is not skipped but scanned like normal HTML.</summary>
    internal const int MaxLessThanInSkippedComment = 20_000;

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

    // Removed with their content up to the first matching end tag. The sanitizer would remove all of them anyway.
    private static readonly FrozenSet<string> RawTextElements = new[]
    {
        "script", "style", "title", "textarea", "iframe", "noembed", "noframes", "noscript", "svg", "math",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Also raw text for the parser (or wanted as such), but their content is shown as normal HTML: only the tag goes.
    private static readonly FrozenSet<string> RawTextTagsOnly = new[] { "xmp", "plaintext", "listing" }
        .ToFrozenSet(StringComparer.Ordinal);

    // The elements on the parser's list of active formatting elements.
    private static readonly FrozenSet<string> FormattingElements = new[]
    {
        "a", "b", "big", "code", "em", "font", "i", "nobr", "s", "small", "strike", "strong", "tt", "u",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Returns the HTML without raw-text elements, with stray <c>&lt;</c> encoded and within the limits.</summary>
    /// <param name="html">Raw HTML.</param>
    /// <returns>The rewritten HTML and whether a limit dropped anything.</returns>
    public static LimitedHtml Limit(string html)
    {
        var rewriter = new Rewriter(html);
        var state = new ScanState();
        int position = html.IndexOf('<', StringComparison.Ordinal);
        while (position >= 0 && position + 1 < html.Length)
        {
            int resumeAt = position + 1;
            char next = html[position + 1];
            if (char.IsAsciiLetter(next))
            {
                resumeAt = HandleStartTag(html, position, rewriter, state);
            }
            else if (next == '/' && position + 2 < html.Length && char.IsAsciiLetter(html[position + 2]))
            {
                resumeAt = HandleEndTag(html, position, rewriter, state);
            }
            else if (next is '/' or '!' or '?')
            {
                resumeAt = HandleComment(html, position, rewriter, state);
            }
            else
            {
                rewriter.EncodeLessThan(position);
            }

            position = resumeAt < html.Length ? html.IndexOf('<', resumeAt) : -1;
        }

        return new LimitedHtml(rewriter.ToResult(), state.WasReduced);
    }

    private static int HandleStartTag(string html, int tagStart, Rewriter rewriter, ScanState state)
    {
        int nameEnd = FindTagNameEnd(html, tagStart + 1);
        string tagName = html[(tagStart + 1)..nameEnd].ToLowerInvariant();
        TagScan tag = ScanAttributes(html, tagStart, nameEnd);
        if (RawTextElements.Contains(tagName))
        {
            int elementEnd = FindRawTextElementEnd(html, tag.End, tagName);
            rewriter.Drop(tagStart, elementEnd);
            return elementEnd;
        }

        if (RawTextTagsOnly.Contains(tagName))
        {
            rewriter.Drop(tagStart, tag.End);
            return tag.End;
        }

        bool isNesting = !NotNesting.Contains(tagName);
        bool isFormatting = FormattingElements.Contains(tagName);
        bool exceedsLimit = state.MarkupItems >= MaxMarkupItems
            || (isNesting && state.OpenTags.Count >= MaxOpenTags)
            || (isFormatting && state.OpenFormattingTotal >= MaxOpenFormattingElements)
            || state.CopiedFormatting + state.OpenFormattingTotal > MaxCopiedFormattingElements
            || tag.AttributeCount > MaxAttributesPerTag
            || state.TagTextChars + tag.CountedChars > MaxTagTextChars
            || state.LessThanInValues + tag.LessThanInValues > MaxLessThanInAttributeValues;
        if (exceedsLimit)
        {
            rewriter.Drop(tagStart, tag.End);
            state.WasReduced = true;
            return tag.End;
        }

        state.MarkupItems++;
        state.CopiedFormatting += state.OpenFormattingTotal;
        state.TagTextChars += tag.CountedChars;
        state.LessThanInValues += tag.LessThanInValues;
        if (isNesting)
        {
            state.OpenTags.Add(tagName);
        }

        if (isFormatting)
        {
            state.OpenFormatting[tagName] = state.OpenFormatting.GetValueOrDefault(tagName) + 1;
            state.OpenFormattingTotal++;
        }

        foreach ((int start, int end) in tag.OversizedStyles ?? [])
        {
            rewriter.Drop(start, end);
            state.WasReduced = true;
        }

        return tag.End;
    }

    // Attributes of an end tag mean nothing, but the parser still reads them, so they are removed. A long list of tag
    // pairs ends here with bare text: once the start tags are over budget, their end tags soon are as well.
    private static int HandleEndTag(string html, int tagStart, Rewriter rewriter, ScanState state)
    {
        int nameEnd = FindTagNameEnd(html, tagStart + 2);
        string tagName = html[(tagStart + 2)..nameEnd].ToLowerInvariant();
        TagScan tag = ScanAttributes(html, tagStart, nameEnd);
        if (state.EndTags >= MaxEndTags)
        {
            rewriter.Drop(tagStart, tag.End);
            state.WasReduced = true;
            return tag.End;
        }

        state.EndTags++;
        CloseTag(state.OpenTags, tagName);

        // Only its own end tag closes a formatting element here, whatever the parser does with other end tags.
        if (state.OpenFormatting.GetValueOrDefault(tagName) > 0)
        {
            state.OpenFormatting[tagName]--;
            state.OpenFormattingTotal--;
        }

        if (tag.AttributeCount > 0)
        {
            rewriter.Drop(tagStart, tag.End);
            rewriter.Insert(html[tagStart..nameEnd] + ">");
        }

        return tag.End;
    }

    // Comments and everything the parser treats like one: up to "-->" or "--!>", other forms up to the next '>'. A comment
    // full of '<' is suspicious, because everything behind a disagreement about its end would be unseen: it stays in the
    // text, but its inside is scanned like normal HTML.
    private static int HandleComment(string html, int start, Rewriter rewriter, ScanState state)
    {
        bool isComment = string.CompareOrdinal(html, start, CommentStart, 0, CommentStart.Length) == 0;
        if (isComment && start < state.ScanCommentsUntil)
        {
            return CountCommentOrDrop(start, state.ScanCommentsUntil, start + CommentStart.Length, rewriter, state);
        }

        int end = FindCommentEnd(html, start, isComment);
        if (isComment && HasManyLessThan(html.AsSpan(start + 1, end - start - 1)))
        {
            state.ScanCommentsUntil = end;
            return CountCommentOrDrop(start, end, start + CommentStart.Length, rewriter, state);
        }

        return CountCommentOrDrop(start, end, end, rewriter, state);
    }

    private static int CountCommentOrDrop(int start, int end, int resumeAt, Rewriter rewriter, ScanState state)
    {
        if (state.MarkupItems >= MaxMarkupItems)
        {
            rewriter.Drop(start, end);
            state.WasReduced = true;
            return end;
        }

        state.MarkupItems++;
        return resumeAt;
    }

    private static bool HasManyLessThan(ReadOnlySpan<char> text)
    {
        int count = 0;
        int index = text.IndexOf('<');
        while (index >= 0)
        {
            if (++count > MaxLessThanInSkippedComment)
            {
                return true;
            }

            text = text[(index + 1)..];
            index = text.IndexOf('<');
        }

        return false;
    }

    private static int FindCommentEnd(string html, int start, bool isComment)
    {
        if (!isComment)
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

    // The position behind the first matching end tag; without one the rest of the input, as the parser would read it.
    private static int FindRawTextElementEnd(string html, int contentStart, string tagName)
    {
        string endTagStart = "</" + tagName;
        int candidate = html.IndexOf(endTagStart, contentStart, StringComparison.OrdinalIgnoreCase);
        while (candidate >= 0)
        {
            int nameEnd = candidate + endTagStart.Length;
            if (nameEnd < html.Length && (IsHtmlWhitespace(html[nameEnd]) || html[nameEnd] is '/' or '>'))
            {
                return ScanAttributes(html, candidate, nameEnd).End;
            }

            candidate = html.IndexOf(endTagStart, nameEnd, StringComparison.OrdinalIgnoreCase);
        }

        return html.Length;
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
            (int valueStart, int valueEnd, position) = ReadAttributeValue(html, position);
            ReadOnlySpan<char> attributeName = html.AsSpan(attributeStart, attributeNameEnd - attributeStart);
            ReadOnlySpan<char> value = html.AsSpan(valueStart, valueEnd - valueStart);
            if (attributeName.Equals("style", StringComparison.OrdinalIgnoreCase) && IsOversizedStyle(value))
            {
                tag.OversizedStyles ??= [];
                tag.OversizedStyles.Add((attributeStart, position));
                excludedChars += position - attributeStart;
                continue;
            }

            tag.LessThanInValues += value.Count('<');
            if (attributeName.Equals("src", StringComparison.OrdinalIgnoreCase) && StartsWithDataScheme(value))
            {
                excludedChars += value.Length;
            }
        }

        tag.CountedChars = tag.End - tagStart - excludedChars;
        return tag;
    }

    // Returns the range of the value and the position behind it. A missing value before '>' is empty.
    private static (int ValueStart, int ValueEnd, int Next) ReadAttributeValue(string html, int position)
    {
        if (position >= html.Length)
        {
            return (html.Length, html.Length, html.Length);
        }

        char first = html[position];
        if (first is '"' or '\'')
        {
            int closing = html.IndexOf(first, position + 1);
            return closing < 0
                ? (position + 1, html.Length, html.Length)
                : (position + 1, closing, closing + 1);
        }

        int end = position;
        while (end < html.Length && !IsHtmlWhitespace(html[end]) && html[end] != '>')
        {
            end++;
        }

        return (position, end, end);
    }

    private static bool StartsWithDataScheme(ReadOnlySpan<char> value)
    {
        return value.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOversizedStyle(ReadOnlySpan<char> value)
    {
        return value.Length > MaxStyleValueChars || value.Count(';') > MaxStyleDeclarations;
    }

    /// <summary>Result of reading one tag.</summary>
    private sealed class TagScan
    {
        /// <summary>Position behind the tag.</summary>
        public int End { get; set; }

        public int AttributeCount { get; set; }

        /// <summary>Length of the tag without <c>data:</c> values of <c>src</c> and without oversized styles.</summary>
        public int CountedChars { get; set; }

        /// <summary>Number of <c>&lt;</c> inside the values.</summary>
        public int LessThanInValues { get; set; }

        /// <summary>Attribute ranges (name and value) to drop; null when there is none.</summary>
        public List<(int Start, int End)>? OversizedStyles { get; set; }
    }

    /// <summary>What the kept markup has used so far.</summary>
    private sealed class ScanState
    {
        public List<string> OpenTags { get; } = [];

        public Dictionary<string, int> OpenFormatting { get; } = new(StringComparer.Ordinal);

        public int OpenFormattingTotal { get; set; }

        public int CopiedFormatting { get; set; }

        public int MarkupItems { get; set; }

        public int EndTags { get; set; }

        public int TagTextChars { get; set; }

        public int LessThanInValues { get; set; }

        /// <summary>Comment starts before this position are not skipped, see <see cref="HandleComment"/>.</summary>
        public int ScanCommentsUntil { get; set; }

        public bool WasReduced { get; set; }
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
