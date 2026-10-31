using System.Text;

namespace MailWarden.Core.Classification;

/// <summary>Kind of a lexical token of an Authentication-Results header value.</summary>
internal enum HeaderTokenKind
{
    /// <summary>Run of ordinary characters, or the content of a quoted string.</summary>
    Word,

    /// <summary>'='.</summary>
    EqualsSign,

    /// <summary>';'.</summary>
    Semicolon,
}

/// <summary>One lexical token.</summary>
/// <param name="Kind">Token kind.</param>
/// <param name="Text">Word text (empty for '=' and ';').</param>
/// <param name="IsJoinedToPrevious">True when no whitespace, control character or comment separates this token from
/// the previous one (e.g. the quoted string and the '@domain' in <c>"x"@a.test</c>). The parser joins such tokens into
/// one property value.</param>
/// <param name="IsQuoted">True for a Word that came from a quoted string ('@' inside it never separates a domain).</param>
internal readonly record struct HeaderToken(HeaderTokenKind Kind, string Text, bool IsJoinedToPrevious, bool IsQuoted = false);

/// <summary>
/// Hand-written single-pass tokenizer (no regex, linear time) for untrusted header values. Comments "(...)" are
/// dropped (nested, with backslash escapes), quoted strings become one Word with escapes resolved, whitespace and
/// control characters (incl. folded line breaks) separate tokens. Unterminated comments/quotes end at the input end.
/// </summary>
internal static class AuthenticationResultsTokenizer
{
    /// <summary>Splits a header value into tokens.</summary>
    /// <param name="headerValue">Raw header value (not null).</param>
    /// <returns>Tokens in input order.</returns>
    public static List<HeaderToken> Tokenize(string headerValue)
    {
        var tokens = new List<HeaderToken>();
        var currentWord = new StringBuilder();
        var currentWordIsJoined = false;

        // True at the start and after whitespace/comments: the next token is not joined to the previous one.
        var separatorPending = true;
        var position = 0;

        while (position < headerValue.Length)
        {
            var character = headerValue[position];
            if (character == '(')
            {
                FlushWord(tokens, currentWord, currentWordIsJoined);
                position = SkipComment(headerValue, position);
                separatorPending = true;
            }
            else if (character == '"')
            {
                FlushWord(tokens, currentWord, currentWordIsJoined);
                position = ReadQuotedString(headerValue, position, out var quotedText);
                tokens.Add(new HeaderToken(HeaderTokenKind.Word, quotedText, !separatorPending, IsQuoted: true));
                separatorPending = false;
            }
            else if (character == ';' || character == '=')
            {
                FlushWord(tokens, currentWord, currentWordIsJoined);
                var kind = character == ';' ? HeaderTokenKind.Semicolon : HeaderTokenKind.EqualsSign;
                tokens.Add(new HeaderToken(kind, string.Empty, !separatorPending));
                separatorPending = false;
                position++;
            }
            else if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                FlushWord(tokens, currentWord, currentWordIsJoined);
                separatorPending = true;
                position++;
            }
            else
            {
                if (currentWord.Length == 0)
                {
                    currentWordIsJoined = !separatorPending;
                    separatorPending = false;
                }

                currentWord.Append(character);
                position++;
            }
        }

        FlushWord(tokens, currentWord, currentWordIsJoined);
        return tokens;
    }

    private static void FlushWord(List<HeaderToken> tokens, StringBuilder currentWord, bool isJoinedToPrevious)
    {
        if (currentWord.Length > 0)
        {
            tokens.Add(new HeaderToken(HeaderTokenKind.Word, currentWord.ToString(), isJoinedToPrevious));
            currentWord.Clear();
        }
    }

    // position points at '('; returns the index after the matching ')' or the input length.
    private static int SkipComment(string text, int position)
    {
        var depth = 0;
        while (position < text.Length)
        {
            var character = text[position];
            if (character == '\\')
            {
                position += 2;
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return position + 1;
                }
            }

            position++;
        }

        return text.Length;
    }

    // position points at the opening '"'; returns the index after the closing '"' or the input length.
    private static int ReadQuotedString(string text, int position, out string content)
    {
        var builder = new StringBuilder();
        position++;
        while (position < text.Length)
        {
            var character = text[position];
            if (character == '\\' && position + 1 < text.Length)
            {
                builder.Append(text[position + 1]);
                position += 2;
                continue;
            }

            if (character == '"')
            {
                content = builder.ToString();
                return position + 1;
            }

            builder.Append(character);
            position++;
        }

        content = builder.ToString();
        return text.Length;
    }
}
