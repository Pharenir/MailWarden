using System.Text;

namespace MailWarden.Core.Text;

/// <summary>
/// Cleans untrusted text (server and mail data) before it is displayed: control characters could break the layout and
/// bidi controls could disguise text, e.g. a file extension.
/// </summary>
public static class SafeDisplayText
{
    /// <summary>
    /// Removes control characters and bidi controls from untrusted text. CR, LF and TAB become one space each. Removed:
    /// other Cc characters, U+200E, U+200F, U+061C, U+202A–U+202E, U+2066–U+2069. Other text is unchanged, no trimming.
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
            if (IsLineOrTab(character))
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

    private static int FindFirstChange(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsControl(text[index]) || IsRemoved(text[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsLineOrTab(char character) => character is '\r' or '\n' or '\t';

    // char.IsControl is exactly the Unicode category Cc.
    private static bool IsRemoved(char character)
    {
        return char.IsControl(character)
            || character is '‎' or '‏' or '؜'
            || character is >= '‪' and <= '‮'
            || character is >= '⁦' and <= '⁩';
    }
}
