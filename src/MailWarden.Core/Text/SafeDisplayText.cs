using System.Text;

namespace MailWarden.Core.Text;

/// <summary>
/// Cleans untrusted text (server and mail data) before it is displayed: control and line characters could break the
/// layout, bidi controls and invisible characters could disguise text, e.g. a file extension.
/// </summary>
public static class SafeDisplayText
{
    private const string Ellipsis = "…";

    // U+E0000–U+E007F (tag characters) in UTF-16: one high surrogate and a range of low surrogates.
    private const char TagCharacterHighSurrogate = '\uDB40';
    private const char TagCharacterLowSurrogateFirst = '\uDC00';
    private const char TagCharacterLowSurrogateLast = '\uDC7F';

    /// <summary>
    /// Removes control characters, bidi controls and invisible characters from untrusted text. CR, LF, TAB, U+2028 and
    /// U+2029 become one space each. Removed: other Cc characters, U+00AD, U+200B, U+200E, U+200F, U+061C, U+202A–U+202E,
    /// U+2060–U+2064, U+2066–U+2069, U+206A–U+206F, U+FEFF, U+E0000–U+E007F. U+200C and U+200D stay, because scripts and
    /// emoji sequences need them. Other text is unchanged, no trimming.
    /// </summary>
    /// <param name="text">Text to clean.</param>
    /// <returns>The cleaned text; the same instance when nothing had to change.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Most texts are clean: no allocation for them.
        int firstChange = FindFirstChange(text);
        if (firstChange < 0)
        {
            return text;
        }

        var cleaned = new StringBuilder(text.Length);
        cleaned.Append(text, 0, firstChange);
        for (int index = firstChange; index < text.Length; index++)
        {
            char character = text[index];
            if (IsTagCharacterAt(text, index))
            {
                index++;
            }
            else if (BecomesSpace(character))
            {
                cleaned.Append(' ');
            }
            else if (!IsRemoved(character))
            {
                cleaned.Append(character);
            }
        }

        return cleaned.ToString();
    }

    /// <summary>
    /// Prepares text of unbounded length for one display line: cleans it (<see cref="Clean"/>), collapses runs of whitespace
    /// to one space, trims, cuts without splitting a surrogate pair and appends "…" when cut.
    /// </summary>
    /// <param name="text">Text to shorten.</param>
    /// <param name="maxCharacters">Maximum length before the "…", greater than 0.</param>
    /// <returns>The shortened text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCharacters"/> is not greater than 0.</exception>
    public static string Shorten(string text, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);

        string cleaned = Clean(text);
        var collapsed = new StringBuilder(Math.Min(cleaned.Length, maxCharacters));
        bool isSpacePending = false;
        foreach (char character in cleaned)
        {
            if (char.IsWhiteSpace(character))
            {
                isSpacePending = true;
                continue;
            }

            if (isSpacePending && collapsed.Length > 0)
            {
                collapsed.Append(' ');
            }

            isSpacePending = false;
            collapsed.Append(character);

            // One character beyond the limit is enough to know that the text gets cut.
            if (collapsed.Length > maxCharacters)
            {
                break;
            }
        }

        if (collapsed.Length <= maxCharacters)
        {
            return collapsed.ToString();
        }

        int cutLength = char.IsHighSurrogate(collapsed[maxCharacters - 1]) ? maxCharacters - 1 : maxCharacters;
        return collapsed.ToString(0, cutLength) + Ellipsis;
    }

    private static int FindFirstChange(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (BecomesSpace(text[index]) || IsRemoved(text[index]) || IsTagCharacterAt(text, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool BecomesSpace(char character)
    {
        return character is '\r' or '\n' or '\t' or '\u2028' or '\u2029';
    }

    // char.IsControl is exactly the Unicode category Cc.
    private static bool IsRemoved(char character)
    {
        return char.IsControl(character)
            || character is '\u00AD' or '\u061C' or '\u200B' or '\u200E' or '\u200F' or '\uFEFF'
            || character is >= '\u202A' and <= '\u202E'
            || character is >= '\u2060' and <= '\u2064'
            || character is >= '\u2066' and <= '\u206F';
    }

    private static bool IsTagCharacterAt(string text, int index)
    {
        return text[index] == TagCharacterHighSurrogate
            && index + 1 < text.Length
            && text[index + 1] is >= TagCharacterLowSurrogateFirst and <= TagCharacterLowSurrogateLast;
    }
}
