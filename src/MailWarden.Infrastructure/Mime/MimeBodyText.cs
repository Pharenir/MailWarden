using System.Collections.Frozen;
using System.Text;
using MimeKit;
using MimeKit.Text;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// Extracts plain body text from a parsed message for search and preview.
/// </summary>
public static class MimeBodyText
{
    // Content of these elements is never visible text.
    private static readonly FrozenSet<string> SkippedElementNames =
        new[] { "script", "style" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Inline elements do not separate words ("<b>Mail</b>Warden" stays one word); every other tag acts as whitespace.
    private static readonly FrozenSet<string> InlineElementNames = new[]
    {
        "a", "abbr", "b", "bdi", "bdo", "big", "cite", "code", "data", "dfn", "em", "font", "i", "kbd", "mark", "q",
        "s", "samp", "small", "span", "strike", "strong", "sub", "sup", "time", "tt", "u", "var", "wbr",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Plain text of the message: <see cref="MimeMessage.TextBody"/> when it contains non-whitespace, otherwise
    /// <see cref="MimeMessage.HtmlBody"/> converted to text (script/style content dropped, tags removed, entities
    /// decoded), otherwise "". Whitespace runs collapse to one space; the result is trimmed and cut to
    /// <paramref name="maxChars"/> (one character less when the cut would split a surrogate pair).
    /// </summary>
    /// <param name="message">Parsed message.</param>
    /// <param name="maxChars">Maximum length of the result, greater than 0.</param>
    /// <returns>The collapsed, trimmed and cut text; "" when the message has no text body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxChars"/> is not greater than 0.</exception>
    public static string Extract(MimeMessage message, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        // Plain text wins when it has content; HTML-only messages (or empty text parts) fall back to the HTML part.
        string? textBody = message.TextBody;
        if (textBody is not null && ContainsNonWhitespace(textBody))
        {
            var textWriter = new CollapsingWriter(maxChars);
            textWriter.Append(textBody);
            return textWriter.ToText();
        }

        string? htmlBody = message.HtmlBody;
        if (htmlBody is null)
        {
            return string.Empty;
        }

        var htmlWriter = new CollapsingWriter(maxChars);
        AppendHtmlText(htmlBody, htmlWriter);
        return htmlWriter.ToText();
    }

    /// <summary>
    /// Cuts an already collapsed text to <paramref name="maxChars"/> the same way <see cref="Extract"/> does, so a
    /// shorter preview can be derived from one long extraction.
    /// </summary>
    /// <param name="text">Result of <see cref="Extract"/>.</param>
    /// <param name="maxChars">Maximum length, greater than 0.</param>
    /// <returns>The cut text.</returns>
    internal static string Cut(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        if (text.Length <= maxChars)
        {
            return text;
        }

        int length = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..length];
    }

    // Walks the HTML tokens: text data is appended, tags separate words (except inline ones), the content of
    // script/style elements, comments, CDATA and doctype are dropped. Stops once the writer is full.
    private static void AppendHtmlText(string html, CollapsingWriter writer)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        string? skippedElementName = null;

        while (!writer.IsFull && tokenizer.ReadNextToken(out HtmlToken? token))
        {
            switch (token)
            {
                case HtmlTagToken tag when skippedElementName is not null:
                    if (tag.IsEndTag && string.Equals(tag.Name, skippedElementName, StringComparison.OrdinalIgnoreCase))
                    {
                        skippedElementName = null;
                        writer.AppendSeparator();
                    }

                    break;

                case HtmlTagToken tag:
                    if (!tag.IsEndTag && !tag.IsEmptyElement && SkippedElementNames.Contains(tag.Name))
                    {
                        skippedElementName = tag.Name;
                    }

                    if (!InlineElementNames.Contains(tag.Name))
                    {
                        writer.AppendSeparator();
                    }

                    break;

                case HtmlDataToken data when token.Kind == HtmlTokenKind.Data && skippedElementName is null:
                    writer.Append(data.Data);
                    break;

                default:
                    // Script data, comments, CDATA, doctype and text inside skipped elements.
                    break;
            }
        }
    }

    private static bool ContainsNonWhitespace(string text)
    {
        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the result incrementally: leading whitespace is dropped, every whitespace run becomes one space that is
    /// only written before the next non-whitespace character (so the text is trimmed), and input stops counting once
    /// the cut length is reached.
    /// </summary>
    private sealed class CollapsingWriter(int maxChars)
    {
        private readonly StringBuilder text = new();
        private bool pendingSpace;

        /// <summary>
        /// True when further input cannot change the cut result: the first <c>maxChars</c> characters are fixed and one
        /// more character is known (needed to decide a surrogate pair at the cut).
        /// </summary>
        public bool IsFull => text.Length > maxChars;

        public void Append(string value)
        {
            foreach (char character in value)
            {
                if (IsFull)
                {
                    return;
                }

                if (char.IsWhiteSpace(character))
                {
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace && text.Length > 0)
                {
                    text.Append(' ');
                }

                pendingSpace = false;
                text.Append(character);
            }
        }

        /// <summary>Marks a word boundary (block tag) without writing anything yet.</summary>
        public void AppendSeparator() => pendingSpace = true;

        public string ToText() => Cut(text.ToString(), maxChars);
    }
}
