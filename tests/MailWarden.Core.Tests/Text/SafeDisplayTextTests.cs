using MailWarden.Core.Text;

namespace MailWarden.Core.Tests.Text;

/// <summary>AP-04a case 1 (D1): <see cref="SafeDisplayText.Clean"/>.</summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "smoke")]
public sealed class SafeDisplayTextTests
{
    // Pure, synchronous logic: no per-test timeout needed.

    // Case 1
    [Fact]
    public void Plain_letters_digits_and_punctuation_are_unchanged()
    {
        Assert.Equal("Invoice 2026: Sender_A, ok!", SafeDisplayText.Clean("Invoice 2026: Sender_A, ok!"));
    }

    // Case 1
    [Fact]
    public void Empty_string_stays_empty()
    {
        Assert.Equal(string.Empty, SafeDisplayText.Clean(string.Empty));
    }

    // Case 1
    [Fact]
    public void Emoji_and_non_ascii_letters_are_unchanged()
    {
        const string text = "Grüße \U0001F600 日本語";

        Assert.Equal(text, SafeDisplayText.Clean(text));
    }

    // Case 1
    [Theory]
    [InlineData('\u202E')] // RLO
    [InlineData('\u202A')]
    [InlineData('\u202B')]
    [InlineData('\u202C')]
    [InlineData('\u202D')]
    [InlineData('\u2066')] // LRI
    [InlineData('\u2067')]
    [InlineData('\u2068')]
    [InlineData('\u2069')]
    [InlineData('\u200E')]
    [InlineData('\u200F')]
    [InlineData('\u061C')]
    public void Each_bidi_control_is_removed(char control)
    {
        Assert.Equal("abcd", SafeDisplayText.Clean($"ab{control}cd"));
    }

    // Case 1
    [Fact]
    public void Rtlo_spoofing_of_a_file_extension_loses_the_control_but_keeps_the_letters()
    {
        Assert.Equal("invoicecod.exe", SafeDisplayText.Clean("invoice\u202Ecod.exe"));
    }

    // Case 1
    [Theory]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    public void Cr_lf_and_tab_each_become_one_space(string input)
    {
        Assert.Equal("a b", SafeDisplayText.Clean(input));
    }

    // Case 1
    [Fact]
    public void Crlf_becomes_two_spaces_one_per_character_and_nothing_is_trimmed()
    {
        Assert.Equal(" a  b ", SafeDisplayText.Clean("\ta\r\nb\n"));
    }

    // Case 1
    [Theory]
    [InlineData('\u0007')] // BEL
    [InlineData('\u0000')]
    [InlineData('\u001B')] // ESC
    [InlineData('\u007F')] // DEL (Cc)
    [InlineData('\u0085')] // NEL (Cc)
    [InlineData('\u009F')] // C1 control
    public void Other_control_characters_are_removed(char control)
    {
        Assert.Equal("xy", SafeDisplayText.Clean($"x{control}y"));
    }

    // Case 1: positive control around the ranges; neighbours of the removed ranges stay.
    [Theory]
    [InlineData('\u2065')] // unassigned, between U+2064 and U+2066
    [InlineData('\u202F')] // narrow no-break space, right after U+202E
    [InlineData('\u200C')] // zero width non-joiner stays (scripts)
    [InlineData('\u200D')] // zero width joiner stays (emoji sequences)
    [InlineData('\u200A')] // hair space, right before U+200B
    [InlineData('\u00A0')]
    [InlineData('\u00AC')] // right before the soft hyphen
    [InlineData('\u00AE')] // right after the soft hyphen
    [InlineData('\u205F')] // medium mathematical space, right before U+2060
    [InlineData('\uFEFE')] // right before the byte order mark
    public void Neighbours_of_the_removed_ranges_stay(char kept)
    {
        Assert.Equal($"a{kept}b", SafeDisplayText.Clean($"a{kept}b"));
    }

    // Case 61 (K14): invisible and separator characters are removed or turned into a space.
    [Theory]
    [InlineData('\u00AD')] // soft hyphen
    [InlineData('\u200B')] // zero width space
    [InlineData('\u2060')] // word joiner
    [InlineData('\u2061')]
    [InlineData('\u2062')]
    [InlineData('\u2063')]
    [InlineData('\u2064')]
    [InlineData('\u206A')]
    [InlineData('\u206B')]
    [InlineData('\u206C')]
    [InlineData('\u206D')]
    [InlineData('\u206E')]
    [InlineData('\u206F')]
    [InlineData('\uFEFF')] // byte order mark / zero width no-break space
    public void Invisible_format_characters_are_removed(char invisible)
    {
        Assert.Equal("abcd", SafeDisplayText.Clean($"ab{invisible}cd"));
    }

    // Case 61
    [Theory]
    [InlineData('\u2028')] // line separator
    [InlineData('\u2029')] // paragraph separator
    public void Line_and_paragraph_separators_become_one_space_each(char separator)
    {
        Assert.Equal("a b", SafeDisplayText.Clean($"a{separator}b"));
        Assert.Equal("a  b", SafeDisplayText.Clean($"a{separator}{separator}b"));
    }

    // Case 61: tag characters U+E0000-U+E007F (surrogate pairs) are removed, the neighbours are not.
    [Theory]
    [InlineData("\U000E0000")]
    [InlineData("\U000E0041")]
    [InlineData("\U000E0020")]
    [InlineData("\U000E007F")]
    public void Tag_characters_are_removed(string tagCharacter)
    {
        Assert.Equal("abcd", SafeDisplayText.Clean($"ab{tagCharacter}cd"));
    }

    // Case 61 positive control: just outside the tag range and emoji sequences with ZWJ stay.
    [Fact]
    public void Characters_next_to_the_tag_range_and_emoji_joiner_sequences_stay()
    {
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";

        Assert.Equal(family, SafeDisplayText.Clean(family));
        Assert.Equal("a\U000E0080b", SafeDisplayText.Clean("a\U000E0080b"));
        Assert.Equal("a\U000E0100b", SafeDisplayText.Clean("a\U000E0100b"));
    }

    // Case 61: tag characters hidden inside a longer text do not break the text around them.
    [Fact]
    public void Hidden_tag_text_between_visible_words_vanishes()
    {
        Assert.Equal("pay now", SafeDisplayText.Clean("pay \U000E0041\U000E0042\U000E0043now"));
    }

    // ---- Shorten (Case 53, K7) -----------------------------------------------------------------------------

    private const string Ellipsis = "\u2026";

    // Case 53
    [Fact]
    public void Shorten_cleans_collapses_whitespace_runs_and_trims()
    {
        Assert.Equal("a b c", SafeDisplayText.Shorten("  a \t\r\n  b\u202E   c  \n", 100));
        Assert.Equal(string.Empty, SafeDisplayText.Shorten("   \t\r\n  ", 100));
        Assert.Equal(string.Empty, SafeDisplayText.Shorten(string.Empty, 100));
    }

    // Case 53
    [Fact]
    public void Shorten_text_exactly_at_the_limit_is_unchanged()
    {
        string text = new('x', 50);

        Assert.Equal(text, SafeDisplayText.Shorten(text, 50));
    }

    // Case 53: the ellipsis is appended when something was cut; whether it counts into the limit is not fixed.
    [Fact]
    public void Shorten_text_one_over_the_limit_is_cut_and_ends_with_an_ellipsis()
    {
        string text = new string('x', 50) + "y";

        string result = SafeDisplayText.Shorten(text, 50);

        Assert.EndsWith(Ellipsis, result, StringComparison.Ordinal);
        string kept = result[..^1];
        Assert.True(kept.Length is 49 or 50, $"kept {kept.Length} characters");
        Assert.StartsWith(kept, text, StringComparison.Ordinal);
        Assert.DoesNotContain("y", result, StringComparison.Ordinal);
    }

    // Case 53
    [Fact]
    public void Shorten_very_long_text_stays_short()
    {
        string result = SafeDisplayText.Shorten(new string('x', 400_000), 500);

        Assert.True(result.Length <= 501, $"length {result.Length}");
        Assert.EndsWith(Ellipsis, result, StringComparison.Ordinal);
    }

    // Case 53: a surrogate pair at the cut is never split, for every position around the cut.
    [Theory]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(51)]
    public void Shorten_never_leaves_a_lone_surrogate_at_the_cut(int prefixLength)
    {
        string text = new string('x', prefixLength) + "\U0001F600\U0001F600\U0001F600" + new string('z', 20);

        string result = SafeDisplayText.Shorten(text, 50);

        for (int index = 0; index < result.Length; index++)
        {
            if (char.IsHighSurrogate(result[index]))
            {
                Assert.True(index + 1 < result.Length && char.IsLowSurrogate(result[index + 1]), $"lone high surrogate at {index}");
                index++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(result[index]), $"lone low surrogate at {index}");
            }
        }

        Assert.EndsWith(Ellipsis, result, StringComparison.Ordinal);
    }

    // Case 53: text that is short only after collapsing is not cut.
    [Fact]
    public void Shorten_measures_after_collapsing_whitespace()
    {
        string text = "a" + new string(' ', 10_000) + "b";

        Assert.Equal("a b", SafeDisplayText.Shorten(text, 10));
    }

    // Case 53
    [Fact]
    public void Shorten_null_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SafeDisplayText.Shorten(null!, 10));
    }

    // Case 1
    [Fact]
    public void Mixed_input_combines_all_rules()
    {
        Assert.Equal("Re: pay now \U0001F600 ok", SafeDisplayText.Clean("Re:\u202E pay\tnow \U0001F600\u0007\u2066 ok"));
    }

    // Case 1
    [Fact]
    public void Null_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SafeDisplayText.Clean(null!));
    }
}
